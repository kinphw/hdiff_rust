using Hdiff.Core.Hwp5;
using Hdiff.Core.Hwpx;

namespace Hdiff.Core.Documents;

public sealed class DocumentReader
{
    private readonly Hwp5Reader _hwp5 = new();
    private readonly HwpxReader _hwpx = new();
    private readonly PlainTextReader _plainText = new();

    public ParsedDocument Read(string path, bool includeMemos = false)
    {
        if (!File.Exists(path)) throw new DocumentReadException($"파일을 찾을 수 없습니다: {path}");
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".hwp" => _hwp5.Read(path, includeMemos),
            ".hwpx" => _hwpx.Read(path, includeMemos),
            ".txt" or ".md" => _plainText.Read(path),
            _ => throw new DocumentReadException("지원 형식은 .hwp, .hwpx, .txt 및 .md 입니다."),
        };
    }
}
