using System.Text.RegularExpressions;
using Hdiff.Core.Documents;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Hdiff.UI.Worker;

/// <summary>
/// Extracts PDF text with the PdfPig strategy proven by DocMine. This type is
/// called only inside the isolated document worker; the UI process never opens
/// protected PDF contents.
/// </summary>
internal sealed partial class PdfTextReader
{
    private const int WindowsMaxPath = 260;
    private const int MaximumBodyCharacters = 30_000_000;

    public static bool IsSupportedExtension(string path) =>
        Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public ParsedDocument Read(string path, bool reflowParagraphs = true)
    {
        if (!File.Exists(path)) throw new DocumentReadException($"파일을 찾을 수 없습니다: {path}");
        if (!IsSupportedExtension(path)) throw new DocumentReadException("PDF 지원 형식은 .pdf입니다.");

        var blocks = new List<DocumentBlock>();
        var warnings = new List<string>();
        var totalCharacters = 0;
        try
        {
            var options = new ParsingOptions { UseLenientParsing = true };
            using var document = PdfDocument.Open(WindowsLongPath(path), options);
            foreach (var page in document.GetPages())
            {
                try
                {
                    foreach (var line in ExtractPageLines(page, reflowParagraphs))
                    {
                        if (line.Length == 0) continue;
                        totalCharacters = checked(totalCharacters + line.Length);
                        if (totalCharacters > MaximumBodyCharacters)
                            throw new DocumentReadException(
                                $"PDF 추출 본문이 안전 상한 {MaximumBodyCharacters:N0}자를 초과했습니다. 손상되거나 비정상적인 파일일 수 있습니다.");
                        blocks.Add(new DocumentBlock(
                            DocumentBlockKind.Paragraph,
                            line,
                            SectionPath: $"{page.Number}쪽"));
                    }
                }
                catch (DocumentReadException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    warnings.Add($"PDF {page.Number}쪽을 읽지 못해 건너뛰었습니다: {LimitMessage(exception.Message)}");
                }
            }
        }
        catch (DocumentReadException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new DocumentReadException($"PDF 본문을 읽지 못했습니다: {LimitMessage(exception.Message)}", exception);
        }

        if (blocks.Count == 0)
            throw new DocumentReadException("PDF에서 텍스트 본문을 찾지 못했습니다. 스캔 이미지 PDF는 OCR이 필요하며 현재 지원하지 않습니다.");

        return new ParsedDocument(path, blocks, "PDF PdfPig 직접 파서", warnings);
    }

    private static IReadOnlyList<string> ExtractPageLines(Page page, bool reflowParagraphs)
    {
        var words = page.GetWords()
            .Where(word => !string.IsNullOrWhiteSpace(word.Text))
            .OrderByDescending(word => word.BoundingBox.Bottom)
            .ThenBy(word => word.BoundingBox.Left)
            .ToList();
        if (words.Count == 0) return Array.Empty<string>();

        var lines = new List<PdfLine>();
        var lineWords = new List<Word>();
        double lineBottom = 0;
        double lineTolerance = 0;

        void Flush()
        {
            if (lineWords.Count == 0) return;
            var ordered = lineWords.OrderBy(word => word.BoundingBox.Left).ToList();
            var text = Clean(string.Join(" ", ordered.Select(word => word.Text)));
            if (text.Length > 0)
            {
                lines.Add(new PdfLine(
                    text,
                    ordered.Min(word => word.BoundingBox.Left),
                    ordered.Max(word => word.BoundingBox.Right),
                    ordered.Max(word => word.BoundingBox.Top),
                    ordered.Min(word => word.BoundingBox.Bottom),
                    ordered.Average(word => Math.Abs(word.BoundingBox.Height))));
            }
            lineWords.Clear();
        }

        foreach (var word in words)
        {
            var bottom = word.BoundingBox.Bottom;
            var height = Math.Abs(word.BoundingBox.Height);
            if (lineWords.Count == 0)
            {
                lineBottom = bottom;
                lineTolerance = Math.Max(1.0, height * 0.6);
                lineWords.Add(word);
            }
            else if (lineBottom - bottom <= lineTolerance)
            {
                lineWords.Add(word);
            }
            else
            {
                Flush();
                lineBottom = bottom;
                lineTolerance = Math.Max(1.0, height * 0.6);
                lineWords.Add(word);
            }
        }
        Flush();

        return reflowParagraphs
            ? ReflowParagraphs(lines)
            : lines.Select(line => line.Text).ToArray();
    }

