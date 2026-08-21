using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Hdiff.Core.Documents;

namespace Hdiff.UI.Worker;

/// <summary>
/// Reads Excel workbooks through a private, read-only COM instance. This is the
/// supported path for DRM workbooks: no Save/SaveAs, temporary copy, format
/// conversion, active-instance attachment, or executable-name impersonation.
/// </summary>
internal sealed partial class ExcelComReader
{
    private const int MacroSecurityForceDisable = 3;
    private const int XlFormulas = -4123;
    private const int XlPart = 2;
    private const int XlByRows = 1;
    private const int XlByColumns = 2;
    private const int XlNext = 1;
    private const int XlPrevious = 2;
    private const long MaximumDenseCellsPerWorksheet = 5_000_000;
    private const int MaximumSparseCellsPerWorksheet = 250_000;
    private const int CellsPerChunk = 100_000;

    public static bool IsSupportedExtension(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".xlsx" or ".xls" or ".xlsm" or ".xlsb";

    public ParsedDocument Read(string path)
    {
        if (!File.Exists(path)) throw new DocumentReadException($"파일을 찾을 수 없습니다: {path}");
        if (!IsSupportedExtension(path)) throw new DocumentReadException("Excel COM 지원 형식은 .xlsx, .xls, .xlsm 및 .xlsb 입니다.");

        var excelType = Type.GetTypeFromProgID("Excel.Application")
            ?? throw new DocumentReadException("Excel COM 자동화 객체를 찾지 못했습니다. Microsoft Excel 설치 여부를 확인하세요.");
        var baselineExcelPids = GetExcelProcessIds();
        dynamic? excel = null;
        object? workbooksObject = null;
        object? workbookObject = null;
        var excelProcessId = 0;
        var ownsExcelProcess = false;

        try
        {
            excel = Activator.CreateInstance(excelType)
                ?? throw new DocumentReadException("Excel COM 객체를 생성하지 못했습니다.");
            excelProcessId = GetExcelProcessId(excel);
            ownsExcelProcess = excelProcessId > 0 && !baselineExcelPids.Contains(excelProcessId);
            if (!ownsExcelProcess)
                throw new DocumentReadException("새 Excel COM 인스턴스가 기존 Excel 프로세스와 분리되지 않아 사용자 문서 보호를 위해 중단했습니다.");

            ConfigureReadOnlyAutomation(excel);
            workbooksObject = excel.Workbooks;
            dynamic workbooks = workbooksObject;
            if (Convert.ToInt32(workbooks.Count, CultureInfo.InvariantCulture) != 0)
                throw new DocumentReadException("새 Excel COM 인스턴스에 이미 열린 통합문서가 있어 중단했습니다.");

            workbookObject = workbooks.Open(
                Path.GetFullPath(path), 0, true,
                Missing.Value, Missing.Value, Missing.Value, true,
                Missing.Value, Missing.Value, false, false,
                Missing.Value, false, true, 0);

            var blocks = ReadWorkbook(workbookObject);
            if (blocks.Count == 0)
                throw new DocumentReadException("Excel 통합문서에서 비교할 시트를 찾지 못했습니다.");

            return new ParsedDocument(path, blocks, "Excel COM 읽기 전용", Array.Empty<string>());
        }
        catch (DocumentReadException)
        {
            throw;
        }
        catch (COMException exception)
        {
            throw new DocumentReadException(
                $"Excel COM 읽기에 실패했습니다. HRESULT=0x{exception.HResult:X8}: {exception.Message}", exception);
        }
        catch (Exception exception)
        {
            throw new DocumentReadException($"Excel 통합문서를 읽지 못했습니다: {exception.Message}", exception);
        }
        finally
        {
            if (workbookObject is not null)
            {
                try
                {
                    dynamic workbook = workbookObject;
                    workbook.Close(false);
                }
                catch { }
            }

            if (excel is not null && ownsExcelProcess)
            {
                try { excel.Quit(); } catch { }
            }

            ReleaseComObject(workbookObject);
            ReleaseComObject(workbooksObject);
            ReleaseComObject(excel);
            CleanupOwnedExcelProcess(excelProcessId, baselineExcelPids);
        }
    }

    private static void ConfigureReadOnlyAutomation(dynamic excel)
    {
        try { excel.Visible = false; } catch { }
        try { excel.DisplayAlerts = false; } catch { }
        try { excel.EnableEvents = false; } catch { }
        try { excel.AskToUpdateLinks = false; } catch { }
        try { excel.ScreenUpdating = false; } catch { }
        try
        {
            // Do not open any workbook unless macros can be force-disabled.
            excel.AutomationSecurity = MacroSecurityForceDisable;
        }
        catch (Exception exception)
        {
            throw new DocumentReadException("Excel 매크로를 강제로 비활성화하지 못해 통합문서를 열지 않았습니다.", exception);
        }
    }

    private static List<DocumentBlock> ReadWorkbook(object workbookObject)
    {
        dynamic workbook = workbookObject;
        object? worksheetsObject = null;
        var blocks = new List<DocumentBlock>();
        try
        {
            worksheetsObject = workbook.Worksheets;
            dynamic worksheets = worksheetsObject;
            var worksheetCount = Convert.ToInt32(worksheets.Count, CultureInfo.InvariantCulture);
            for (var index = 1; index <= worksheetCount; index++)
            {
                object? worksheetObject = null;
                try
                {
                    worksheetObject = worksheets.Item[index];
                    dynamic worksheet = worksheetObject;
                    var sheetName = NormalizeHeader(Convert.ToString(worksheet.Name, CultureInfo.InvariantCulture) ?? $"Sheet{index}");
                    var visibility = Convert.ToInt32(worksheet.Visible, CultureInfo.InvariantCulture);
                    var visibilityLabel = visibility switch
                    {
                        0 => " [숨김]",
                        2 => " [매우 숨김]",
                        _ => string.Empty,
                    };
                    blocks.Add(new DocumentBlock(DocumentBlockKind.Paragraph, $"[시트] {sheetName}{visibilityLabel}", SectionPath: sheetName));

                    var bounds = FindContentBounds(worksheetObject);
                    if (bounds is null) continue;

                    var builder = new WorksheetTableBuilder(blocks, sheetName);
                    if (bounds.Value.CellCount <= MaximumDenseCellsPerWorksheet)
                        ReadDenseRows(worksheetObject, bounds.Value, builder);
                    else
                        ReadSparseRows(worksheetObject, builder, sheetName);
                    builder.Flush();
                }
                finally
                {
                    ReleaseComObject(worksheetObject);
                }
            }
            return blocks;
        }
        finally
        {
            ReleaseComObject(worksheetsObject);
        }
    }

    private static CellBounds? FindContentBounds(object worksheetObject)
    {
        dynamic worksheet = worksheetObject;
        object? cellsObject = null;
        object? firstRowCell = null;
        object? lastRowCell = null;
        object? firstColumnCell = null;
        object? lastColumnCell = null;
        try
        {
            cellsObject = worksheet.Cells;
            dynamic cells = cellsObject;
            firstRowCell = cells.Find("*", Missing.Value, XlFormulas, XlPart, XlByRows, XlNext, false, false, false);
            if (firstRowCell is null) return null;
            lastRowCell = cells.Find("*", Missing.Value, XlFormulas, XlPart, XlByRows, XlPrevious, false, false, false);
            firstColumnCell = cells.Find("*", Missing.Value, XlFormulas, XlPart, XlByColumns, XlNext, false, false, false);
            lastColumnCell = cells.Find("*", Missing.Value, XlFormulas, XlPart, XlByColumns, XlPrevious, false, false, false);
            if (lastRowCell is null || firstColumnCell is null || lastColumnCell is null) return null;

            dynamic firstRow = firstRowCell;
            dynamic lastRow = lastRowCell;
            dynamic firstColumn = firstColumnCell;
            dynamic lastColumn = lastColumnCell;
            return new CellBounds(
                Convert.ToInt32(firstRow.Row, CultureInfo.InvariantCulture),
                Convert.ToInt32(lastRow.Row, CultureInfo.InvariantCulture),
                Convert.ToInt32(firstColumn.Column, CultureInfo.InvariantCulture),
                Convert.ToInt32(lastColumn.Column, CultureInfo.InvariantCulture));
        }
        finally
        {
            ReleaseComObject(lastColumnCell);
            ReleaseComObject(firstColumnCell);
            ReleaseComObject(lastRowCell);
            ReleaseComObject(firstRowCell);
            ReleaseComObject(cellsObject);
        }
    }

    private static void ReadDenseRows(object worksheetObject, CellBounds bounds, WorksheetTableBuilder builder)
    {
        var rowsPerChunk = Math.Max(1, Math.Min(500, CellsPerChunk / Math.Max(1, bounds.ColumnCount)));
        for (var firstRow = bounds.FirstRow; firstRow <= bounds.LastRow; firstRow += rowsPerChunk)
        {
            var lastRow = Math.Min(bounds.LastRow, firstRow + rowsPerChunk - 1);
            object? rangeObject = null;
            try
            {
                rangeObject = CreateRange(worksheetObject, firstRow, bounds.FirstColumn, lastRow, bounds.LastColumn);
                dynamic range = rangeObject;
                object? values = range.Value;

                var chunkRowCount = lastRow - firstRow + 1;
                for (var rowOffset = 0; rowOffset < chunkRowCount; rowOffset++)
                {
                    var cells = new string[bounds.ColumnCount];
                    for (var columnOffset = 0; columnOffset < bounds.ColumnCount; columnOffset++)
                        cells[columnOffset] = NormalizeCell(ReadVariant(values, rowOffset, columnOffset));
                    builder.AddRow(firstRow + rowOffset, TrimRow(cells));
                }
            }
            finally
            {
                ReleaseComObject(rangeObject);
            }
        }
    }

    private static void ReadSparseRows(object worksheetObject, WorksheetTableBuilder builder, string sheetName)
    {
        dynamic worksheet = worksheetObject;
        object? cellsObject = null;
        object? currentCell = null;
        var rows = new SortedDictionary<int, SortedDictionary<int, string>>();
        try
        {
            cellsObject = worksheet.Cells;
            dynamic cells = cellsObject;
            currentCell = cells.Find("*", Missing.Value, XlFormulas, XlPart, XlByRows, XlNext, false, false, false);
            if (currentCell is null) return;
            dynamic first = currentCell;
            var firstRow = Convert.ToInt32((object)first.Row, CultureInfo.InvariantCulture);
            var firstColumn = Convert.ToInt32((object)first.Column, CultureInfo.InvariantCulture);

            for (var guard = 0; guard < MaximumSparseCellsPerWorksheet; guard++)
            {
                dynamic cell = currentCell;
                var row = Convert.ToInt32((object)cell.Row, CultureInfo.InvariantCulture);
                var column = Convert.ToInt32((object)cell.Column, CultureInfo.InvariantCulture);
                object? raw = cell.Value;
                var text = NormalizeCell(raw);
                if (!string.IsNullOrEmpty(text))
                {
                    if (!rows.TryGetValue(row, out var rowCells)) rows[row] = rowCells = new SortedDictionary<int, string>();
                    rowCells[column] = text;
                }

                object? nextCell = cells.FindNext(currentCell);
                if (nextCell is null) break;
                dynamic next = nextCell;
                var nextRow = Convert.ToInt32((object)next.Row, CultureInfo.InvariantCulture);
                var nextColumn = Convert.ToInt32((object)next.Column, CultureInfo.InvariantCulture);
                if (nextRow == firstRow && nextColumn == firstColumn)
                {
                    ReleaseComObject(nextCell);
                    currentCell = null;
                    break;
                }

                ReleaseComObject(currentCell);
                currentCell = nextCell;
                if (guard == MaximumSparseCellsPerWorksheet - 1)
                    throw new DocumentReadException($"Excel 시트 '{sheetName}'의 실제 셀이 {MaximumSparseCellsPerWorksheet:N0}개를 넘어 희소 범위 안전 한도를 초과했습니다.");
            }

            foreach (var row in rows)
            {
                var addressedCells = row.Value
                    .Select(cell => $"{ColumnName(cell.Key)}: {cell.Value}")
                    .ToArray();
                builder.AddRow(row.Key, addressedCells);
            }
        }
        finally
        {
            ReleaseComObject(currentCell);
            ReleaseComObject(cellsObject);
        }
    }

    private static object CreateRange(object worksheetObject, int firstRow, int firstColumn, int lastRow, int lastColumn)
    {
        dynamic worksheet = worksheetObject;
        object? cellsObject = null;
        object? startCell = null;
        object? endCell = null;
        try
        {
            cellsObject = worksheet.Cells;
            dynamic cells = cellsObject;
            startCell = cells.Item[firstRow, firstColumn];
            endCell = cells.Item[lastRow, lastColumn];
            return worksheet.Range[startCell, endCell];
        }
        finally
        {
            ReleaseComObject(endCell);
            ReleaseComObject(startCell);
            ReleaseComObject(cellsObject);
        }
    }

    private static object? ReadVariant(object? value, int row, int column)
    {
        if (value is not Array array) return row == 0 && column == 0 ? value : null;
        if (array.Rank != 2) return null;
        return array.GetValue(array.GetLowerBound(0) + row, array.GetLowerBound(1) + column);
    }

    private static IReadOnlyList<string> TrimRow(string[] cells)
    {
        var first = Array.FindIndex(cells, text => !string.IsNullOrEmpty(text));
        if (first < 0) return Array.Empty<string>();
        var last = Array.FindLastIndex(cells, text => !string.IsNullOrEmpty(text));
        return cells[first..(last + 1)];
    }

    private static string NormalizeCell(object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            DateTime dateTime => dateTime.TimeOfDay == TimeSpan.Zero
                ? dateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            double number => number.ToString("G15", CultureInfo.InvariantCulture),
            float number => number.ToString("G9", CultureInfo.InvariantCulture),
            decimal number => number.ToString(CultureInfo.InvariantCulture),
            bool boolean => boolean ? "TRUE" : "FALSE",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty,
        };
        if (text.Length == 0) return string.Empty;

        text = ControlCharacters().Replace(text, string.Empty).Trim();
        return text.Replace("\r\n", " ↵ ", StringComparison.Ordinal)
            .Replace("\r", " ↵ ", StringComparison.Ordinal)
            .Replace("\n", " ↵ ", StringComparison.Ordinal);
    }

