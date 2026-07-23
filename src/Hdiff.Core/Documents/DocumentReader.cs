using Hdiff.Core.Hwp5;
using Hdiff.Core.Hwpx;

namespace Hdiff.Core.Documents;

public sealed class DocumentReader
{
    private readonly Hwp5Reader _hwp5 = new();
    private readonly HwpxReader _hwpx = new();

    public ParsedDocument Read(string path)
    {
        if (!File.Exists(path)) throw new DocumentReadException($"파일을 찾을 수 없습니다: {path}");
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".hwp" => _hwp5.Read(path),
            ".hwpx" => _hwpx.Read(path),
            _ => throw new DocumentReadException("지원 형식은 .hwp 및 .hwpx 입니다."),
        };
    }
}
