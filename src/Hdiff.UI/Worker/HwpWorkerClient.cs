using System.Diagnostics;
using System.Text.Json;
using Hdiff.Core.Documents;

namespace Hdiff.UI.Worker;

internal sealed class HwpWorkerClient
{
    public ParsedDocument Read(
        string path,
        bool allowComFallback,
        bool includeMemos = false,
        bool reflowPdfParagraphs = true)
    {
        var workerPath = ResolveWorkerPath(path);
        using var process = Process.Start(new ProcessStartInfo(workerPath, "--worker")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        }) ?? throw new InvalidOperationException("문서 파서 워커를 시작하지 못했습니다.");

        process.StandardInput.WriteLine(JsonSerializer.Serialize(
            new WorkerRequest(path, allowComFallback, includeMemos, ForceComFallback: false, reflowPdfParagraphs)));
        process.StandardInput.Close();
        var timeoutMilliseconds = ExcelComReader.IsSupportedExtension(path)
            || WordComReader.IsSupportedExtension(path)
            || PdfTextReader.IsSupportedExtension(path)
            ? 300_000
            : 30_000;
        var responseTask = process.StandardOutput.ReadLineAsync();
        if (!responseTask.Wait(timeoutMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"문서 파서가 {timeoutMilliseconds / 1000}초 안에 응답하지 않았습니다.");
        }
        var line = responseTask.GetAwaiter().GetResult();
        if (!process.WaitForExit(10_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }
        if (line is null)
            throw new IOException("문서 파서 워커가 응답 없이 종료되었습니다.");
        var response = JsonSerializer.Deserialize<WorkerResponse>(line)
            ?? throw new IOException("문서 파서 워커 응답을 해석하지 못했습니다.");
        if (!response.Ok || response.Document is null)
            throw new DocumentReadException(response.Error ?? "문서 파서 워커가 실패했습니다.");
        return response.Document;
    }

    private static string ResolveWorkerPath(string documentPath)
    {
        var currentExecutable = Environment.ProcessPath
            ?? throw new InvalidOperationException("현재 실행 파일 경로를 찾지 못했습니다.");
        if (!PdfTextReader.IsSupportedExtension(documentPath)) return currentExecutable;

        // DocMine's production DRM environment permits protected PDF bytes
        // only to a python.exe basename. The release package contains an
        // identical Hdiff single-file binary under that worker-only name.
        var directory = Path.GetDirectoryName(currentExecutable);
        if (string.IsNullOrEmpty(directory)) return currentExecutable;
        var drmWorker = Path.Combine(directory, "python.exe");
        return File.Exists(drmWorker) ? drmWorker : currentExecutable;
    }
}