    private static string NormalizeHeader(string text) =>
        text.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();

    private static string ColumnName(int column)
    {
        var name = new StringBuilder();
        for (var value = column; value > 0; value = (value - 1) / 26)
            name.Insert(0, (char)('A' + ((value - 1) % 26)));
        return name.ToString();
    }

    private static int[] GetExcelProcessIds()
    {
        var processes = Process.GetProcessesByName("EXCEL");
        try { return processes.Select(process => process.Id).ToArray(); }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private static int GetExcelProcessId(dynamic excel)
    {
        try
        {
            var windowHandle = new IntPtr(Convert.ToInt64(excel.Hwnd, CultureInfo.InvariantCulture));
            _ = GetWindowThreadProcessId(windowHandle, out var processId);
            return checked((int)processId);
        }
        catch
        {
            return 0;
        }
    }

    private static void CleanupOwnedExcelProcess(int processId, IReadOnlyCollection<int> baselineExcelPids)
    {
        if (processId <= 0 || baselineExcelPids.Contains(processId)) return;
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited || process.WaitForExit(3_000)) return;
            if (process.ProcessName.Equals("EXCEL", StringComparison.OrdinalIgnoreCase))
            {
                process.Kill(entireProcessTree: false);
                process.WaitForExit(3_000);
            }
        }
        catch (ArgumentException)
        {
            // The isolated Excel process already exited.
        }
        catch
        {
            // The worker process is about to exit; never touch any baseline PID.
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is null || !Marshal.IsComObject(value)) return;
        try { Marshal.FinalReleaseComObject(value); } catch { }
    }

    [GeneratedRegex(@"[\x00-\x08\x0B\x0C\x0E-\x1F]")]
    private static partial Regex ControlCharacters();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    private readonly record struct CellBounds(int FirstRow, int LastRow, int FirstColumn, int LastColumn)
    {
        public int RowCount => LastRow - FirstRow + 1;
        public int ColumnCount => LastColumn - FirstColumn + 1;
        public long CellCount => checked((long)RowCount * ColumnCount);
    }

    private sealed class WorksheetTableBuilder(List<DocumentBlock> blocks, string sheetName)
    {
        private readonly List<IReadOnlyList<string>> _rows = new();
        private int? _lastContentRow;

        public void AddRow(int sourceRow, IReadOnlyList<string> cells)
        {
            if (cells.Count == 0)
            {
                Flush();
                _lastContentRow = null;
                return;
            }

            if (_lastContentRow is not null && sourceRow > _lastContentRow.Value + 1) Flush();
            _rows.Add(cells);
            _lastContentRow = sourceRow;
        }

        public void Flush()
        {
            if (_rows.Count == 0) return;
            // A repeated, non-numbered boundary prevents rows from separate
            // tables from being aligned across a fully blank source row.
            blocks.Add(new DocumentBlock(DocumentBlockKind.Table, "[표 영역]", _rows.ToArray(), sheetName));
            _rows.Clear();
        }
    }
}
