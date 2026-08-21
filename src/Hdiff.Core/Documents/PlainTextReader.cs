using System.Text;

namespace Hdiff.Core.Documents;

/// <summary>
/// Converts plain text and Markdown into the same paragraph model used by the
/// HWP readers. Markdown syntax is intentionally preserved verbatim so the
/// comparison shows exactly what the user pasted or what the file contains.
/// </summary>
public sealed class PlainTextReader
{
    public ParsedDocument Read(string path)
    {
        if (!File.Exists(path)) throw new DocumentReadException($"파일을 찾을 수 없습니다: {path}");

        try
        {
            var (text, encodingLabel) = Decode(File.ReadAllBytes(path));
            var reader = Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase)
                ? "Markdown 직접 파서"
                : "텍스트 직접 파서";
            if (encodingLabel is not null) reader += $" ({encodingLabel})";
            return ReadText(path, text, reader);
        }
        catch (DocumentReadException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new DocumentReadException($"텍스트 파일을 읽지 못했습니다: {ex.Message}", ex);
        }
    }

    public ParsedDocument ReadText(string sourceName, string text, string reader = "직접 입력")
    {
        var normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var blocks = normalized
            .Split('\n', StringSplitOptions.None)
            .Select(line => new DocumentBlock(DocumentBlockKind.Paragraph, line))
            .ToArray();
        return new ParsedDocument(sourceName, blocks, reader, Array.Empty<string>());
    }

    private static (string Text, string? EncodingLabel) Decode(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true)
                .GetString(bytes, 3, bytes.Length - 3), null);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16 LE");
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16 BE");

        try
        {
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes), null);
        }
        catch (DecoderFallbackException utf8Error)
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                var cp949 = Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                return (cp949.GetString(bytes), "CP949");
            }
            catch (Exception cp949Error) when (cp949Error is DecoderFallbackException or NotSupportedException)
            {
                throw new DocumentReadException(
                    "TXT/MD 파일 인코딩을 읽지 못했습니다. UTF-8, BOM이 있는 UTF-16 또는 CP949 파일을 사용하세요.",
                    new AggregateException(utf8Error, cp949Error));
            }
        }
    }
}
