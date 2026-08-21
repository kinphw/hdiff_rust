using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hdiff.ExcelComReadProbe;

internal static class Program
{
    private const int MacroSecurityForceDisable = 3;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        if (args.Contains("--worker", StringComparer.OrdinalIgnoreCase))
            return RunWorker(WorkerOptions.Parse(args));

        if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            return RunControllerAsync(ControllerOptions.Parse(args)).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"PoC 실행 준비에 실패했습니다: {exception.Message}");
            return 2;
        }
    }

    private static async Task<int> RunControllerAsync(ControllerOptions options)
    {
        var fullPath = Path.GetFullPath(options.FilePath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("시험할 Excel 파일을 찾지 못했습니다.", fullPath);

        var logPath = options.LogPath is null ? CreateDefaultLogPath() : Path.GetFullPath(options.LogPath);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? Directory.GetCurrentDirectory());

        using var log = new ControllerLog(logPath);
        var file = new FileInfo(fullPath);
        var baselineExcelPids = GetExcelProcessIds();
        log.Write("info", "probe_started", "Excel COM 읽기 PoC를 시작합니다.", new
        {
            targetPath = fullPath,
            file.Extension,
            file.Length,
            lastWriteUtc = file.LastWriteTimeUtc,
            timeoutSeconds = options.TimeoutSeconds,
            options.MaxCells,
            options.IncludeValues,
            options.SampleLimit,
            baselineExcelPids,
            os = RuntimeInformation.OSDescription,
            runtime = RuntimeInformation.FrameworkDescription,
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
        });
        if (options.IncludeValues)
            log.Write("warning", "sensitive_logging_enabled", "셀 값과 수식 미리보기가 로그에 기록됩니다. 외부 전달 전에 내용을 확인하세요.");

        var startInfo = CreateWorkerStartInfo(fullPath, options, baselineExcelPids);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("PoC 워커를 시작하지 못했습니다.");
        var createdExcelPid = 0;

        var stdoutPump = PumpAsync(process.StandardOutput, line =>
        {
            log.WriteWorkerLine(line);
            if (TryReadExcelProcessId(line, out var processId)) Interlocked.Exchange(ref createdExcelPid, processId);
        });
        var stderrPump = PumpAsync(process.StandardError, line =>
            log.Write("error", "worker_stderr", line));

        var waitTask = process.WaitForExitAsync();
        var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(options.TimeoutSeconds))).ConfigureAwait(false);
        var timedOut = completed != waitTask;
        if (timedOut)
        {
            log.Write("error", "probe_timeout", "Excel COM 호출이 제한 시간 안에 끝나지 않아 PoC 워커를 종료합니다.", new
            {
                options.TimeoutSeconds,
                workerProcessId = process.Id,
                excelProcessId = Volatile.Read(ref createdExcelPid),
            });
            try { process.Kill(entireProcessTree: false); } catch (Exception exception) { log.WriteException("worker_kill_failed", exception); }
        }

        try { await waitTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); } catch { }
        await Task.WhenAll(stdoutPump, stderrPump).ConfigureAwait(false);

        CleanupOwnedExcelProcess(Volatile.Read(ref createdExcelPid), baselineExcelPids, log);
        var exitCode = timedOut ? 124 : process.ExitCode;
        log.Write(exitCode == 0 ? "info" : "error", "probe_finished",
            exitCode == 0 ? "Excel COM에서 통합문서 구조와 셀 내용을 읽었습니다." : "Excel COM 읽기 PoC가 실패했습니다.",
            new { exitCode, logPath });

        Console.WriteLine();
        Console.WriteLine($"로그 파일: {logPath}");
        return exitCode;
    }

    private static ProcessStartInfo CreateWorkerStartInfo(string filePath, ControllerOptions options, int[] baselineExcelPids)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("현재 실행 파일 경로를 찾지 못했습니다.");
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            startInfo.ArgumentList.Add(Assembly.GetEntryAssembly()?.Location
                ?? throw new InvalidOperationException("PoC 어셈블리 경로를 찾지 못했습니다."));

        startInfo.ArgumentList.Add("--worker");
        startInfo.ArgumentList.Add("--file");
        startInfo.ArgumentList.Add(filePath);
        startInfo.ArgumentList.Add("--max-cells");
        startInfo.ArgumentList.Add(options.MaxCells.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--sample-limit");
        startInfo.ArgumentList.Add(options.SampleLimit.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--baseline-excel-pids");
        startInfo.ArgumentList.Add(string.Join(',', baselineExcelPids));
        if (options.IncludeValues) startInfo.ArgumentList.Add("--include-values");
        return startInfo;
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> consume)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            consume(line);
    }

    private static bool TryReadExcelProcessId(string line, out int processId)
    {
        processId = 0;
        try
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            return root.TryGetProperty("event", out var eventName)
                && eventName.GetString() == "excel_instance_created"
                && root.TryGetProperty("data", out var data)
                && data.TryGetProperty("excelProcessId", out var pid)
                && pid.TryGetInt32(out processId);
        }
        catch
        {
            return false;
        }
    }

    private static int RunWorker(WorkerOptions options)
    {
        dynamic? excel = null;
        object? workbooksObject = null;
        object? workbookObject = null;
        var ownsExcelProcess = false;
        var stage = "excel_activation";

        try
        {
            WorkerLog.Write("info", "worker_started", "격리된 Excel COM 워커를 시작했습니다.", new
            {
                workerProcessId = Environment.ProcessId,
                options.FilePath,
                options.MaxCells,
                options.IncludeValues,
                options.SampleLimit,
            });

            var excelType = Type.GetTypeFromProgID("Excel.Application");
            if (excelType is null)
            {
                WorkerLog.Write("error", "excel_not_registered", "Excel.Application COM 클래스가 등록되어 있지 않습니다.");
                return 10;
            }

            excel = Activator.CreateInstance(excelType)
                ?? throw new InvalidOperationException("Excel COM 객체를 생성하지 못했습니다.");
            var excelProcessId = GetExcelProcessId(excel);
            ownsExcelProcess = excelProcessId > 0 && !options.BaselineExcelProcessIds.Contains(excelProcessId);
            WorkerLog.Write("info", "excel_instance_created", "Excel COM 인스턴스를 생성했습니다.", new
            {
                excelProcessId,
                isolatedProcess = ownsExcelProcess,
                version = SafeGet(() => (string?)excel.Version),
                build = SafeGet(() => Convert.ToString(excel.Build, CultureInfo.InvariantCulture)),
            });

            if (!ownsExcelProcess)
            {
                WorkerLog.Write("error", "excel_instance_not_isolated",
                    "새 COM 객체가 기존 Excel 프로세스에 연결되었거나 PID를 확인하지 못해 사용자 Excel 보호를 위해 중단합니다.");
                return 11;
            }

            stage = "excel_security_configuration";
            SetOptionalExcelProperty("Visible", () => excel.Visible = false);
            SetOptionalExcelProperty("DisplayAlerts", () => excel.DisplayAlerts = false);
            SetOptionalExcelProperty("EnableEvents", () => excel.EnableEvents = false);
            SetOptionalExcelProperty("AskToUpdateLinks", () => excel.AskToUpdateLinks = false);
            SetOptionalExcelProperty("ScreenUpdating", () => excel.ScreenUpdating = false);
            try
            {
                excel.AutomationSecurity = MacroSecurityForceDisable;
                WorkerLog.Write("info", "macros_disabled", "AutomationSecurity를 ForceDisable로 설정했습니다.");
            }
            catch (Exception exception)
            {
                WorkerLog.WriteException("macro_security_failed", stage, exception);
                return 12;
            }

            workbooksObject = excel.Workbooks;
            dynamic workbooks = workbooksObject;
            var initialWorkbookCount = Convert.ToInt32(workbooks.Count, CultureInfo.InvariantCulture);
            if (initialWorkbookCount != 0)
            {
                WorkerLog.Write("error", "unexpected_existing_workbooks",
                    "새 Excel 인스턴스에 이미 열린 통합문서가 있어 사용자 문서 보호를 위해 중단합니다.", new { initialWorkbookCount });
                return 13;
            }

            stage = "workbook_open";
            var stopwatch = Stopwatch.StartNew();
            workbookObject = workbooks.Open(
                Path.GetFullPath(options.FilePath), 0, true,
                Missing.Value, Missing.Value, Missing.Value, true,
                Missing.Value, Missing.Value, false, false,
                Missing.Value, false, true, 0);
            stopwatch.Stop();
            dynamic workbook = workbookObject;
            WorkerLog.Write("info", "workbook_opened", "통합문서를 읽기 전용으로 열었습니다.", new
            {
                elapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                name = SafeGet(() => (string?)workbook.Name),
                fullName = SafeGet(() => (string?)workbook.FullName),
                readOnly = SafeGet(() => (bool?)workbook.ReadOnly),
                saved = SafeGet(() => (bool?)workbook.Saved),
            });

            stage = "workbook_read";
            var summary = ReadWorkbook(workbook, options);
            WorkerLog.Write("info", "workbook_read_completed", "모든 워크시트의 시험 읽기를 완료했습니다.", summary);
            return 0;
        }
        catch (Exception exception)
        {
            WorkerLog.WriteException("probe_failed", stage, exception);
            if (excel is not null)
            {
                var protectedViewCount = SafeGet(() => Convert.ToInt32(excel.ProtectedViewWindows.Count, CultureInfo.InvariantCulture));
                WorkerLog.Write("info", "excel_state_after_failure", "실패 시점의 Excel 상태입니다.", new { protectedViewCount });
            }
            return stage == "workbook_open" ? 20 : 30;
        }
        finally
        {
            if (workbookObject is not null)
            {
                try
                {
                    dynamic workbook = workbookObject;
                    workbook.Close(false);
                    WorkerLog.Write("info", "workbook_closed", "저장하지 않고 시험 통합문서를 닫았습니다.");
                }
                catch (Exception exception) { WorkerLog.WriteException("workbook_close_failed", "cleanup", exception); }
            }

            if (excel is not null && ownsExcelProcess)
            {
                try
                {
                    excel.Quit();
                    WorkerLog.Write("info", "excel_quit_requested", "PoC가 생성한 Excel 인스턴스에 Quit을 요청했습니다.");
                }
                catch (Exception exception) { WorkerLog.WriteException("excel_quit_failed", "cleanup", exception); }
            }

            ReleaseComObject(workbookObject);
            ReleaseComObject(workbooksObject);
            ReleaseComObject(excel);
            WorkerLog.Write("info", "worker_cleanup_completed", "COM 참조 정리를 마쳤습니다.");
        }
    }

    private static object ReadWorkbook(dynamic workbook, WorkerOptions options)
    {
        object? worksheetsObject = null;
        using var workbookHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var sheetSummaries = new List<object>();
        long totalUsedCells = 0;
        long totalScannedCells = 0;
        long totalNonEmptyCells = 0;
        long totalFormulaCells = 0;

        try
        {
            worksheetsObject = workbook.Worksheets;
            dynamic worksheets = worksheetsObject;
            var count = Convert.ToInt32(worksheets.Count, CultureInfo.InvariantCulture);
            WorkerLog.Write("info", "worksheet_collection_read", "워크시트 목록을 읽었습니다.", new { worksheetCount = count });

            for (var index = 1; index <= count; index++)
            {
                object? worksheetObject = null;
                object? usedRangeObject = null;
                try
                {
                    worksheetObject = worksheets.Item[index];
                    dynamic worksheet = worksheetObject;
                    var sheetName = Convert.ToString(worksheet.Name, CultureInfo.InvariantCulture) ?? $"Sheet{index}";
                    var visibility = SafeGet(() => Convert.ToInt32(worksheet.Visible, CultureInfo.InvariantCulture));
                    usedRangeObject = worksheet.UsedRange;
                    dynamic usedRange = usedRangeObject;

                    var startRow = Convert.ToInt32(usedRange.Row, CultureInfo.InvariantCulture);
                    var startColumn = Convert.ToInt32(usedRange.Column, CultureInfo.InvariantCulture);
                    var rowCount = ReadComCollectionCount(usedRange.Rows);
                    var columnCount = ReadComCollectionCount(usedRange.Columns);
                    var usedCellCount = checked((long)rowCount * columnCount);
                    totalUsedCells += usedCellCount;

                    var sheet = usedCellCount <= options.MaxCells
                        ? ReadCompleteRange(usedRangeObject, sheetName, startRow, startColumn, rowCount, columnCount, options)
                        : ReadSampledRange(usedRangeObject, sheetName, startRow, startColumn, rowCount, columnCount, usedCellCount, options);

                    totalScannedCells += sheet.ScannedCells;
                    totalNonEmptyCells += sheet.NonEmptyCells;
                    totalFormulaCells += sheet.FormulaCells;
                    AppendHash(workbookHash, sheetName, sheet.ContentSha256);
                    var summary = new
                    {
                        index,
                        name = sheetName,
                        visibility,
                        usedRange = new { startRow, startColumn, rowCount, columnCount, usedCellCount },
                        scanComplete = sheet.Complete,
                        sheet.ScannedCells,
                        sheet.NonEmptyCells,
                        sheet.FormulaCells,
                        sheet.FormulaProperty,
                        sheet.TypeCounts,
                        contentSha256 = sheet.ContentSha256,
                        samples = sheet.Samples,
                    };
                    sheetSummaries.Add(summary);
                    WorkerLog.Write("info", "worksheet_read", "워크시트 범위와 셀 데이터를 읽었습니다.", summary);
                }
                finally
                {
                    ReleaseComObject(usedRangeObject);
                    ReleaseComObject(worksheetObject);
                }
            }

            return new
            {
                worksheetCount = count,
                totalUsedCells,
                totalScannedCells,
                totalNonEmptyCells,
                totalFormulaCells,
                workbookContentSha256 = Convert.ToHexString(workbookHash.GetHashAndReset()).ToLowerInvariant(),
                sheets = sheetSummaries,
            };
        }
        finally
        {
            ReleaseComObject(worksheetsObject);
        }
    }

    private static SheetReadResult ReadCompleteRange(
        object usedRangeObject,
        string sheetName,
        int startRow,
        int startColumn,
        int rowCount,
        int columnCount,
        WorkerOptions options)
    {
        dynamic usedRange = usedRangeObject;
        object? values = usedRange.Value2;
        var (formulas, formulaProperty) = ReadFormulaValue(usedRangeObject);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var typeCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        var samples = new List<object>();
        long nonEmpty = 0;
        long formulaCells = 0;

        for (var row = 0; row < rowCount; row++)
        {
            for (var column = 0; column < columnCount; column++)
            {
                var value = ReadVariant(values, row, column, rowCount, columnCount);
                var formula = ReadVariant(formulas, row, column, rowCount, columnCount);
                var valueText = ToInvariantText(value);
                var formulaText = ToInvariantText(formula);
                var hasFormula = formulaText.StartsWith('=');
                if (string.IsNullOrEmpty(valueText) && !hasFormula) continue;

                nonEmpty++;
                if (hasFormula) formulaCells++;
                var typeName = value?.GetType().Name ?? "null";
                typeCounts[typeName] = typeCounts.GetValueOrDefault(typeName) + 1;
                var address = CellAddress(startRow + row, startColumn + column);
                AppendHash(hash, sheetName, address, typeName, valueText, hasFormula ? formulaText : string.Empty);
                AddSample(samples, options, address, typeName, valueText, hasFormula ? formulaText : null);
            }
        }

        return new SheetReadResult(
            Complete: true,
            ScannedCells: checked((long)rowCount * columnCount),
            NonEmptyCells: nonEmpty,
            FormulaCells: formulaCells,
            FormulaProperty: formulaProperty,
            ContentSha256: Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            TypeCounts: typeCounts,
            Samples: samples);
    }

    private static SheetReadResult ReadSampledRange(
        object usedRangeObject,
        string sheetName,
        int startRow,
        int startColumn,
        int rowCount,
        int columnCount,
        long usedCellCount,
        WorkerOptions options)
    {
        dynamic usedRange = usedRangeObject;
        WorkerLog.Write("warning", "worksheet_range_too_large",
            "UsedRange가 최대 전체 스캔 셀 수를 넘어 모서리와 중앙 셀만 시험합니다. 필요하면 --max-cells를 늘리세요.",
            new { sheetName, usedCellCount, options.MaxCells });

        var positions = new HashSet<(int Row, int Column)>
        {
            (1, 1),
            (1, columnCount),
            (rowCount, 1),
            (rowCount, columnCount),
            ((rowCount + 1) / 2, (columnCount + 1) / 2),
        };
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var typeCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        var samples = new List<object>();
        long nonEmpty = 0;
        long formulaCells = 0;
        var formulaProperty = "Formula2/Formula per cell";

        foreach (var position in positions)
        {
            object? cellsObject = null;
            object? cellObject = null;
            try
            {
                cellsObject = usedRange.Cells;
                dynamic cells = cellsObject;
                cellObject = cells.Item[position.Row, position.Column];
                dynamic cell = cellObject;
                object? value = cell.Value2;
                var (formula, property) = ReadFormulaValue(cellObject);
                formulaProperty = property;
                var valueText = ToInvariantText(value);
                var formulaText = ToInvariantText(formula);
                var hasFormula = formulaText.StartsWith('=');
                if (string.IsNullOrEmpty(valueText) && !hasFormula) continue;

                nonEmpty++;
                if (hasFormula) formulaCells++;
                var typeName = value?.GetType().Name ?? "null";
                typeCounts[typeName] = typeCounts.GetValueOrDefault(typeName) + 1;
                var address = CellAddress(startRow + position.Row - 1, startColumn + position.Column - 1);
                AppendHash(hash, sheetName, address, typeName, valueText, hasFormula ? formulaText : string.Empty);
                AddSample(samples, options, address, typeName, valueText, hasFormula ? formulaText : null);
            }
            finally
            {
                ReleaseComObject(cellObject);
                ReleaseComObject(cellsObject);
            }
        }

        return new SheetReadResult(
            Complete: false,
            ScannedCells: positions.Count,
            NonEmptyCells: nonEmpty,
            FormulaCells: formulaCells,
            FormulaProperty: formulaProperty,
            ContentSha256: Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            TypeCounts: typeCounts,
            Samples: samples);
    }

    private static (object? Value, string Property) ReadFormulaValue(object rangeObject)
    {
        dynamic range = rangeObject;
        try { return ((object?)range.Formula2, "Formula2"); }
        catch { return ((object?)range.Formula, "Formula"); }
    }

    private static object? ReadVariant(object? value, int row, int column, int rowCount, int columnCount)
    {
        if (value is not Array array) return row == 0 && column == 0 ? value : null;
        if (array.Rank != 2) return null;
        var rowIndex = array.GetLowerBound(0) + row;
        var columnIndex = array.GetLowerBound(1) + column;
        if (row >= rowCount || column >= columnCount) return null;
        return array.GetValue(rowIndex, columnIndex);
    }

    private static void AddSample(List<object> samples, WorkerOptions options, string address, string type, string value, string? formula)
    {
        if (!options.IncludeValues || samples.Count >= options.SampleLimit) return;
        samples.Add(new
        {
            address,
            type,
            value = TruncateForLog(value),
            formula = formula is null ? null : TruncateForLog(formula),
        });
    }

    private static string TruncateForLog(string value)
    {
        var clean = value.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        return clean.Length <= 160 ? clean : clean[..160] + "…";
    }

    private static string ToInvariantText(object? value) => value switch
    {
        null => string.Empty,
        string text => text,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => value.ToString() ?? string.Empty,
    };

    private static void AppendHash(IncrementalHash hash, params string[] parts)
    {
        foreach (var part in parts)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(part));
            hash.AppendData(new byte[] { 0 });
        }
        hash.AppendData(new byte[] { (byte)'\n' });
    }

    private static int ReadComCollectionCount(object collectionObject)
    {
        try
        {
            dynamic collection = collectionObject;
            return Convert.ToInt32(collection.Count, CultureInfo.InvariantCulture);
        }
        finally
        {
            ReleaseComObject(collectionObject);
        }
    }

    private static string CellAddress(int row, int column)
    {
        var name = new StringBuilder();
        for (var value = column; value > 0; value = (value - 1) / 26)
            name.Insert(0, (char)('A' + ((value - 1) % 26)));
        return name.Append(row.ToString(CultureInfo.InvariantCulture)).ToString();
    }

    private static int GetExcelProcessId(dynamic excel)
    {
        try
        {
            var hwnd = new IntPtr(Convert.ToInt64(excel.Hwnd, CultureInfo.InvariantCulture));
            _ = GetWindowThreadProcessId(hwnd, out var processId);
            return checked((int)processId);
        }
        catch
        {
            return 0;
        }
    }

    private static void SetOptionalExcelProperty(string name, Action setter)
    {
        try
        {
            setter();
            WorkerLog.Write("info", "excel_property_set", $"Excel.{name} 설정을 적용했습니다.", new { property = name });
        }
        catch (Exception exception)
        {
            WorkerLog.Write("warning", "excel_property_set_failed", $"Excel.{name} 설정을 적용하지 못했습니다.", ExceptionData(exception, "configuration"));
        }
    }

    private static T? SafeGet<T>(Func<T> getter)
    {
        try { return getter(); }
        catch { return default; }
    }

    private static int[] GetExcelProcessIds()
    {
        var processes = Process.GetProcessesByName("EXCEL");
        try { return processes.Select(process => process.Id).OrderBy(id => id).ToArray(); }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private static void CleanupOwnedExcelProcess(int processId, IReadOnlyCollection<int> baselineExcelPids, ControllerLog log)
    {
        if (processId <= 0 || baselineExcelPids.Contains(processId)) return;
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited) return;
            if (!process.ProcessName.Equals("EXCEL", StringComparison.OrdinalIgnoreCase))
            {
                log.Write("warning", "cleanup_pid_mismatch", "기록된 PID가 Excel 프로세스가 아니어서 종료하지 않았습니다.", new { processId, process.ProcessName });
                return;
            }

            if (process.WaitForExit(5_000)) return;
            log.Write("warning", "owned_excel_force_cleanup", "PoC가 생성한 Excel 프로세스가 남아 있어 강제 종료합니다.", new { processId });
            process.Kill(entireProcessTree: false);
            process.WaitForExit(5_000);
        }
        catch (ArgumentException)
        {
            // The owned process already exited.
        }
        catch (Exception exception)
        {
            log.WriteException("owned_excel_cleanup_failed", exception);
        }
    }

    private static object ExceptionData(Exception exception, string stage) => new
    {
        stage,
        type = exception.GetType().FullName,
        exception.Message,
        hresult = $"0x{exception.HResult:X8}",
        stackTrace = exception.StackTrace,
        inner = exception.InnerException is null ? null : new
        {
            type = exception.InnerException.GetType().FullName,
            exception.InnerException.Message,
            hresult = $"0x{exception.InnerException.HResult:X8}",
        },
    };

    private static string CreateDefaultLogPath()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Hdiff", "ExcelComReadProbe", "Logs");
        return Path.Combine(directory, $"excel-com-probe-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.jsonl");
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is null || !Marshal.IsComObject(value)) return;
        try { Marshal.FinalReleaseComObject(value); } catch { }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Excel DRM 환경 COM 읽기 PoC (정상 Excel 자동화 권한만 시험하며 DRM을 변형하거나 우회하지 않습니다.)");
        Console.WriteLine();
        Console.WriteLine("사용법:");
        Console.WriteLine("  Hdiff.ExcelComReadProbe.exe \"C:\\문서\\시험.xlsx\"");
        Console.WriteLine("  Hdiff.ExcelComReadProbe.exe \"C:\\문서\\시험.xlsx\" --log \"C:\\Temp\\excel-probe.jsonl\"");
        Console.WriteLine();
        Console.WriteLine("옵션:");
        Console.WriteLine("  --timeout-seconds N   COM 제한 시간(기본 90초)");
        Console.WriteLine("  --max-cells N         시트별 전체 스캔 최대 셀 수(기본 1,000,000)");
        Console.WriteLine("  --include-values      최대 N개 셀의 값/수식 미리보기를 로그에 포함(민감정보 주의)");
        Console.WriteLine("  --sample-limit N      값 미리보기 최대 개수(기본 20)");
        Console.WriteLine("  --log PATH            JSON Lines 로그 저장 경로");
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    private sealed record ControllerOptions(
        string FilePath,
        string? LogPath,
        int TimeoutSeconds,
        long MaxCells,
        bool IncludeValues,
        int SampleLimit)
    {
        public static ControllerOptions Parse(string[] args)
        {
            string? filePath = null;
            string? logPath = null;
            var timeout = 90;
            long maxCells = 1_000_000;
            var includeValues = false;
            var sampleLimit = 20;

            for (var index = 0; index < args.Length; index++)
            {
                var argument = args[index];
                switch (argument.ToLowerInvariant())
                {
                    case "--log": logPath = RequireValue(args, ref index, argument); break;
                    case "--timeout-seconds": timeout = ParseInt(RequireValue(args, ref index, argument), argument, 5, 600); break;
                    case "--max-cells": maxCells = ParseLong(RequireValue(args, ref index, argument), argument, 1, 20_000_000); break;
                    case "--sample-limit": sampleLimit = ParseInt(RequireValue(args, ref index, argument), argument, 0, 200); break;
                    case "--include-values": includeValues = true; break;
                    default:
                        if (argument.StartsWith('-')) throw new ArgumentException($"알 수 없는 옵션입니다: {argument}");
                        if (filePath is not null) throw new ArgumentException("시험 파일은 하나만 지정할 수 있습니다.");
                        filePath = argument;
                        break;
                }
            }

            return new ControllerOptions(filePath ?? throw new ArgumentException("시험할 Excel 파일 경로가 필요합니다."),
                logPath, timeout, maxCells, includeValues, sampleLimit);
        }
    }

    private sealed record WorkerOptions(
        string FilePath,
        long MaxCells,
        bool IncludeValues,
        int SampleLimit,
        HashSet<int> BaselineExcelProcessIds)
    {
        public static WorkerOptions Parse(string[] args)
        {
            string? filePath = null;
            long maxCells = 1_000_000;
            var includeValues = false;
            var sampleLimit = 20;
            var baseline = new HashSet<int>();

            for (var index = 0; index < args.Length; index++)
            {
                var argument = args[index];
                switch (argument.ToLowerInvariant())
                {
                    case "--worker": break;
                    case "--file": filePath = RequireValue(args, ref index, argument); break;
                    case "--max-cells": maxCells = ParseLong(RequireValue(args, ref index, argument), argument, 1, 20_000_000); break;
                    case "--sample-limit": sampleLimit = ParseInt(RequireValue(args, ref index, argument), argument, 0, 200); break;
                    case "--include-values": includeValues = true; break;
                    case "--baseline-excel-pids":
                        foreach (var value in RequireValue(args, ref index, argument).Split(',', StringSplitOptions.RemoveEmptyEntries))
                            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) baseline.Add(pid);
                        break;
                }
            }

            return new WorkerOptions(filePath ?? throw new ArgumentException("워커 파일 경로가 없습니다."),
                maxCells, includeValues, sampleLimit, baseline);
        }
    }

    private sealed record SheetReadResult(
        bool Complete,
        long ScannedCells,
        long NonEmptyCells,
        long FormulaCells,
        string FormulaProperty,
        string ContentSha256,
        IReadOnlyDictionary<string, long> TypeCounts,
        IReadOnlyList<object> Samples);

    private sealed class ControllerLog : IDisposable
    {
        private readonly object _gate = new();
        private readonly StreamWriter _writer;

        public ControllerLog(string path)
        {
            _writer = new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        }

        public void Write(string level, string eventName, string message, object? data = null)
        {
            var line = WorkerLog.CreateLine(level, eventName, message, data);
            WriteWorkerLine(line);
        }

        public void WriteException(string eventName, Exception exception) =>
            Write("error", eventName, exception.Message, ExceptionData(exception, "controller"));

        public void WriteWorkerLine(string line)
        {
            lock (_gate)
            {
                _writer.WriteLine(line);
                Console.WriteLine(FormatForConsole(line));
            }
        }

        public void Dispose() => _writer.Dispose();

        private static string FormatForConsole(string line)
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                var timestamp = root.GetProperty("timestamp").GetDateTimeOffset().ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                var level = root.GetProperty("level").GetString()?.ToUpperInvariant();
                var eventName = root.GetProperty("event").GetString();
                var message = root.GetProperty("message").GetString();
                return $"[{timestamp}] {level,-7} {eventName}: {message}";
            }
            catch
            {
                return line;
            }
        }
    }

    private static class WorkerLog
    {
        public static void Write(string level, string eventName, string message, object? data = null) =>
            Console.WriteLine(CreateLine(level, eventName, message, data));

        public static void WriteException(string eventName, string stage, Exception exception) =>
            Write("error", eventName, exception.Message, ExceptionData(exception, stage));

        public static string CreateLine(string level, string eventName, string message, object? data = null) =>
            JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.Now,
                processId = Environment.ProcessId,
                level,
                @event = eventName,
                message,
                data,
            });
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length) throw new ArgumentException($"{option} 뒤에 값이 필요합니다.");
        return args[index];
    }

    private static int ParseInt(string value, string option, int minimum, int maximum)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < minimum || parsed > maximum)
            throw new ArgumentOutOfRangeException(option, $"{option} 값은 {minimum:N0}~{maximum:N0}이어야 합니다.");
        return parsed;
    }

    private static long ParseLong(string value, string option, long minimum, long maximum)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < minimum || parsed > maximum)
            throw new ArgumentOutOfRangeException(option, $"{option} 값은 {minimum:N0}~{maximum:N0}이어야 합니다.");
        return parsed;
    }
}
