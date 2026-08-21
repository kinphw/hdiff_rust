using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Hdiff.Core.Documents;

namespace Hdiff.UI.Worker;

/// <summary>
/// Reads Word documents through a private, read-only COM instance. It never
/// saves, converts, copies, or attaches to an existing user-owned Word process.
/// </summary>
internal sealed partial class WordComReader
{
    private const int MacroSecurityForceDisable = 3;
    private const int AlertsNone = 0;
    private const int DoNotSaveChanges = 0;
    private const int WithInTable = 12;

    public static bool IsSupportedExtension(string path) =>
        Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase);

    public ParsedDocument Read(string path)
    {
        if (!File.Exists(path)) throw new DocumentReadException($"파일을 찾을 수 없습니다: {path}");
        if (!IsSupportedExtension(path)) throw new DocumentReadException("Word COM 지원 형식은 .docx입니다.");

        var wordType = Type.GetTypeFromProgID("Word.Application")
            ?? throw new DocumentReadException("Word COM 자동화 객체를 찾지 못했습니다. Microsoft Word 설치 여부를 확인하세요.");
        var baselineWordPids = GetWordProcessIds();
        dynamic? word = null;
        object? optionsObject = null;
        object? documentsObject = null;
        object? documentObject = null;
        var wordProcessId = 0;
        var ownsWordProcess = false;

        try
        {
            word = Activator.CreateInstance(wordType)
                ?? throw new DocumentReadException("Word COM 객체를 생성하지 못했습니다.");
            wordProcessId = GetWordProcessId(word, baselineWordPids);
            ownsWordProcess = wordProcessId > 0 && !baselineWordPids.Contains(wordProcessId);
            if (!ownsWordProcess)
                throw new DocumentReadException("새 Word COM 인스턴스가 기존 Word 프로세스와 분리되지 않아 사용자 문서 보호를 위해 중단했습니다.");

            ConfigureReadOnlyAutomation(word, out optionsObject);
            documentsObject = word.Documents;
            dynamic documents = documentsObject;
            if (Convert.ToInt32(documents.Count, CultureInfo.InvariantCulture) != 0)
                throw new DocumentReadException("새 Word 인스턴스에 이미 열린 문서가 있어 중단했습니다.");

            documentObject = documents.Open(
                FileName: Path.GetFullPath(path),
                ConfirmConversions: false,
                ReadOnly: true,
                AddToRecentFiles: false,
                Revert: false,
                Visible: false,
                OpenAndRepair: false,
                NoEncodingDialog: true);
            dynamic document = documentObject;
            if (Convert.ToBoolean(document.ReadOnly, CultureInfo.InvariantCulture) != true)
                throw new DocumentReadException("Word가 문서를 읽기 전용으로 열지 않아 내용 읽기를 중단했습니다.");

            var blocks = ReadDocument(documentObject);
            if (blocks.Count == 0)
                throw new DocumentReadException("Word 문서에서 비교할 본문이나 표를 찾지 못했습니다.");
            return new ParsedDocument(path, blocks, "Word COM 읽기 전용", Array.Empty<string>());
        }
        catch (DocumentReadException)
        {
            throw;
        }
        catch (COMException exception)
        {
            throw new DocumentReadException(
                $"Word COM 읽기에 실패했습니다. HRESULT=0x{exception.HResult:X8}: {exception.Message}", exception);
        }
        catch (Exception exception)
        {
            throw new DocumentReadException($"Word 문서를 읽지 못했습니다: {exception.Message}", exception);
        }
        finally
        {
            if (documentObject is not null)
            {
                try
                {
                    dynamic document = documentObject;
                    document.Close(DoNotSaveChanges);
                }
                catch { }
            }

            if (word is not null && ownsWordProcess)
            {
                try { word.Quit(DoNotSaveChanges); } catch { }
            }

            ReleaseComObject(documentObject);
            ReleaseComObject(documentsObject);
            ReleaseComObject(optionsObject);
            ReleaseComObject(word);
            CleanupOwnedWordProcess(wordProcessId, baselineWordPids);
        }
    }

    private static void ConfigureReadOnlyAutomation(dynamic word, out object? optionsObject)
    {
        optionsObject = null;
        try { word.Visible = false; } catch { }
        try { word.DisplayAlerts = AlertsNone; } catch { }
        try { word.ScreenUpdating = false; } catch { }
        try
        {
            word.AutomationSecurity = MacroSecurityForceDisable;
        }
        catch (Exception exception)
        {
            throw new DocumentReadException("Word 매크로를 강제로 비활성화하지 못해 문서를 열지 않았습니다.", exception);
        }

        try
        {
            optionsObject = word.Options;
            dynamic options = optionsObject;
            options.UpdateLinksAtOpen = false;
        }
        catch (Exception exception)
        {
            throw new DocumentReadException("Word 외부 링크 갱신을 비활성화하지 못해 문서를 열지 않았습니다.", exception);
        }
    }

    private static List<DocumentBlock> ReadDocument(object documentObject)
    {
        dynamic document = documentObject;
        object? paragraphsObject = null;
        object? tablesObject = null;
        var candidates = new List<DocumentCandidate>();
        var blocks = new List<DocumentBlock>();

        try
        {
            paragraphsObject = document.Paragraphs;
            dynamic paragraphs = paragraphsObject;
            var paragraphCount = Convert.ToInt32(paragraphs.Count, CultureInfo.InvariantCulture);
            for (var index = 1; index <= paragraphCount; index++)
            {
                object? paragraphObject = null;
                object? rangeObject = null;
                try
                {
                    paragraphObject = paragraphs.Item(index);
                    dynamic paragraph = paragraphObject;
                    rangeObject = paragraph.Range;
                    dynamic range = rangeObject;
                    var inTable = Convert.ToBoolean(range.Information[WithInTable], CultureInfo.InvariantCulture);
                    if (inTable) continue;

                    var start = Convert.ToInt32(range.Start, CultureInfo.InvariantCulture);
                    var text = NormalizeParagraph(Convert.ToString(range.Text, CultureInfo.InvariantCulture));
                    candidates.Add(new DocumentCandidate(start, DocumentCandidateKind.Paragraph, index, text));
                }
                finally
                {
                    ReleaseComObject(rangeObject);
                    ReleaseComObject(paragraphObject);
                }
            }

            tablesObject = document.Tables;
            dynamic tables = tablesObject;
            var tableCount = Convert.ToInt32(tables.Count, CultureInfo.InvariantCulture);
            for (var index = 1; index <= tableCount; index++)
            {
                object? tableObject = null;
                object? rangeObject = null;
                try
                {
                    tableObject = tables.Item(index);
                    dynamic table = tableObject;
                    var nestingLevel = TryReadInt(() => table.NestingLevel) ?? 1;
                    if (nestingLevel != 1) continue;
                    rangeObject = table.Range;
                    dynamic range = rangeObject;
                    var start = Convert.ToInt32(range.Start, CultureInfo.InvariantCulture);
                    candidates.Add(new DocumentCandidate(start, DocumentCandidateKind.Table, index, null));
                }
                finally
                {
                    ReleaseComObject(rangeObject);
                    ReleaseComObject(tableObject);
                }
            }

            foreach (var candidate in candidates.OrderBy(candidate => candidate.Start))
            {
                if (candidate.Kind == DocumentCandidateKind.Paragraph)
                {
                    blocks.Add(new DocumentBlock(DocumentBlockKind.Paragraph, candidate.Text ?? string.Empty));
                    continue;
                }

                object? tableObject = null;
                try
                {
                    tableObject = tables.Item(candidate.CollectionIndex);
                    var rows = ReadTable(tableObject);
                    if (rows.Count > 0)
                        blocks.Add(new DocumentBlock(DocumentBlockKind.Table, string.Empty, rows));
                }
                finally
                {
                    ReleaseComObject(tableObject);
                }
            }
            return blocks;
        }
        finally
        {
            ReleaseComObject(tablesObject);
            ReleaseComObject(paragraphsObject);
        }
    }

    private static IReadOnlyList<IReadOnlyList<string>> ReadTable(object tableObject)
    {
        try
        {
            return ReadTableByRows(tableObject);
        }
        catch
        {
            return ReadTableByCellCoordinates(tableObject);
        }
    }

    private static IReadOnlyList<IReadOnlyList<string>> ReadTableByRows(object tableObject)
    {
        dynamic table = tableObject;
        object? rowsObject = null;
        var result = new List<IReadOnlyList<string>>();
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
                    result.Add(values);
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

    private static IReadOnlyList<IReadOnlyList<string>> ReadTableByCellCoordinates(object tableObject)
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
                    var rowIndex = Convert.ToInt32((object)cell.RowIndex, CultureInfo.InvariantCulture);
                    var columnIndex = Convert.ToInt32((object)cell.ColumnIndex, CultureInfo.InvariantCulture);
                    rangeObject = cell.Range;
                    dynamic range = rangeObject;
                    var text = NormalizeCell(Convert.ToString(range.Text, CultureInfo.InvariantCulture));
                    if (!rows.TryGetValue(rowIndex, out var columns))
                        rows[rowIndex] = columns = new SortedDictionary<int, string>();
                    columns[columnIndex] = text;
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

        var result = new List<IReadOnlyList<string>>(rows.Count);
        foreach (var columns in rows.Values)
        {
            var values = new string[columns.Keys.DefaultIfEmpty(0).Max()];
            foreach (var (column, text) in columns) values[column - 1] = text;
            result.Add(values);
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
            return NormalizeCell(Convert.ToString(range.Text, CultureInfo.InvariantCulture));
        }
        finally
        {
            ReleaseComObject(rangeObject);
            ReleaseComObject(cellObject);
        }
    }

    private static string NormalizeParagraph(string? value)
    {
        var text = (value ?? string.Empty).TrimEnd('\r', '\a');
        return text.Replace("\v", " ↵ ", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal);
    }

    private static string NormalizeCell(string? value)
    {
        var text = (value ?? string.Empty)
            .Replace('\a', ' ')
            .Replace('\r', ' ')
            .Replace('\v', ' ')
            .Replace('\f', ' ');
        return Whitespace().Replace(text, " ").Trim();
    }

    private static int? TryReadInt(Func<object> get)
    {
        try { return Convert.ToInt32(get(), CultureInfo.InvariantCulture); }
        catch { return null; }
    }

    private static int[] GetWordProcessIds()
    {
        var processes = Process.GetProcessesByName("WINWORD");
        try { return processes.Select(process => process.Id).ToArray(); }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private static int GetWordProcessId(dynamic word, IReadOnlyCollection<int> baselineWordPids)
    {
        try
        {
            var windowHandle = new IntPtr(Convert.ToInt64(word.Hwnd, CultureInfo.InvariantCulture));
            _ = GetWindowThreadProcessId(windowHandle, out var processId);
            if (processId > 0) return checked((int)processId);
        }
        catch
        {
            // Word can defer its window handle until a document window exists.
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            var created = GetWordProcessIds()
                .Where(processId => !baselineWordPids.Contains(processId))
                .ToArray();
            if (created.Length == 1) return created[0];
            if (created.Length > 1) return 0;
            Thread.Sleep(100);
        }
        return 0;
    }

    private static void CleanupOwnedWordProcess(int processId, IReadOnlyCollection<int> baselineWordPids)
    {
        if (processId <= 0 || baselineWordPids.Contains(processId)) return;
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited || process.WaitForExit(3_000)) return;
            if (process.ProcessName.Equals("WINWORD", StringComparison.OrdinalIgnoreCase))
            {
                process.Kill(entireProcessTree: false);
                process.WaitForExit(3_000);
            }
        }
        catch (ArgumentException)
        {
            // The isolated Word process already exited.
        }
        catch
        {
            // The worker is exiting; never touch a baseline PID.
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is null || !Marshal.IsComObject(value)) return;
        try { Marshal.FinalReleaseComObject(value); } catch { }
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    private enum DocumentCandidateKind
    {
        Paragraph,
        Table,
    }

    private sealed record DocumentCandidate(
        int Start,
        DocumentCandidateKind Kind,
        int CollectionIndex,
        string? Text);
}
