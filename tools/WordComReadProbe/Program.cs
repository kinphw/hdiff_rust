using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hdiff.WordComReadProbe;

internal static partial class Program
{
    private const int MacroSecurityForceDisable = 3;
    private const int AlertsNone = 0;
    private const int DoNotSaveChanges = 0;
    private const int WithInTable = 12;
    private const int StatisticPages = 2;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        if (args.Contains("--worker", StringComparer.OrdinalIgnoreCase))
            return RunWorker(WorkerOptions.Parse(args));

        if (args.Length == 0
            || args.Contains("--help", StringComparer.OrdinalIgnoreCase)
            || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
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
        if (!File.Exists(fullPath)) throw new FileNotFoundException("시험할 Word 파일을 찾지 못했습니다.", fullPath);

        var logPath = options.LogPath is null ? CreateDefaultLogPath() : Path.GetFullPath(options.LogPath);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? Directory.GetCurrentDirectory());

        using var log = new ControllerLog(logPath);
        var file = new FileInfo(fullPath);
        var baselineWordPids = GetWordProcessIds();
        log.Write("info", "probe_started", "Word COM 읽기 PoC를 시작합니다.", new
        {
            targetPath = fullPath,
            file.Extension,
            file.Length,
            lastWriteUtc = file.LastWriteTimeUtc,
            options.TimeoutSeconds,
            options.IncludeText,
            options.SampleLimit,
            baselineWordPids,
            os = RuntimeInformation.OSDescription,
            runtime = RuntimeInformation.FrameworkDescription,
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
        });
        if (options.IncludeText)
            log.Write("warning", "sensitive_logging_enabled",
                "본문과 표 행 미리보기가 로그에 기록됩니다. 외부 전달 전에 내용을 확인하세요.");

        var startInfo = CreateWorkerStartInfo(fullPath, options, baselineWordPids);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("PoC 워커를 시작하지 못했습니다.");
        var createdWordPid = 0;

        var stdoutPump = PumpAsync(process.StandardOutput, line =>
        {
            log.WriteWorkerLine(line);
            if (TryReadWordProcessId(line, out var processId))
                Interlocked.Exchange(ref createdWordPid, processId);
        });
        var stderrPump = PumpAsync(process.StandardError, line =>
            log.Write("error", "worker_stderr", line));

        var waitTask = process.WaitForExitAsync();
        var completed = await Task.WhenAny(
            waitTask,
            Task.Delay(TimeSpan.FromSeconds(options.TimeoutSeconds))).ConfigureAwait(false);
        var timedOut = completed != waitTask;
        if (timedOut)
        {
            log.Write("error", "probe_timeout", "Word COM 호출이 제한 시간 안에 끝나지 않아 PoC 워커를 종료합니다.", new
            {
                options.TimeoutSeconds,
                workerProcessId = process.Id,
                wordProcessId = Volatile.Read(ref createdWordPid),
            });
            try { process.Kill(entireProcessTree: false); }
            catch (Exception exception) { log.WriteException("worker_kill_failed", exception); }
        }

