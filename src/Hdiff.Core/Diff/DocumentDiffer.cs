using DiffPlex;
using DiffPlex.Chunkers;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Hdiff.Core.Documents;

namespace Hdiff.Core.Diff;

public enum DiffChangeKind
{
    Unchanged,
    Inserted,
    Deleted,
    Modified,
}

public enum InlineDiffFragmentKind
{
    Unchanged,
    Removed,
    Added,
}

public sealed record InlineDiffFragment(InlineDiffFragmentKind Kind, string Text);

public sealed record DiffRow(
    DiffChangeKind Kind,
    int? OldLine,
    string? OldText,
    int? NewLine,
    string? NewText,
    IReadOnlyList<InlineDiffFragment> OldFragments,
    IReadOnlyList<InlineDiffFragment> NewFragments);

public sealed record DiffSummary(int Inserted, int Deleted, int Modified, int Unchanged)
{
    public bool HasChanges => Inserted + Deleted + Modified > 0;
}

public sealed record DocumentDiff(
    ParsedDocument OldDocument,
    ParsedDocument NewDocument,
    IReadOnlyList<DiffRow> Rows,
    DiffSummary Summary)
{
    public string ToGitLog()
    {
        var lines = new List<string>
        {
            $"diff --hwp \"{Path.GetFileName(OldDocument.SourcePath)}\" \"{Path.GetFileName(NewDocument.SourcePath)}\"",
            $"--- {Path.GetFileName(OldDocument.SourcePath)} ({OldDocument.Reader})",
            $"+++ {Path.GetFileName(NewDocument.SourcePath)} ({NewDocument.Reader})",
            $"변경 요약: 수정 {Summary.Modified}, 추가 {Summary.Inserted}, 삭제 {Summary.Deleted}",
        };

        foreach (var row in Rows.Where(r => r.Kind != DiffChangeKind.Unchanged))
        {
            var oldPos = row.OldLine?.ToString() ?? "-";
            var newPos = row.NewLine?.ToString() ?? "-";
            lines.Add($"@@ 문단 {oldPos} → {newPos} @@");
            if (row.OldText is not null) lines.Add("- " + row.OldText);
            if (row.NewText is not null) lines.Add("+ " + row.NewText);
        }
        return string.Join(Environment.NewLine, lines);
    }
}

public sealed class DocumentDiffer
{
    private readonly SideBySideDiffBuilder _builder =
        new(new Differ(), LineChunker.Instance, CharacterChunker.Instance);

    public DocumentDiff Compare(ParsedDocument oldDocument, ParsedDocument newDocument, bool ignoreWhitespace = true)
    {
        var oldText = string.Join("\n", oldDocument.ComparisonLines());
        var newText = string.Join("\n", newDocument.ComparisonLines());
        var model = _builder.BuildDiffModel(oldText, newText, ignoreWhitespace);
        var rows = new List<DiffRow>();
        var inserted = 0;
        var deleted = 0;
        var modified = 0;
        var unchanged = 0;
        var count = Math.Max(model.OldText.Lines.Count, model.NewText.Lines.Count);

        for (var i = 0; i < count; i++)
        {
            var left = i < model.OldText.Lines.Count ? model.OldText.Lines[i] : null;
            var right = i < model.NewText.Lines.Count ? model.NewText.Lines[i] : null;
            var leftIsReal = left is not null && left.Type != ChangeType.Imaginary;
            var rightIsReal = right is not null && right.Type != ChangeType.Imaginary;

            if (leftIsReal && rightIsReal && left!.Type == ChangeType.Unchanged && right!.Type == ChangeType.Unchanged)
            {
                rows.Add(CreateRow(DiffChangeKind.Unchanged, left, right));
                unchanged++;
            }
            else if (leftIsReal && rightIsReal)
            {
                rows.Add(CreateRow(DiffChangeKind.Modified, left, right));
                modified++;
            }
            else if (leftIsReal)
            {
                rows.Add(CreateRow(DiffChangeKind.Deleted, left, null));
                deleted++;
            }
            else if (rightIsReal)
            {
                rows.Add(CreateRow(DiffChangeKind.Inserted, null, right));
                inserted++;
            }
        }

        return new DocumentDiff(oldDocument, newDocument, rows, new DiffSummary(inserted, deleted, modified, unchanged));
    }

    private static DiffRow CreateRow(DiffChangeKind kind, DiffPiece? oldPiece, DiffPiece? newPiece) => new(
        kind,
        oldPiece?.Position,
        oldPiece?.Text,
        newPiece?.Position,
        newPiece?.Text,
        ToFragments(oldPiece, isOldSide: true),
        ToFragments(newPiece, isOldSide: false));

    private static IReadOnlyList<InlineDiffFragment> ToFragments(DiffPiece? piece, bool isOldSide)
    {
        if (piece is null || piece.Type == ChangeType.Imaginary) return Array.Empty<InlineDiffFragment>();

        var pieces = piece.SubPieces.Count > 0 ? piece.SubPieces : new List<DiffPiece> { piece };
        var fragments = new List<InlineDiffFragment>();
        foreach (var subPiece in pieces)
        {
            if (subPiece.Type == ChangeType.Imaginary || string.IsNullOrEmpty(subPiece.Text)) continue;
            var kind = subPiece.Type switch
            {
                ChangeType.Unchanged => InlineDiffFragmentKind.Unchanged,
                _ when isOldSide => InlineDiffFragmentKind.Removed,
                _ => InlineDiffFragmentKind.Added,
            };

            if (fragments.LastOrDefault() is { } previous && previous.Kind == kind)
            {
                fragments[^1] = previous with { Text = previous.Text + subPiece.Text };
            }
            else
            {
                fragments.Add(new InlineDiffFragment(kind, subPiece.Text));
            }
        }
        return fragments;
    }
}
