using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Hdiff.Core.Documents;

namespace Hdiff.UI.Worker;

/// <summary>
/// Transitional fallback only. It reads text from a privately-created Hancom
/// automation instance through InitScan/GetText, which is a memory-only path.
/// It never calls GetTextFile, Save, SaveAs, or enumerates user-owned Hwp.exe
/// processes.
/// </summary>
internal sealed class HwpComFallbackReader
{
    // Do not create a document lock beside the source file. In a DLP/DRM
    // environment even this small write can be classified as a prohibited
    // document operation. This reader never calls Save or SaveAs.
    private const string ReadOnlyOpenOptions = "lock:false;forceopen:true;versionwarning:false";
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

            hwp.Open(Path.GetFullPath(path), "", ReadOnlyOpenOptions);
            var raw = ReadEntireDocumentByScan((object)hwp);
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
                // No SetModified/Save/SaveAs: the fallback only reads a TEXT
                // string from memory, then closes the private COM document.
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

    private static string ReadEntireDocumentByScan(object comObject)
    {
        // Newer Hancom builds expose the documented IID, but a few installed
        // versions reject that QueryInterface call even though the methods are
        // available through IDispatch. Both routes below are still the same
        // memory-only InitScan/GetText API; neither falls back to GetTextFile.
        if (comObject is IHwpTextScanner scanner)
            return Scan(
                () => scanner.InitScan(null, 0x77, null, null, null, null),
                () =>
                {
                    var state = scanner.GetText(out var chunk);
                    return (state, chunk);
                },
                scanner.ReleaseScan);

        try
        {
            dynamic hwpScanner = comObject;
            return Scan(
                () => hwpScanner.InitScan(null, 0x77, null, null, null, null),
                () =>
                {
                    var state = hwpScanner.GetText(out string chunk);
                    return ((int)state, chunk);
                },
                () => hwpScanner.ReleaseScan());
        }
        catch (DocumentReadException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new DocumentReadException(
                "한글 COM 메모리 스캔 인터페이스를 호출하지 못했습니다. " +
                "보안 정책 팝업을 유발하는 GetTextFile 폴백은 사용하지 않습니다.", exception);
        }
    }

    private static string Scan(
        Func<bool> initScan,
        Func<(int State, string Text)> getText,
        Action releaseScan)
    {
        var builder = new System.Text.StringBuilder();
        try
        {
            // 0x77 scans the complete document (0xFF is selection-only).
            // This remains a memory-only API and never invokes Hancom's
            // internal SaveBlockAction path used by GetTextFile.
            if (!initScan())
                throw new DocumentReadException("한글 COM 본문 스캔을 시작하지 못했습니다.");

            // 2 = 일반 텍스트, 3 = 다음 문단, 4/5 = 제어영역 진입/이탈이다.
            // 표·필드·섹션 정의가 앞에 있는 실제 보고서는 4/5로 시작할 수 있다.
            // 여기서 멈추면 본문이 비어 보이므로, 목록 끝(0/1)까지 스캔을 계속한다.
            // The guard prevents a malformed document from keeping a COM worker alive forever.
            for (var count = 0; count < 1_000_000; count++)
            {
                var (state, chunk) = getText();
                if (state is 2 or 3)
                {
                    builder.Append(chunk);
                    continue;
                }

                if (state is 0 or 1)
                    break;

                if (state is 101 or 102)
                    throw new DocumentReadException($"한글 COM 본문 스캔이 상태 코드 {state}로 실패했습니다.");

                // State 4/5 and future non-terminal states. The returned
                // chunk is normally a control marker, but retain any actual
                // text and let Clean remove the marker later.
                if (!string.IsNullOrEmpty(chunk))
                    builder.Append(chunk);
            }
        }
        finally
        {
            try { releaseScan(); } catch { }
        }
        return builder.ToString();
    }
}