        try { await waitTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); } catch { }
        await Task.WhenAll(stdoutPump, stderrPump).ConfigureAwait(false);

        CleanupOwnedWordProcess(Volatile.Read(ref createdWordPid), baselineWordPids, log);
        var exitCode = timedOut ? 124 : process.ExitCode;
        log.Write(exitCode == 0 ? "info" : "error", "probe_finished",
            exitCode == 0
                ? "Word COM에서 문서 본문과 표 행을 읽었습니다."
                : "Word COM 읽기 PoC가 실패했습니다.",
            new { exitCode, logPath });

        Console.WriteLine();
        Console.WriteLine($"로그 파일: {logPath}");
        return exitCode;
    }

    private static ProcessStartInfo CreateWorkerStartInfo(
        string filePath,
        ControllerOptions options,
        int[] baselineWordPids)
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
        {
            var assemblyName = Assembly.GetEntryAssembly()?.GetName().Name
                ?? throw new InvalidOperationException("PoC 어셈블리 이름을 찾지 못했습니다.");
            startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll"));
        }

        startInfo.ArgumentList.Add("--worker");
        startInfo.ArgumentList.Add("--file");
        startInfo.ArgumentList.Add(filePath);
        startInfo.ArgumentList.Add("--sample-limit");
        startInfo.ArgumentList.Add(options.SampleLimit.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--baseline-word-pids");
        startInfo.ArgumentList.Add(string.Join(',', baselineWordPids));
        if (options.IncludeText) startInfo.ArgumentList.Add("--include-text");
        return startInfo;
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> consume)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            consume(line);
    }

    private static bool TryReadWordProcessId(string line, out int processId)
    {
        processId = 0;
        try
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            return root.TryGetProperty("event", out var eventName)
                && eventName.GetString() == "word_instance_created"
                && root.TryGetProperty("data", out var data)
                && data.TryGetProperty("wordProcessId", out var pid)
                && pid.TryGetInt32(out processId);
        }
        catch
        {
            return false;
        }
    }

    private static int RunWorker(WorkerOptions options)
    {
        dynamic? word = null;
        object? wordOptionsObject = null;
        object? documentsObject = null;
        object? documentObject = null;
        var ownsWordProcess = false;
        var stage = "word_activation";

        try
        {
            WorkerLog.Write("info", "worker_started", "격리된 Word COM 워커를 시작했습니다.", new
            {
                workerProcessId = Environment.ProcessId,
                options.FilePath,
                options.IncludeText,
                options.SampleLimit,
            });

            var wordType = Type.GetTypeFromProgID("Word.Application");
            if (wordType is null)
            {
                WorkerLog.Write("error", "word_not_registered",
                    "Word.Application COM 클래스가 등록되어 있지 않습니다.");
                return 10;
            }

            word = Activator.CreateInstance(wordType)
                ?? throw new InvalidOperationException("Word COM 객체를 생성하지 못했습니다.");
            var wordProcessId = GetWordProcessId(word, options.BaselineWordProcessIds);
            ownsWordProcess = wordProcessId > 0 && !options.BaselineWordProcessIds.Contains(wordProcessId);
            WorkerLog.Write("info", "word_instance_created", "Word COM 인스턴스를 생성했습니다.", new
            {
                wordProcessId,
                isolatedProcess = ownsWordProcess,
                version = SafeGet(() => Convert.ToString(word.Version, CultureInfo.InvariantCulture)),
                build = SafeGet(() => Convert.ToString(word.Build, CultureInfo.InvariantCulture)),
            });

            if (!ownsWordProcess)
            {
                WorkerLog.Write("error", "word_instance_not_isolated",
                    "새 COM 객체가 기존 Word 프로세스에 연결되었거나 PID를 확인하지 못해 사용자 Word 보호를 위해 중단합니다.");
                return 11;
            }

            stage = "word_security_configuration";
            SetOptionalWordProperty("Visible", () => word.Visible = false);
            SetOptionalWordProperty("DisplayAlerts", () => word.DisplayAlerts = AlertsNone);
            SetOptionalWordProperty("ScreenUpdating", () => word.ScreenUpdating = false);
            try
            {
                word.AutomationSecurity = MacroSecurityForceDisable;
                WorkerLog.Write("info", "macros_disabled",
                    "AutomationSecurity를 ForceDisable로 설정했습니다.");
            }
            catch (Exception exception)
            {
                WorkerLog.WriteException("macro_security_failed", stage, exception);
                return 12;
            }

            wordOptionsObject = word.Options;
            dynamic wordOptions = wordOptionsObject;
            SetOptionalWordProperty("Options.UpdateLinksAtOpen", () => wordOptions.UpdateLinksAtOpen = false);

            documentsObject = word.Documents;
            dynamic documents = documentsObject;
            var initialDocumentCount = Convert.ToInt32(documents.Count, CultureInfo.InvariantCulture);
            if (initialDocumentCount != 0)
            {
                WorkerLog.Write("error", "unexpected_existing_documents",
                    "새 Word 인스턴스에 이미 열린 문서가 있어 사용자 문서 보호를 위해 중단합니다.",
                    new { initialDocumentCount });
                return 13;
            }

            stage = "document_open";
            var stopwatch = Stopwatch.StartNew();
            documentObject = documents.Open(
                FileName: Path.GetFullPath(options.FilePath),
                ConfirmConversions: false,
                ReadOnly: true,
                AddToRecentFiles: false,
                Revert: false,
                Visible: false,
                OpenAndRepair: false,
                NoEncodingDialog: true);
            stopwatch.Stop();
            dynamic document = documentObject;
            var readOnly = SafeGet(() => (bool?)document.ReadOnly);
            WorkerLog.Write("info", "document_opened", "문서를 읽기 전용으로 열었습니다.", new
            {
                elapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                name = SafeGet(() => (string?)document.Name),
                fullName = SafeGet(() => (string?)document.FullName),
                readOnly,
                saved = SafeGet(() => (bool?)document.Saved),
                protectionType = SafeGet(() => Convert.ToInt32(document.ProtectionType, CultureInfo.InvariantCulture)),
                compatibilityMode = SafeGet(() => Convert.ToInt32(document.CompatibilityMode, CultureInfo.InvariantCulture)),
            });
            if (readOnly != true)
            {
                WorkerLog.Write("error", "document_not_read_only",
                    "Word가 문서를 읽기 전용으로 열지 않아 원본 보호를 위해 내용 읽기를 중단합니다.");
                return 14;
            }

            stage = "document_read";
            var summary = ReadDocument(document, options);
            WorkerLog.Write("info", "document_read_completed",
                "본문 문단과 표 행의 시험 읽기를 완료했습니다.", summary);
            return 0;
        }
        catch (Exception exception)
        {
            WorkerLog.WriteException("probe_failed", stage, exception);
            if (word is not null)
            {
                var protectedViewCount = SafeGet(() =>
                    Convert.ToInt32(word.ProtectedViewWindows.Count, CultureInfo.InvariantCulture));
                WorkerLog.Write("info", "word_state_after_failure",
                    "실패 시점의 Word 상태입니다.", new { protectedViewCount });
            }
            return stage == "document_open" ? 20 : 30;
        }
        finally
        {
            if (documentObject is not null)
            {
                try
                {
                    dynamic document = documentObject;
                    document.Close(DoNotSaveChanges);
                    WorkerLog.Write("info", "document_closed",
                        "저장하지 않고 시험 문서를 닫았습니다.");
                }
                catch (Exception exception)
                {
                    WorkerLog.WriteException("document_close_failed", "cleanup", exception);
                }
            }

            if (word is not null && ownsWordProcess)
            {
                try
                {
                    word.Quit(DoNotSaveChanges);
                    WorkerLog.Write("info", "word_quit_requested",
                        "PoC가 생성한 Word 인스턴스에 Quit을 요청했습니다.");
                }
                catch (Exception exception)
                {
                    WorkerLog.WriteException("word_quit_failed", "cleanup", exception);
                }
            }

            ReleaseComObject(documentObject);
            ReleaseComObject(documentsObject);
            ReleaseComObject(wordOptionsObject);
            ReleaseComObject(word);
            WorkerLog.Write("info", "worker_cleanup_completed", "COM 참조 정리를 마쳤습니다.");
        }
    }

    private static object ReadDocument(dynamic document, WorkerOptions options)
    {
        object? paragraphsObject = null;
        object? tablesObject = null;
        using var documentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var candidates = new List<DocumentCandidate>();
        var samples = new List<object>();
        var tableSummaries = new List<object>();
        var paragraphCount = 0;
        var tableRowCount = 0;

        try
        {
            paragraphsObject = document.Paragraphs;
            dynamic paragraphs = paragraphsObject;
            var rawParagraphCount = Convert.ToInt32(paragraphs.Count, CultureInfo.InvariantCulture);
            for (var index = 1; index <= rawParagraphCount; index++)
            {
                object? paragraphObject = null;
                object? rangeObject = null;
                try
                {
                    paragraphObject = paragraphs.Item(index);
                    dynamic paragraph = paragraphObject;
                    rangeObject = paragraph.Range;
                    dynamic range = rangeObject;
                    var inTable = SafeGet(() => Convert.ToBoolean(range.Information[WithInTable], CultureInfo.InvariantCulture)) == true;
                    if (inTable) continue;

                    var text = CleanParagraph(Convert.ToString(range.Text, CultureInfo.InvariantCulture));
                    if (text.Length == 0) continue;
                    var start = Convert.ToInt32(range.Start, CultureInfo.InvariantCulture);
                    candidates.Add(new DocumentCandidate(start, CandidateKind.Paragraph, index, text));
                }
                finally
                {
                    ReleaseComObject(rangeObject);
                    ReleaseComObject(paragraphObject);
                }
            }

            tablesObject = document.Tables;
            dynamic tables = tablesObject;
            var rawTableCount = Convert.ToInt32(tables.Count, CultureInfo.InvariantCulture);
            for (var index = 1; index <= rawTableCount; index++)
            {
                object? tableObject = null;
                object? rangeObject = null;
                try
                {
                    tableObject = tables.Item(index);
                    dynamic table = tableObject;
                    var nestingLevel = SafeGet(() => Convert.ToInt32(table.NestingLevel, CultureInfo.InvariantCulture)) ?? 1;
                    if (nestingLevel != 1) continue;
                    rangeObject = table.Range;
                    dynamic range = rangeObject;
                    var start = Convert.ToInt32(range.Start, CultureInfo.InvariantCulture);
                    candidates.Add(new DocumentCandidate(start, CandidateKind.Table, index, null));
                }
                finally
                {
                    ReleaseComObject(rangeObject);
                    ReleaseComObject(tableObject);
                }
            }

            foreach (var candidate in candidates.OrderBy(candidate => candidate.Start))
            {
                if (candidate.Kind == CandidateKind.Paragraph)
                {
                    paragraphCount++;
                    AppendHash(documentHash, "P", candidate.Text ?? string.Empty);
                    AddSample(samples, options, "paragraph", paragraphCount, candidate.Text ?? string.Empty);
                    continue;
                }

                object? tableObject = null;
                try
                {
                    tableObject = tables.Item(candidate.CollectionIndex);
                    var tableResult = ReadTable(tableObject, candidate.CollectionIndex);
                    tableRowCount += tableResult.Rows.Count;
                    foreach (var row in tableResult.Rows)
                    {
                        AppendHash(documentHash, "T", row);
                        AddSample(samples, options, "table_row", tableRowCount - tableResult.Rows.Count + tableResult.Rows.IndexOf(row) + 1, row);
                    }

                    var summary = new
                    {
                        index = candidate.CollectionIndex,
                        tableResult.ReadMode,
                        tableResult.RowCountReported,
                        tableResult.ColumnCountReported,
                        rowsRead = tableResult.Rows.Count,
                        contentSha256 = HashLines(tableResult.Rows),
                        samples = options.IncludeText
                            ? tableResult.Rows.Take(Math.Min(options.SampleLimit, 10)).ToArray()
                            : Array.Empty<string>(),
                    };
                    tableSummaries.Add(summary);
                    WorkerLog.Write("info", "table_read",
                        "표의 각 행을 셀 경계와 함께 한 줄로 읽었습니다.", summary);
                }
                catch (Exception exception)
                {
                    WorkerLog.WriteException("table_read_failed", "document_read", exception);
                    throw;
                }
                finally
                {
                    ReleaseComObject(tableObject);
                }
            }

            return new
            {
                paragraphCollectionCount = Convert.ToInt32(paragraphs.Count, CultureInfo.InvariantCulture),
                tableCollectionCount = Convert.ToInt32(tables.Count, CultureInfo.InvariantCulture),
                bodyParagraphsRead = paragraphCount,
                topLevelTablesRead = tableSummaries.Count,
                tableRowsRead = tableRowCount,
                orderedComparisonLines = paragraphCount + tableRowCount,
                pageCount = SafeGet(() => Convert.ToInt32(document.ComputeStatistics(StatisticPages, false), CultureInfo.InvariantCulture)),
                wordCount = ReadCollectionCount(() => document.Words),
                characterCount = ReadCollectionCount(() => document.Characters),
                documentContentSha256 = Convert.ToHexString(documentHash.GetHashAndReset()).ToLowerInvariant(),
                tables = tableSummaries,
                samples,
            };
        }
        finally
        {
            ReleaseComObject(tablesObject);
            ReleaseComObject(paragraphsObject);
        }
    }

    private static TableReadResult ReadTable(object tableObject, int tableIndex)
    {
        dynamic table = tableObject;
        var reportedRows = ReadCollectionCount(() => table.Rows);
        var reportedColumns = ReadCollectionCount(() => table.Columns);

        try
        {
            return new TableReadResult(
                "Rows",
                reportedRows,
                reportedColumns,
                ReadTableByRows(tableObject));
        }
        catch (Exception rowException)
        {
            WorkerLog.Write("warning", "table_row_collection_fallback",
                "병합 셀 때문에 행 컬렉션을 읽지 못해 셀 좌표 기반으로 다시 읽습니다.",
                new
                {
                    tableIndex,
                    type = rowException.GetType().FullName,
                    rowException.Message,
                    hresult = $"0x{rowException.HResult:X8}",
                });
            return new TableReadResult(
                "CellCoordinates",
                reportedRows,
                reportedColumns,
                ReadTableByCellCoordinates(tableObject));
        }
    }

    private static List<string> ReadTableByRows(object tableObject)
    {
        dynamic table = tableObject;
        object? rowsObject = null;
        var result = new List<string>();
        try
        {
            rowsObject = table.Rows;
            dynamic rows = rowsObject;
            var rowCount = Convert.ToInt32(rows.Count, CultureInfo.InvariantCulture);
            for (var rowIndex = 1; rowIndex <= rowCount; rowIndex++)
            {
                object? rowObject = null;
                object? cellsObject = null;
                try
                {
                    rowObject = rows.Item(rowIndex);
                    dynamic row = rowObject;
                    cellsObject = row.Cells;
                    dynamic cells = cellsObject;
                    var cellCount = Convert.ToInt32(cells.Count, CultureInfo.InvariantCulture);
                    var values = new string[cellCount];
                    for (var cellIndex = 1; cellIndex <= cellCount; cellIndex++)
                        values[cellIndex - 1] = ReadCellText(cells, cellIndex);
                    result.Add(string.Join(" | ", values));
                }
                finally
                {
                    ReleaseComObject(cellsObject);
                    ReleaseComObject(rowObject);
                }
            }
            return result;
        }
        finally
        {
            ReleaseComObject(rowsObject);
        }
    }

    private static List<string> ReadTableByCellCoordinates(object tableObject)
    {
        dynamic table = tableObject;
        object? tableRangeObject = null;
        object? cellsObject = null;
        var rows = new SortedDictionary<int, SortedDictionary<int, string>>();
        try
        {
            tableRangeObject = table.Range;
            dynamic tableRange = tableRangeObject;
            cellsObject = tableRange.Cells;
            dynamic cells = cellsObject;
            var count = Convert.ToInt32(cells.Count, CultureInfo.InvariantCulture);
            for (var index = 1; index <= count; index++)
            {
                object? cellObject = null;
                object? rangeObject = null;
                try
                {
                    cellObject = cells.Item(index);
                    dynamic cell = cellObject;
                    int row = Convert.ToInt32((object)cell.RowIndex, CultureInfo.InvariantCulture);
                    int column = Convert.ToInt32((object)cell.ColumnIndex, CultureInfo.InvariantCulture);
                    rangeObject = cell.Range;
                    dynamic range = rangeObject;
                    var text = CleanCellText(Convert.ToString(range.Text, CultureInfo.InvariantCulture));
                    if (!rows.TryGetValue(row, out var columns))
                    {
                        columns = new SortedDictionary<int, string>();
                        rows.Add(row, columns);
                    }
                    columns[column] = text;
                }
                finally
                {
                    ReleaseComObject(rangeObject);
                    ReleaseComObject(cellObject);
                }
            }
        }
        finally
        {
            ReleaseComObject(cellsObject);
            ReleaseComObject(tableRangeObject);
        }

        var result = new List<string>(rows.Count);
        foreach (var columns in rows.Values)
        {
            var lastColumn = columns.Keys.DefaultIfEmpty(0).Max();
            var values = new string[lastColumn];
            foreach (var (column, text) in columns)
                values[column - 1] = text;
            result.Add(string.Join(" | ", values));
        }
        return result;
    }

    private static string ReadCellText(dynamic cells, int index)
    {
        object? cellObject = null;
        object? rangeObject = null;
        try
        {
            cellObject = cells.Item(index);
            dynamic cell = cellObject;
            rangeObject = cell.Range;
            dynamic range = rangeObject;
            return CleanCellText(Convert.ToString(range.Text, CultureInfo.InvariantCulture));
        }
        finally
        {
            ReleaseComObject(rangeObject);
            ReleaseComObject(cellObject);
        }
    }

    private static int? ReadCollectionCount(Func<object> getCollection)
    {
        object? collectionObject = null;
        try
        {
            collectionObject = getCollection();
            dynamic collection = collectionObject;
            return Convert.ToInt32(collection.Count, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseComObject(collectionObject);
        }
    }

    private static string CleanParagraph(string? text) =>
        NormalizeWhitespace((text ?? string.Empty).TrimEnd('\r', '\a', '\v', '\f'));

    private static string CleanCellText(string? text) =>
        NormalizeWhitespace((text ?? string.Empty)
            .Replace('\a', ' ')
            .Replace('\r', ' ')
            .Replace('\v', ' ')
            .Replace('\f', ' '));

    private static string NormalizeWhitespace(string text) =>
        WhitespaceRegex().Replace(text, " ").Trim();

    private static void AddSample(
        List<object> samples,
        WorkerOptions options,
        string kind,
        int index,
        string text)
    {
        if (!options.IncludeText || samples.Count >= options.SampleLimit) return;
        samples.Add(new { kind, index, text });
    }

    private static void AppendHash(IncrementalHash hash, string kind, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(kind + "\0" + text + "\n");
        hash.AppendData(bytes);
    }

    private static string HashLines(IEnumerable<string> lines)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var line in lines) AppendHash(hash, "T", line);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void SetOptionalWordProperty(string property, Action set)
    {
        try
        {
            set();
            WorkerLog.Write("info", "word_property_set",
                $"{property} 안전 설정을 적용했습니다.", new { property });
        }
        catch (Exception exception)
        {
            WorkerLog.Write("warning", "word_property_set_failed",
                $"{property} 설정을 적용하지 못했습니다.", new
                {
                    property,
                    type = exception.GetType().FullName,
                    exception.Message,
                    hresult = $"0x{exception.HResult:X8}",
                });
        }
    }

    private static int GetWordProcessId(dynamic word, IReadOnlySet<int> baselineProcessIds)
    {
        try
        {
            var handle = new IntPtr(Convert.ToInt64(word.Hwnd, CultureInfo.InvariantCulture));
            _ = GetWindowThreadProcessId(handle, out var processId);
            if (processId > 0) return checked((int)processId);
        }
        catch
        {
            // Word can keep Hwnd at zero until its first document window exists.
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            var newProcessIds = GetWordProcessIds()
                .Where(processId => !baselineProcessIds.Contains(processId))
                .ToArray();
            if (newProcessIds.Length == 1) return newProcessIds[0];
            if (newProcessIds.Length > 1) return 0;
            Thread.Sleep(100);
        }
        return 0;
    }

    private static int[] GetWordProcessIds() =>
        Process.GetProcessesByName("WINWORD")
            .Select(process =>
            {
                try { return process.Id; }
                finally { process.Dispose(); }
            })
            .Order()
            .ToArray();

    private static void CleanupOwnedWordProcess(int processId, IReadOnlyCollection<int> baselinePids, ControllerLog log)
    {
        if (processId <= 0 || baselinePids.Contains(processId)) return;
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited) return;
            if (!process.ProcessName.Equals("WINWORD", StringComparison.OrdinalIgnoreCase))
            {
                log.Write("warning", "cleanup_pid_mismatch",
                    "기록된 PID가 Word 프로세스가 아니어서 종료하지 않았습니다.",
                    new { processId, process.ProcessName });
                return;
            }

            if (process.WaitForExit(5_000)) return;
            log.Write("warning", "owned_word_force_cleanup",
                "PoC가 생성한 Word 프로세스가 남아 있어 강제 종료합니다.", new { processId });
            process.Kill(entireProcessTree: false);
            process.WaitForExit(5_000);
        }
        catch (ArgumentException)
        {
            // The owned process already exited.
        }
        catch (Exception exception)
        {
            log.WriteException("owned_word_cleanup_failed", exception);
        }
    }

    private static T? SafeGet<T>(Func<T> get)
    {
        try { return get(); }
        catch { return default; }
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
            "Hdiff", "WordComReadProbe", "Logs");
        return Path.Combine(directory,
            $"word-com-probe-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.jsonl");
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is null || !Marshal.IsComObject(value)) return;
        try { Marshal.FinalReleaseComObject(value); } catch { }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Word DRM 환경 COM 읽기 PoC (DRM 파일을 복사·변환하거나 저장하지 않습니다.)");
        Console.WriteLine();
        Console.WriteLine("사용법:");
        Console.WriteLine("  Hdiff.WordComReadProbe.exe \"C:\\문서\\시험.docx\"");
        Console.WriteLine("  Hdiff.WordComReadProbe.exe \"C:\\문서\\시험.docx\" --include-text");
        Console.WriteLine();
        Console.WriteLine("옵션:");
        Console.WriteLine("  --timeout-seconds N   COM 제한 시간(기본 120초)");
        Console.WriteLine("  --include-text        본문·표 행 미리보기를 로그에 포함(민감정보 주의)");
        Console.WriteLine("  --sample-limit N      내용 미리보기 최대 행 수(기본 30)");
        Console.WriteLine("  --log PATH            JSON Lines 로그 저장 경로");
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    private sealed record ControllerOptions(
        string FilePath,
        string? LogPath,
        int TimeoutSeconds,
        bool IncludeText,
        int SampleLimit)
    {
        public static ControllerOptions Parse(string[] args)
        {
            string? filePath = null;
            string? logPath = null;
            var timeout = 120;
            var includeText = false;
            var sampleLimit = 30;

            for (var index = 0; index < args.Length; index++)
            {
                var argument = args[index];
                switch (argument.ToLowerInvariant())
                {
                    case "--log":
                        logPath = RequireValue(args, ref index, argument);
                        break;
                    case "--timeout-seconds":
                        timeout = ParseInt(RequireValue(args, ref index, argument), argument, 5, 600);
                        break;
                    case "--sample-limit":
                        sampleLimit = ParseInt(RequireValue(args, ref index, argument), argument, 0, 500);
                        break;
                    case "--include-text":
                        includeText = true;
                        break;
                    default:
                        if (argument.StartsWith('-'))
                            throw new ArgumentException($"알 수 없는 옵션입니다: {argument}");
                        if (filePath is not null)
                            throw new ArgumentException("시험 파일은 하나만 지정할 수 있습니다.");
                        filePath = argument;
                        break;
                }
            }

            return new ControllerOptions(
                filePath ?? throw new ArgumentException("시험할 Word 파일 경로가 필요합니다."),
                logPath,
                timeout,
                includeText,
                sampleLimit);
        }
    }

    private sealed record WorkerOptions(
        string FilePath,
        bool IncludeText,
        int SampleLimit,
        HashSet<int> BaselineWordProcessIds)
    {
        public static WorkerOptions Parse(string[] args)
        {
            string? filePath = null;
            var includeText = false;
            var sampleLimit = 30;
            var baseline = new HashSet<int>();

            for (var index = 0; index < args.Length; index++)
            {
                var argument = args[index];
                switch (argument.ToLowerInvariant())
                {
                    case "--worker":
                        break;
                    case "--file":
                        filePath = RequireValue(args, ref index, argument);
                        break;
                    case "--sample-limit":
                        sampleLimit = ParseInt(RequireValue(args, ref index, argument), argument, 0, 500);
                        break;
                    case "--include-text":
                        includeText = true;
                        break;
                    case "--baseline-word-pids":
                        foreach (var value in RequireValue(args, ref index, argument)
                                     .Split(',', StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                                baseline.Add(pid);
                        }
                        break;
                }
            }

            return new WorkerOptions(
                filePath ?? throw new ArgumentException("워커 파일 경로가 없습니다."),
                includeText,
                sampleLimit,
                baseline);
        }
    }

    private enum CandidateKind
    {
        Paragraph,
        Table,
    }

    private sealed record DocumentCandidate(
        int Start,
        CandidateKind Kind,
        int CollectionIndex,
        string? Text);

    private sealed record TableReadResult(
        string ReadMode,
        int? RowCountReported,
        int? ColumnCountReported,
        List<string> Rows);

    private sealed class ControllerLog : IDisposable
    {
        private readonly object _gate = new();
        private readonly StreamWriter _writer;

        public ControllerLog(string path)
        {
            _writer = new StreamWriter(path, append: false, new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
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
                var timestamp = root.GetProperty("timestamp").GetDateTimeOffset().ToLocalTime()
                    .ToString("HH:mm:ss", CultureInfo.InvariantCulture);
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

    private static int ParseInt(
        string value,
        string option,
        int minimum,
        int maximum)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed < minimum
            || parsed > maximum)
        {
            throw new ArgumentOutOfRangeException(
                option,
                $"{option} 값은 {minimum:N0}~{maximum:N0}이어야 합니다.");
        }
        return parsed;
    }
}
