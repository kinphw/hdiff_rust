using System.Net;
using System.Runtime.InteropServices;
using System.Text;
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

    // Hancom's control ID for a memo, identical to the one the direct HWP5
    // parser matches. See Hwp5Reader.IsMemoControlId.
    private const string MemoControlId = "%%me";

    // Guards against a malformed document producing an endless walk.
    private const int ControlWalkLimit = 100_000;

    // Moves the caret to the position returned by the most recent GetText.
    // Hancom exposes this position specifically for enriching scan results
    // with control and table-cell context.
    private const int MoveScanPosition = 201;

    private static readonly Regex ControlCharacters = new(@"[\x00-\x08\x0B\x0C\x0E-\x1F]", RegexOptions.Compiled);

    // KeyIndicator returns Korean UI text such as "(B12): 문자 입력" while
    // the caret is inside a table cell. The address itself is locale-neutral.
    private static readonly Regex TableCellIndicator = new(
        @"\((?<column>[A-Z]+)(?<row>\d+)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // Paired with the warning text built in DescribeMemosLeftInBody.
    private static readonly Regex MemoWarningPattern = new(@"^이 문서에는 메모 (\d+)건", RegexOptions.Compiled);

    public ParsedDocument Read(string path, string directFailure, bool includeMemos = false)
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
            var warnings = new List<string> { $"직접 파서 실패 후 COM 텍스트 경로 사용: {directFailure}" };
            if (!includeMemos)
            {
                var memoWarning = DescribeMemosLeftInBody((object)hwp);
                if (memoWarning is not null) warnings.Add(memoWarning);
            }

            var text = Clean(raw);
            var blocks = text.Split('\n', StringSplitOptions.TrimEntries)
                .Select(line => new DocumentBlock(DocumentBlockKind.Paragraph, line))
                .ToArray();
            if (blocks.All(block => string.IsNullOrWhiteSpace(block.Text))) throw new DocumentReadException("한글 COM이 본문 텍스트를 반환하지 않았습니다.");
            return new ParsedDocument(path, blocks, "한글 COM 폴백", warnings);
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
                scanner.ReleaseScan,
                () => TryGetTableCellAtScanPosition(comObject));

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
                () => hwpScanner.ReleaseScan(),
                () => TryGetTableCellAtScanPosition(comObject));
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
        Action releaseScan,
        Func<TableCellLocation?> getTableCellAtScanPosition)
    {
        var builder = new StringBuilder();
        TableHierarchyCapture? table = null;
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
                if (state is 0 or 1)
                {
                    table?.AppendTo(builder);
                    table = null;
                    break;
                }

                if (state is 101 or 102)
                    throw new DocumentReadException($"한글 COM 본문 스캔이 상태 코드 {state}로 실패했습니다.");

                // A top-level table starts with state 4. Once inside it, probe
                // every chunk because Hancom can report the first cell of a
                // nested table with state 5 instead. The table control's
                // anchor, not the state number, identifies hierarchy changes.
                var location = table is not null || state == 4
                    ? getTableCellAtScanPosition()
                    : null;

                if (table is null)
                {
                    if (location is not null)
                    {
                        table = new TableHierarchyCapture();
                        table.AppendRaw(chunk);
                        table.AppendCell(location.Value, chunk);
                        continue;
                    }

                    // States 2/3 are normal text and paragraph transitions.
                    // For controls that are not tables, retaining their chunk
                    // matches the previous fallback behavior.
                    if (!string.IsNullOrEmpty(chunk)) builder.Append(chunk);
                    continue;
                }

                table.AppendRaw(chunk);
                if (location is not null)
                {
                    table.AppendCell(location.Value, chunk);
                    continue;
                }

                // A state-5 chunk with no table parent is the boundary after
                // the outermost table. Nested-table exits point back at their
                // parent table and therefore take the branch above.
                if (state == 5)
                {
                    if (HasMeaningfulTableText(chunk)) table.MarkUnreliable();
                    table.AppendTo(builder);
                    table = null;
                    continue;
                }

                // If Hancom cannot identify a meaningful chunk inside a table,
                // do not risk losing or misplacing it. The entire hierarchy is
                // emitted from the original scan stream when its boundary ends.
                if (HasMeaningfulTableText(chunk)) table.MarkUnreliable();
            }

            table?.AppendTo(builder);
        }
        finally
        {
            try { releaseScan(); } catch { }
        }
        return builder.ToString();
    }

    private static bool HasMeaningfulTableText(string value) =>
        !string.IsNullOrWhiteSpace(NormalizeTableCellText(value));

    private static TableCellLocation? TryGetTableCellAtScanPosition(object comObject)
    {
        dynamic hwp = comObject;
        object? parentControl = null;
        object? anchorPosition = null;
        try
        {
            // MovePos(201) is read-only: it moves the private automation
            // caret to the last GetText result without editing the document.
            hwp.MovePos(MoveScanPosition, 0, 0);
            parentControl = hwp.ParentCtrl;
            if (parentControl is null) return null;

            dynamic parent = parentControl;
            string controlId = parent.CtrlID ?? string.Empty;
            if (!controlId.StartsWith("tbl", StringComparison.Ordinal)) return null;

            // Cell addresses restart at A1 in every nested table. The table
            // control's anchor position is stable throughout that table and
            // distinguishes it from both its parent and sibling tables.
            anchorPosition = parent.GetAnchorPos(0);
            if (anchorPosition is null) return null;
            dynamic anchor = anchorPosition;
            var tableIdentity = new TableIdentity(
                Convert.ToInt32(anchor.Item("List")),
                Convert.ToInt32(anchor.Item("Para")),
                Convert.ToInt32(anchor.Item("Pos")));

            var sectionCount = 0;
            var sectionNumber = 0;
            var pageNumber = 0;
            var columnNumber = 0;
            var lineNumber = 0;
            var characterPosition = 0;
            short over = 0;
            string indicator = string.Empty;
            if (!(bool)hwp.KeyIndicator(
                    ref sectionCount,
                    ref sectionNumber,
                    ref pageNumber,
                    ref columnNumber,
                    ref lineNumber,
                    ref characterPosition,
                    ref over,
                    ref indicator))
                return null;

            var match = TableCellIndicator.Match(indicator);
            if (!match.Success || !int.TryParse(match.Groups["row"].Value, out var row)) return null;

            return new TableCellLocation(tableIdentity, match.Value, row);
        }
        catch
        {
            // Hancom versions differ in which automation members they expose.
            // A failed probe must never discard text; TableHierarchyCapture
            // falls back to the original raw stream for that whole hierarchy.
            return null;
        }
        finally
        {
            if (anchorPosition is not null && Marshal.IsComObject(anchorPosition))
            {
                try { Marshal.ReleaseComObject(anchorPosition); } catch { }
            }
            if (parentControl is not null && Marshal.IsComObject(parentControl))
            {
                try { Marshal.ReleaseComObject(parentControl); } catch { }
            }
        }
    }

    private static string NormalizeTableCellText(string value)
    {
        value = WebUtility.HtmlDecode(value).Replace('\r', '\n');
        value = ControlCharacters.Replace(value, string.Empty);
        return string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private readonly record struct TableIdentity(int List, int Paragraph, int Position);

    private readonly record struct TableCellLocation(TableIdentity Table, string Address, int Row);

    private sealed class TableHierarchyCapture
    {
        private readonly StringBuilder _raw = new();
        private readonly List<string> _lines = new();
        private readonly Dictionary<TableIdentity, TableRowCapture> _tables = new();
        private TableIdentity? _currentTable;
        private bool _reliable = true;

        public void AppendRaw(string value) => _raw.Append(value);

        public void MarkUnreliable() => _reliable = false;

        public void AppendCell(TableCellLocation location, string value)
        {
            if (_currentTable != location.Table)
            {
                FlushCurrentTable();
                _currentTable = location.Table;
            }

            if (!_tables.TryGetValue(location.Table, out var table))
            {
                table = new TableRowCapture(_lines.Add);
                _tables.Add(location.Table, table);
            }
            table.AppendCell(location.Address, location.Row, value);
        }

        public void AppendTo(StringBuilder destination)
        {
            FlushCurrentTable();
            if (!_reliable || _lines.Count == 0)
            {
                destination.Append(_raw);
                return;
            }

            if (destination.Length > 0 && destination[^1] != '\n') destination.Append('\n');
            foreach (var line in _lines)
                destination.Append(line).Append('\n');
        }

        private void FlushCurrentTable()
        {
            if (_currentTable is not { } identity) return;
            if (_tables.TryGetValue(identity, out var table)) table.FlushRow();
            _currentTable = null;
        }
    }

    private sealed class TableRowCapture(Action<string> appendLine)
    {
        private readonly List<StringBuilder> _cells = new();
        private int? _currentRow;
        private string? _currentAddress;

        public void AppendCell(string address, int row, string value)
        {
            var text = NormalizeTableCellText(value);
            if (_currentRow != row)
            {
                FlushRow();
                _currentRow = row;
            }

            if (!string.Equals(_currentAddress, address, StringComparison.OrdinalIgnoreCase))
            {
                _currentAddress = address;
                _cells.Add(new StringBuilder());
            }

            if (string.IsNullOrEmpty(text)) return;
            var cell = _cells[^1];
            if (cell.Length > 0) cell.Append(' ');
            cell.Append(text);
        }

        public void FlushRow()
        {
            if (_currentRow is null) return;
            appendLine(string.Join(" ", _cells.Select(cell => cell.ToString()).Where(text => text.Length > 0)));
            _cells.Clear();
            _currentRow = null;
            _currentAddress = null;
        }
    }

    /// <summary>
    /// Reports how many memos remain in the scanned text. Only two read-only
    /// calls are used — walking Ctrl.Next and reading CtrlID — because those
    /// are the calls measured to leave the document's modified flag clear.
    ///
    /// Excluding the memos was prototyped and rejected. DeleteCtrl works but
    /// sets IsModified, and a DLP/DRM agent can treat that edit as a
    /// prohibited operation — the same class of problem that pushed this
    /// reader off GetTextFile and document locks. Reading each memo's own
    /// list (SetPos into the memo list, then a scanSposList range scan) stays
    /// read-only but raised a COMException on every measured run, leaving the
    /// COM object unusable. So the memos stay in the body and the user is
    /// told, rather than risking a security popup on every comparison.
    /// </summary>
    private static string? DescribeMemosLeftInBody(object comObject)
    {
        dynamic hwp = comObject;
        int memoCount;
        try
        {
            memoCount = CountMemoControls(hwp);
        }
        catch (Exception exception)
        {
            return $"메모 포함 여부를 확인하지 못했습니다. 메모가 본문에 섞여 비교될 수 있습니다: {exception.Message}";
        }

        if (memoCount == 0) return null;
        return $"이 문서에는 메모 {memoCount}건이 있습니다. 한글 COM 경로에서는 원본을 건드리지 않고 " +
            "메모만 분리할 수 없어 '메모 제외' 설정을 적용하지 못했고, 메모 내용이 본문과 함께 비교됩니다. " +
            "메모를 빼고 비교하려면 DRM이 해제된 HWP/HWPX를 직접 파서로 여세요.";
    }

    /// <summary>
    /// Reads back the memo count from the warning above. The warning crosses
    /// a process boundary as JSON, so the UI can only recover the number by
    /// parsing it — keep this pattern next to the text it has to match.
    /// Returns 0 when the document carries no memo warning.
    /// </summary>
    public static int CountMemosReported(IEnumerable<string> warnings)
    {
        var total = 0;
        foreach (var warning in warnings)
        {
            var match = MemoWarningPattern.Match(warning);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var count)) total += count;
        }
        return total;
    }

    private static int CountMemoControls(dynamic hwp)
    {
        var count = 0;
        dynamic? control = hwp.HeadCtrl;
        for (var guard = 0; control is not null && guard < ControlWalkLimit; guard++)
        {
            string id = string.Empty;
            try { id = control.CtrlID ?? string.Empty; } catch { }
            if (id == MemoControlId) count++;
            control = control.Next;
        }
        return count;
    }
}
