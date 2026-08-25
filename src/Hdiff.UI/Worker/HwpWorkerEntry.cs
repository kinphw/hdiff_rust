using System.Text;
using System.Text.Json;
using Hdiff.Core.Documents;

namespace Hdiff.UI.Worker;

/// <summary>
/// All document-content reads happen in this child process. The parent only
/// handles paths and UI state, so a DLP/DRM crash cannot take down the window.
/// </summary>
internal static class HwpWorkerEntry
{
    public static void Run()
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.SetIn(new StreamReader(Console.OpenStandardInput(), utf8));
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });

        string? line;
        while ((line = Console.In.ReadLine()) is not null)
        {
            WorkerResponse response;
            try
            {
                var request = JsonSerializer.Deserialize<WorkerRequest>(line)
                    ?? throw new InvalidOperationException("워커 요청 JSON이 비어 있습니다.");
                response = Handle(request);
            }
            catch (Exception ex)
            {
                response = new WorkerResponse(false, null, ex.Message, null);
            }
            Console.Out.WriteLine(JsonSerializer.Serialize(response));
        }
    }

    private static WorkerResponse Handle(WorkerRequest request)
    {
        if (request.ForceComFallback)
        {
            if (!request.AllowComFallback)
                return new WorkerResponse(false, null, "COM 강제 테스트에는 COM 폴백 허용이 필요합니다.", null);
            try
            {
                var document = new HwpComFallbackReader().Read(
                    request.Path,
                    "테스트 요청으로 직접 파서를 건너뜀",
                    request.IncludeMemos);
                return new WorkerResponse(true, document, null, document.Reader);
            }
            catch (Exception comError)
            {
                return new WorkerResponse(false, null, $"COM 폴백 실패: {comError.Message}", null);
            }
        }

        if (PdfTextReader.IsSupportedExtension(request.Path))
        {
            try
            {
                var document = new PdfTextReader().Read(request.Path, request.ReflowPdfParagraphs);
                return new WorkerResponse(true, document, null, document.Reader);
            }
            catch (Exception pdfError)
            {
                return new WorkerResponse(false, null, $"PDF 읽기 실패: {pdfError.Message}", null);
            }
        }

        if (ExcelComReader.IsSupportedExtension(request.Path))
        {
            if (!request.AllowComFallback)
                return new WorkerResponse(false, null, "Excel 문서는 읽기 전용 COM 허용이 필요합니다.", null);
            try
            {
                var document = new ExcelComReader().Read(request.Path);
                return new WorkerResponse(true, document, null, document.Reader);
            }
            catch (Exception excelError)
            {
                return new WorkerResponse(false, null, $"Excel COM 읽기 실패: {excelError.Message}", null);
            }
        }

        if (WordComReader.IsSupportedExtension(request.Path))
        {
            if (!request.AllowComFallback)
                return new WorkerResponse(false, null, "Word 문서는 읽기 전용 COM 허용이 필요합니다.", null);
            try
            {
                var document = new WordComReader().Read(request.Path);
                return new WorkerResponse(true, document, null, document.Reader);
            }
            catch (Exception wordError)
            {
                return new WorkerResponse(false, null, $"Word COM 읽기 실패: {wordError.Message}", null);
            }
        }

        try
        {
            var document = new DocumentReader().Read(request.Path, request.IncludeMemos);
            return new WorkerResponse(true, document, null, document.Reader);
        }
        catch (DocumentReadException directError) when (
            request.AllowComFallback
            && (Path.GetExtension(request.Path).Equals(".hwp", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(request.Path).Equals(".hwpx", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var document = new HwpComFallbackReader().Read(request.Path, directError.Message, request.IncludeMemos);
                return new WorkerResponse(true, document, null, document.Reader);
            }
            catch (Exception comError)
            {
                return new WorkerResponse(false, null,
                    $"직접 파서 실패: {directError.Message}\nCOM 폴백 실패: {comError.Message}", null);
            }
        }
        catch (DocumentReadException directError)
        {
            return new WorkerResponse(false, null, directError.Message, null);
        }
    }
}

internal sealed record WorkerRequest(
    string Path,
    bool AllowComFallback,
    bool IncludeMemos = false,
    bool ForceComFallback = false,
    bool ReflowPdfParagraphs = true);
internal sealed record WorkerResponse(bool Ok, ParsedDocument? Document, string? Error, string? Reader);
