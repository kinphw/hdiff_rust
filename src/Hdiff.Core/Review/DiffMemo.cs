using Hdiff.Core.Diff;

namespace Hdiff.Core.Review;

/// <summary>Which column of the comparison a memo is talking about.</summary>
public enum DiffMemoSide
{
    Old,
    New,
}

/// <summary>
/// Where a review memo is attached. <see cref="RowIndex"/> is a position in the
/// current <see cref="DocumentDiff.Rows"/>, so it is only valid for one
/// comparison run. The paragraph texts are kept so the memo can be re-attached
/// after the same document pair is compared again with different options.
/// </summary>
public sealed record DiffMemoAnchor(
    int RowIndex,
    DiffChangeKind Kind,
    string? OldText,
    string? NewText,
    DiffMemoSide Side)
{
    public const int OrphanedRowIndex = -1;

    public bool IsOrphaned => RowIndex < 0;

    /// <summary>The paragraph a reader sees the memo next to.</summary>
    public string Quote => Side == DiffMemoSide.Old
        ? OldText ?? NewText ?? string.Empty
        : NewText ?? OldText ?? string.Empty;

    /// <summary>
    /// A memo written on an empty cell would have nothing to point at, so a
    /// row that exists on one side only always takes that side.
    /// </summary>
    public static DiffMemoSide ResolveSide(DiffRow row, DiffMemoSide requested)
    {
        if (row.OldText is null) return DiffMemoSide.New;
        if (row.NewText is null) return DiffMemoSide.Old;
        return requested;
    }
}

/// <summary>
/// A reviewer note attached to one comparison row, in the spirit of the memo
/// features of Word, Excel and 한글. Memos never touch the compared documents;
/// they live in the comparison session and in the exported HTML.
/// </summary>
public sealed record DiffMemo(
    string Id,
    DiffMemoAnchor Anchor,
    string Author,
    string Text,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt)
{
    public DateTimeOffset LastEditedAt => UpdatedAt ?? CreatedAt;
    public IReadOnlyList<DiffMemoReply> Replies { get; init; } = Array.Empty<DiffMemoReply>();
}

public sealed record DiffMemoReply(string Id, string Author, string Text, DateTimeOffset CreatedAt);

/// <summary>
/// Holds the review memos of the current comparison. Row indices shift whenever
/// a comparison option changes, so the store re-anchors by paragraph text
/// instead of trusting the stored index.
/// </summary>
public sealed class DiffMemoStore
{
    private static readonly char KeySeparator = (char)1;
    private static readonly string NoText = ((char)2).ToString();
    private readonly List<DiffMemo> _memos = new();

    /// <summary>Raised after any add, edit, delete, clear or re-anchor.</summary>
    public event EventHandler? Changed;

    /// <summary>Memos in reading order; memos that lost their row come last.</summary>
    public IReadOnlyList<DiffMemo> Memos => _memos;

    public int Count => _memos.Count;

    public bool HasMemos => _memos.Count > 0;

    public DiffMemo Add(
        DocumentDiff diff,
        int rowIndex,
        DiffMemoSide side,
        string author,
        string text,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(diff);
        if (rowIndex < 0 || rowIndex >= diff.Rows.Count)
            throw new ArgumentOutOfRangeException(nameof(rowIndex), "메모를 붙일 비교 행이 없습니다.");

        var row = diff.Rows[rowIndex];
        var memo = new DiffMemo(
            Guid.NewGuid().ToString("n"),
            new DiffMemoAnchor(rowIndex, row.Kind, row.OldText, row.NewText, DiffMemoAnchor.ResolveSide(row, side)),
            author,
            text,
            createdAt,
            UpdatedAt: null);
        _memos.Add(memo);
        Sort();
        Changed?.Invoke(this, EventArgs.Empty);
        return memo;
    }