    /// <summary>
    /// Puts wrapped lines back into the paragraph they came from.
    ///
    /// A PDF line ends for one of two reasons: the next word did not fit, or the
    /// paragraph ended. Those look different on the page - a wrapped line runs
    /// out to the text block's right edge, a paragraph's last line stops short -
    /// so the decision is a measurement, not a reading of the text.
    ///
    /// Where the geometry cannot tell (short-line layouts such as 개조식 reports,
    /// tables and forms, whose lines never reach the edge) nothing is merged and
    /// the result is the same as not reflowing at all.
    /// </summary>
    private static IReadOnlyList<string> ReflowParagraphs(IReadOnlyList<PdfLine> lines)
    {
        if (lines.Count == 0) return Array.Empty<string>();

        // The text block's edges, taken from the lines themselves so that margins,
        // page size and orientation need no assumptions. The 90th percentile
        // ignores the occasional line that overhangs the block.
        var rightEdge = Percentile(lines.Select(line => line.Right), 0.9);
        var bodyLeft = Percentile(lines.Select(line => line.Left), 0.5);
        var averageHeight = lines.Average(line => line.Height);
        // A wrapped line stops short of the edge by however wide the word that
        // did not fit was, so the tolerance is measured in characters - not in
        // line height, which is far too tight and leaves real paragraphs split.
        var totalCharacters = Math.Max(1, lines.Sum(line => line.Text.Length));
        var averageCharacterWidth = lines.Sum(line => line.Right - line.Left) / totalCharacters;
        var fullLineSlack = Math.Max(averageCharacterWidth * 7, averageHeight * 1.2);

        var paragraphs = new List<string>();
        var current = new List<PdfLine>();

        void Close()
        {
            if (current.Count == 0) return;
            paragraphs.Add(JoinWrapped(current));
            current.Clear();
        }

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            current.Add(line);
            if (index == lines.Count - 1)
            {
                Close();
                break;
            }

            var next = lines[index + 1];
            if (!ContinuesParagraph(line, next, rightEdge, bodyLeft, fullLineSlack, averageHeight)) Close();
        }

        Close();
        return paragraphs;
    }

    private static double Percentile(IEnumerable<double> values, double fraction)
    {
        var sorted = values.OrderBy(value => value).ToArray();
        var index = Math.Clamp((int)(sorted.Length * fraction), 0, sorted.Length - 1);
        return sorted[index];
    }

    private static bool ContinuesParagraph(
        PdfLine line,
        PdfLine next,
        double rightEdge,
        double bodyLeft,
        double fullLineSlack,
        double averageHeight)
    {
        // The line stopped well before the edge: the paragraph ended there.
        if (line.Right < rightEdge - fullLineSlack) return false;

        // A wider gap than normal leading separates blocks, not wrapped lines.
        var gap = line.Bottom - next.Top;
        if (gap > averageHeight * 0.9) return false;

        // The next line opens a new block of its own.
        if (next.Left > bodyLeft + (averageHeight * 0.8)) return false;
        return !StartsNewBlock(next.Text);
    }

    /// <summary>
    /// Openers that begin a block no matter how full the line above was:
    /// 조문, numbered and bulleted items, and 개조식 markers.
    /// </summary>
    private static bool StartsNewBlock(string text) => BlockOpener().IsMatch(text);

    /// <summary>
    /// Korean text wraps without a space at the break, so joining with one would
    /// insert a space that is not in the document. Latin text does need it.
    /// </summary>
    private static string JoinWrapped(IReadOnlyList<PdfLine> lines)
    {
        var builder = new System.Text.StringBuilder(lines[0].Text);
        for (var index = 1; index < lines.Count; index++)
        {
            var next = lines[index].Text;
            if (next.Length == 0) continue;
            if (builder.Length > 0 && builder[^1] == '-' && char.IsLower(next[0]))
            {
                builder.Length -= 1;
                builder.Append(next);
                continue;
            }
            if (builder.Length > 0 && NeedsSpace(builder[^1], next[0])) builder.Append(' ');
            builder.Append(next);
        }
        return builder.ToString();
    }

    private static bool NeedsSpace(char left, char right) =>
        !(IsWide(left) && IsWide(right)) && !char.IsWhiteSpace(left) && !char.IsWhiteSpace(right);

    private static bool IsWide(char character) =>
        character is >= '\uac00' and <= '\ud7a3'      // 한글 음절
        || character is >= '\u3130' and <= '\u318f'   // 한글 자모
        || character is >= '\u4e00' and <= '\u9fff'   // 한자
        || character is >= '\u3000' and <= '\u303f';  // CJK 문장부호

    private sealed record PdfLine(string Text, double Left, double Right, double Top, double Bottom, double Height);

    private static string Clean(string text)
    {
        text = text.Replace('\u00a0', ' ');
        return ControlCharacters().Replace(text, string.Empty).Trim();
    }

    private static string WindowsLongPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) || fullPath.Length < WindowsMaxPath)
            return fullPath;
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + fullPath[2..];
        return @"\\?\" + fullPath;
    }

    private static string LimitMessage(string message) =>
        message.Length <= 900 ? message : message[..900];

    [GeneratedRegex(@"[\x00-\x08\x0B\x0C\x0E-\x1F]")]
    private static partial Regex ControlCharacters();

    [GeneratedRegex(@"^\s*(제\s*\d+\s*[조장절관항]|\d+[.)]|[(（]\d+[)）]|[①-⑳]|[가-힣][.)]|[-·•▪□○◦▶*])\s")]
    private static partial Regex BlockOpener();
}
