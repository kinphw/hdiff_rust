using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Hdiff.Core.Documents;

namespace Hdiff.UI.Worker;

/// <summary>
/// Transitional fallback only. It reads text from a privately-created Hancom
/// automation instance, creates no normalized copy, and never enumerates or
/// kills user-owned Hwp.exe processes.
/// </summary>
internal sealed class HwpComFallbackReader
{
    private static readonly Regex ControlCharacters = new(@"[\x00-\x08\x0B\x0C\x0E-\x1F]", RegexOptions.Compiled);

    public ParsedDocument Read(string path, string directFailure)
    {
        var progId = Type.GetTypeFromProgID("HWPFrame.HwpObject")
            ?? throw new InvalidOperationException("한글 COM 자동화 객체를 찾지 못했습니다. 한글 설치 여부를 확인하세요.");
        dynamic? hwp = null;
        try
        {
            hwp = Activator.CreateInstance(progId)
                ?? throw new InvalidOperationException("한글 COM 인스턴스 생성에 실패했습니다.");
            try { hwp.RegisterModule("FilePathCheckDLL", "FilePathCheckerModule"); } catch { }
            try { hwp.XHwpWindows.Item(0).Visible = false; } catch { }
            try { hwp.SetMessageBoxMode(0x10000); } catch { }

            hwp.Open(Path.GetFullPath(path), "", "forceopen:true;versionwarning:false");
            var raw = (string?)hwp.GetTextFile("TEXT", "") ?? "";
            var text = Clean(raw);
            var blocks = text.Split('\n', StringSplitOptions.TrimEntries)
                .Select(line => new DocumentBlock(DocumentBlockKind.Paragraph, line))
                .ToArray();
            if (blocks.All(block => string.IsNullOrWhiteSpace(block.Text))) throw new DocumentReadException("한글 COM이 본문 텍스트를 반환하지 않았습니다.");
            return new ParsedDocument(path, blocks, "한글 COM 폴백", new[] { $"직접 파서 실패 후 COM 텍스트 경로 사용: {directFailure}" });
        }
        finally
        {
            if (hwp is not null)
            {
                try { hwp.XHwpDocuments.Item(0).SetModified(false); } catch { }
                try { hwp.Run("FileClose"); } catch { }
                try { hwp.SetMessageBoxMode(0xF0000); } catch { }
                try { hwp.Quit(); } catch { }
                try { Marshal.FinalReleaseComObject(hwp); } catch { }
            }
        }
    }

    private static string Clean(string value)
    {
        value = WebUtility.HtmlDecode(value).Replace('\r', '\n');
        value = ControlCharacters.Replace(value, "");
        return value.Trim();
    }
}
