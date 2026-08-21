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

    public ParsedDocument Read(string path)
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
                    foreach (var line in ExtractPageLines(page))
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

    private static IReadOnlyList<string> ExtractPageLines(Page page)
    {
        var words = page.GetWords()
            .Where(word => !string.IsNullOrWhiteSpace(word.Text))
            .OrderByDescending(word => word.BoundingBox.Bottom)
            .ThenBy(word => word.BoundingBox.Left)
            .ToList();
        if (words.Count == 0) return Array.Empty<string>();

        var lines = new List<string>();
        var lineWords = new List<Word>();
        double lineBottom = 0;
        double lineTolerance = 0;

        void Flush()
        {
            if (lineWords.Count == 0) return;
            var line = string.Join(" ", lineWords
                .OrderBy(word => word.BoundingBox.Left)
                .Select(word => word.Text));
            line = Clean(line);
            if (line.Length > 0) lines.Add(line);
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
        return lines;
    }

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
}
