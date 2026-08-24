namespace Hdiff.Core.Review;

/// <summary>A memo paired with the stable reading-order number shown in every UI.</summary>
public sealed record NumberedDiffMemo(int Number, DiffMemo Memo);

/// <summary>
/// Shared presentation rules for native and exported review surfaces. Keeping
/// numbering here prevents a memo from changing identity when it is exported.
/// </summary>
public static class DiffMemoDisplay
{
    public static IReadOnlyList<NumberedDiffMemo> NumberForDisplay(
        IEnumerable<DiffMemo>? memos,
        int rowCount)
    {
        if (rowCount < 0) throw new ArgumentOutOfRangeException(nameof(rowCount));
        if (memos is null) return Array.Empty<NumberedDiffMemo>();

        return memos
            .Select(memo => memo.Anchor.RowIndex >= rowCount
                ? memo with { Anchor = memo.Anchor with { RowIndex = DiffMemoAnchor.OrphanedRowIndex } }
                : memo)
            .OrderBy(memo => memo.Anchor.IsOrphaned ? int.MaxValue : memo.Anchor.RowIndex)
            .ThenBy(memo => memo.CreatedAt)
            .ThenBy(memo => memo.Id, StringComparer.Ordinal)
            .Select((memo, index) => new NumberedDiffMemo(index + 1, memo))
            .ToArray();
    }
}
