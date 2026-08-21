namespace Hdiff.Core.Documents;

public enum DocumentBlockKind
{
    Paragraph,
    Table,
}

/// <summary>
/// A reader-neutral document block. Both HWP5 and HWPX readers must produce
/// this model so that the comparison engine never depends on a file format.
/// </summary>
public sealed record DocumentBlock(
    DocumentBlockKind Kind,
    string Text,
    IReadOnlyList<IReadOnlyList<string>>? Rows = null,
    string? SectionPath = null);

public sealed record ParsedDocument(
    string SourcePath,
    IReadOnlyList<DocumentBlock> Blocks,
    string Reader,
    IReadOnlyList<string> Warnings)
{
    /// <summary>
    /// Gets the source paragraphs used as comparison rows. Empty paragraphs are
    /// retained in the document model so the UI can show original report
    /// spacing when requested, but are normally omitted as comparison noise.
    /// </summary>
    public IEnumerable<string> ComparisonLines(bool includeEmptyParagraphs = false)
    {
        foreach (var block in Blocks)
        {
            if (block.Kind == DocumentBlockKind.Table && block.Rows is not null)
            {
                if (!string.IsNullOrWhiteSpace(block.Text)) yield return block.Text;
                foreach (var row in block.Rows)
                    yield return "[표] " + string.Join(" | ", row);
            }
            else if (includeEmptyParagraphs || !string.IsNullOrWhiteSpace(block.Text))
            {
                yield return block.Text;
            }
        }
    }
}

public sealed class DocumentReadException(string message, Exception? innerException = null)
    : Exception(message, innerException);