    public bool Update(string id, string author, string text, DateTimeOffset updatedAt)
    {
        var index = _memos.FindIndex(memo => memo.Id == id);
        if (index < 0) return false;
        _memos[index] = _memos[index] with { Author = author, Text = text, UpdatedAt = updatedAt };
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Remove(string id)
    {
        var index = _memos.FindIndex(memo => memo.Id == id);
        if (index < 0) return false;
        _memos.RemoveAt(index);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public DiffMemoReply AddReply(string memoId, string author, string text, DateTimeOffset createdAt)
    {
        var index = _memos.FindIndex(memo => memo.Id == memoId);
        if (index < 0) throw new ArgumentException("회신을 달 검토 메모가 없습니다.", nameof(memoId));
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("회신 내용을 입력해 주세요.", nameof(text));
        var reply = new DiffMemoReply(Guid.NewGuid().ToString("n"),
            string.IsNullOrWhiteSpace(author) ? "검토자" : author.Trim(), text.Trim(), createdAt);
        _memos[index] = _memos[index] with { Replies = _memos[index].Replies.Append(reply).ToArray() };
        Changed?.Invoke(this, EventArgs.Empty);
        return reply;
    }

    public bool RemoveReply(string memoId, string replyId)
    {
        var index = _memos.FindIndex(memo => memo.Id == memoId);
        if (index < 0) return false;
        var replies = _memos[index].Replies.Where(reply => reply.Id != replyId).ToArray();
        if (replies.Length == _memos[index].Replies.Count) return false;
        _memos[index] = _memos[index] with { Replies = replies };
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Clear()
    {
        if (_memos.Count == 0) return;
        _memos.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public DiffMemo? Find(string id) => _memos.FirstOrDefault(memo => memo.Id == id);

    public IReadOnlyList<DiffMemo> ForRow(int rowIndex) =>
        rowIndex < 0 ? Array.Empty<DiffMemo>() : _memos.Where(memo => memo.Anchor.RowIndex == rowIndex).ToArray();

    /// <summary>
    /// Memos per comparison row, split by the column they were written on, so
    /// each side can show its own flag.
    /// </summary>
    public IReadOnlyDictionary<int, (int Old, int New)> CountByRowSide()
    {
        var counts = new Dictionary<int, (int Old, int New)>();
        foreach (var memo in _memos)
        {
            if (memo.Anchor.IsOrphaned) continue;
            var current = counts.GetValueOrDefault(memo.Anchor.RowIndex);
            counts[memo.Anchor.RowIndex] = memo.Anchor.Side == DiffMemoSide.Old
                ? current with { Old = current.Old + 1 }
                : current with { New = current.New + 1 };
        }
        return counts;
    }

    /// <summary>
    /// Moves every memo onto the row of <paramref name="diff"/> that still holds
    /// its paragraph. A memo whose paragraph disappeared keeps its text but is
    /// marked orphaned rather than being silently attached to a stranger.
    /// </summary>
    public void Reanchor(DocumentDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);
        if (_memos.Count == 0) return;

        var exact = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var loose = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var rowIndex = 0; rowIndex < diff.Rows.Count; rowIndex++)
        {
            var row = diff.Rows[rowIndex];
            Index(exact, ExactKey(row.Kind, row.OldText, row.NewText), rowIndex);
            Index(loose, LooseKey(row.OldText, row.NewText), rowIndex);
        }

        // Several memos can share one paragraph, so resolve per anchor rather
        // than per memo. Two different paragraphs never land on the same row.
        var taken = new HashSet<int>();
        var resolvedByAnchor = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < _memos.Count; index++)
        {
            var anchor = _memos[index].Anchor;
            var exactKey = ExactKey(anchor.Kind, anchor.OldText, anchor.NewText);
            var anchorKey = $"{anchor.RowIndex}{KeySeparator}{exactKey}";
            if (!resolvedByAnchor.TryGetValue(anchorKey, out var rowIndex))
            {
                rowIndex =
                    Nearest(exact, exactKey, anchor.RowIndex, taken)
                    ?? Nearest(loose, LooseKey(anchor.OldText, anchor.NewText), anchor.RowIndex, taken)
                    ?? DiffMemoAnchor.OrphanedRowIndex;
                resolvedByAnchor[anchorKey] = rowIndex;
                if (rowIndex >= 0) taken.Add(rowIndex);
            }
            if (rowIndex == anchor.RowIndex) continue;

            var resolved = rowIndex >= 0 ? diff.Rows[rowIndex] : null;
            _memos[index] = _memos[index] with
            {
                Anchor = resolved is null
                    ? anchor with { RowIndex = DiffMemoAnchor.OrphanedRowIndex }
                    : new DiffMemoAnchor(
                        rowIndex,
                        resolved.Kind,
                        resolved.OldText,
                        resolved.NewText,
                        DiffMemoAnchor.ResolveSide(resolved, anchor.Side)),
            };
        }

        Sort();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void Index(Dictionary<string, List<int>> map, string key, int rowIndex)
    {
        if (!map.TryGetValue(key, out var rows)) map[key] = rows = new List<int>();
        rows.Add(rowIndex);
    }

    /// <summary>
    /// The same paragraph text repeats often in a report, so prefer the
    /// candidate closest to where the memo used to sit, and never move two
    /// memos of different paragraphs onto one row.
    /// </summary>
    private static int? Nearest(Dictionary<string, List<int>> map, string key, int previousRowIndex, HashSet<int> taken)
    {
        if (!map.TryGetValue(key, out var rows)) return null;
        int? best = null;
        foreach (var candidate in rows)
        {
            if (taken.Contains(candidate)) continue;
            if (previousRowIndex < 0) return candidate;
            if (best is null || Math.Abs(candidate - previousRowIndex) < Math.Abs(best.Value - previousRowIndex))
                best = candidate;
        }
        return best;
    }

    private static string ExactKey(DiffChangeKind kind, string? oldText, string? newText) =>
        $"{(int)kind}{KeySeparator}{oldText ?? NoText}{KeySeparator}{newText ?? NoText}";

    private static string LooseKey(string? oldText, string? newText) =>
        $"{oldText ?? NoText}{KeySeparator}{newText ?? NoText}";

    private void Sort() => _memos.Sort(static (left, right) =>
    {
        var leftRow = left.Anchor.IsOrphaned ? int.MaxValue : left.Anchor.RowIndex;
        var rightRow = right.Anchor.IsOrphaned ? int.MaxValue : right.Anchor.RowIndex;
        var byRow = leftRow.CompareTo(rightRow);
        if (byRow != 0) return byRow;
        var byCreated = left.CreatedAt.CompareTo(right.CreatedAt);
        return byCreated != 0 ? byCreated : StringComparer.Ordinal.Compare(left.Id,right.Id);
    });
}
