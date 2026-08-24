using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Hdiff.Core.Diff;
using Hdiff.Core.Review;

namespace Hdiff.UI;

/// <summary>
/// Renders each paired visual row once on a shared canvas.  The old implementation
/// synchronized two independent RichTextBox scroll positions; that can drift by a
/// few pixels.  This renderer has one coordinate system and one scrollbar instead.
/// </summary>
internal sealed class SideBySideDiffView : UserControl
{
    private const int SplitterWidth = 5;
    private const int OverviewWidth = 42;
    private const int LineNumberWidth = 48;

    private static readonly IReadOnlyList<InlineDiffFragment> EmptyFragments = Array.Empty<InlineDiffFragment>();
    private readonly Label _oldHeader = CreateHeader("변경 전");
    private readonly Label _newHeader = CreateHeader("변경 후");
    private readonly Panel _headerDivider = new() { Dock = DockStyle.Fill };
    private readonly DiffOverviewMap _oldOverview = new(oldSide: true) { Dock = DockStyle.None };
    private readonly DiffOverviewMap _newOverview = new(oldSide: false) { Dock = DockStyle.None };
    // The two overview maps are the visible synchronized scroll controls. Keep
    // a hidden ScrollBar only as the range/value model used by existing logic.
    private readonly VScrollBar _verticalScroll = new() { Visible = false, TabStop = false, Size = Size.Empty };
    private readonly HScrollBar _horizontalScroll = new() { Dock = DockStyle.Bottom, Visible = false };
    private readonly DiffCanvas _canvas = new() { Dock = DockStyle.Fill };
    private readonly Panel _body = new() { Dock = DockStyle.Fill, BackColor = Color.White };
    private readonly List<VisualDiffRow> _visualRows = new();
    private readonly Dictionary<int, int> _diffRowToFirstVisualLine = new();
    private readonly System.Windows.Forms.Timer _highlightTimer = new() { Interval = 1400 };
    private DocumentDiff? _lastDiff;
    private bool _wrapLongLines = true;
    private bool _reflowQueued;
    private int _verticalMaximumRow;
    private int _visibleVisualRowCount = 1;
    private int _oldTextWidth;
    private int _newTextWidth;
    private HdiffThemePalette _theme = HdiffThemes.Light;

    public SideBySideDiffView()
    {
        BackColor = Color.FromArgb(220, 224, 230);

        var headers = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 32,
            ColumnCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        headers.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        headers.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, SplitterWidth));
        headers.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        headers.Controls.Add(_oldHeader, 0, 0);
        headers.Controls.Add(_headerDivider, 1, 0);
        headers.Controls.Add(_newHeader, 2, 0);

        _canvas.MemoAddRequested += (_, target) => MemoAddRequested?.Invoke(this, target);
        _canvas.MemoOpenRequested += (_, target) => MemoOpenRequested?.Invoke(this, target);
        _highlightTimer.Tick += (_, _) =>
        {
            _highlightTimer.Stop();
            _canvas.HighlightedDiffRow = null;
        };

        _oldOverview.NavigateToLineRequested += (_, line) => ScrollToDiffRow(line);
        _newOverview.NavigateToLineRequested += (_, line) => ScrollToDiffRow(line);
        _oldOverview.MouseWheel += CanvasMouseWheel;
        _newOverview.MouseWheel += CanvasMouseWheel;
        _body.MouseWheel += CanvasMouseWheel;
        _body.Controls.Add(_canvas);
        _body.Controls.Add(_oldOverview);
        _body.Controls.Add(_newOverview);

        _verticalScroll.ValueChanged += (_, _) => UpdateCanvasScroll();
        _horizontalScroll.ValueChanged += (_, _) => UpdateCanvasScroll();
        _canvas.MouseWheel += CanvasMouseWheel;
        _body.Resize += (_, _) =>
        {
            // Column geometry is also needed before a diff exists so the
            // center divider remains visible on the initial empty screen.
            LayoutBody();
            QueueReflow();
            MemoGeometryChanged?.Invoke(this, EventArgs.Empty);
        };

        Controls.Add(_body);
        Controls.Add(_horizontalScroll);
        Controls.Add(_verticalScroll);
        Controls.Add(headers);
        ApplyTheme(_theme);
    }

    /// <summary>A reviewer asked for a new memo on this comparison cell.</summary>
    public event EventHandler<DiffMemoTarget>? MemoAddRequested;

    /// <summary>A reviewer clicked the memo flag of this comparison cell.</summary>
    public event EventHandler<DiffMemoTarget>? MemoOpenRequested;

    /// <summary>The selected memo anchor moved because the view scrolled or reflowed.</summary>
    public event EventHandler? MemoGeometryChanged;

    /// <summary>Global memo numbers per comparison row, separated by column.</summary>
    public void SetMemoNumbers(
        IReadOnlyDictionary<int, (IReadOnlyList<int> Old, IReadOnlyList<int> New)> numbersByDiffRow) =>
        _canvas.SetMemoNumbers(numbersByDiffRow);

    /// <summary>
    /// Marks the row a memo belongs to for as long as that memo stays selected,
    /// which is what ties a memo in the list to its paragraph on screen.
    /// </summary>
    public int? PinnedDiffRow
    {
        get => _canvas.PinnedDiffRow;
        set => _canvas.PinnedDiffRow = value;
    }
    public DiffMemoTarget? PinnedMemoTarget
    {
        get => _canvas.PinnedMemoTarget;
        set
        {
            _canvas.PinnedMemoTarget = value;
            MemoGeometryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool TryGetPinnedMemoAnchorScreen(out Point anchor)
    {
        if (_canvas.TryGetPinnedMemoFlagBounds(out var bounds))
        {
            anchor = _canvas.PointToScreen(new Point(bounds.Right, bounds.Top + (bounds.Height / 2)));
            return true;
        }

        anchor = default;
        return false;
    }

    /// <summary>Brings a comparison row into view and marks it briefly, for memo navigation.</summary>
    public void RevealDiffRow(int diffRow)
    {
        if (!_diffRowToFirstVisualLine.ContainsKey(diffRow)) return;
        ScrollToDiffRow(diffRow);
        _canvas.HighlightedDiffRow = diffRow;
        _highlightTimer.Stop();
        _highlightTimer.Start();
    }

    /// <summary>Like VS Code's word wrap, but wrapping is calculated once for both sides.</summary>
    public bool WrapLongLines
    {
        get => _wrapLongLines;
        set
        {
            if (_wrapLongLines == value) return;
            _wrapLongLines = value;
            _horizontalScroll.Visible = !value;
            QueueReflow();
        }
    }

    /// <summary>Draw a subtle horizontal boundary after every paired visual row.</summary>
    public bool ShowRowSeparators
    {
        get => _canvas.ShowRowSeparators;
        set => _canvas.ShowRowSeparators = value;
    }

    /// <summary>Allow mouse selection and clipboard copy without changing the shared-canvas layout.</summary>
    public bool TextSelectionEnabled
    {
        get => _canvas.TextSelectionEnabled;
        set => _canvas.TextSelectionEnabled = value;
    }

    /// <summary>
    /// Text size in typographic points. At 96 DPI, 9/10.5/12pt correspond to
    /// the 12/14/16px choices shown in the toolbar.
    /// </summary>
    public float DocumentFontSizePoints
    {
        get => _canvas.DocumentFontSizePoints;
        set
        {
            if (Math.Abs(_canvas.DocumentFontSizePoints - value) < 0.01f) return;
            _canvas.SetDocumentFontSize(value);
            QueueReflow();
        }
    }

    public void ApplyTheme(HdiffThemePalette theme)
    {
        _theme = theme;
        BackColor = theme.AppBack;
        _body.BackColor = theme.CanvasBack;
        _oldHeader.BackColor = theme.HeaderBack;
        _newHeader.BackColor = theme.HeaderBack;
        _oldHeader.ForeColor = theme.Text;
        _newHeader.ForeColor = theme.Text;
        _headerDivider.BackColor = theme.AppBack;
        _verticalScroll.BackColor = theme.SurfaceBack;
        _horizontalScroll.BackColor = theme.SurfaceBack;
        _canvas.ApplyTheme(theme);
        _oldOverview.ApplyTheme(theme);
        _newOverview.ApplyTheme(theme);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _highlightTimer.Dispose();
        base.Dispose(disposing);
    }

    public void Clear()
    {
        _lastDiff = null;
        _highlightTimer.Stop();
        _canvas.HighlightedDiffRow = null;
        _canvas.PinnedDiffRow = null;
        _canvas.SetMemoNumbers(
            new Dictionary<int, (IReadOnlyList<int> Old, IReadOnlyList<int> New)>());
        _oldHeader.Text = "변경 전";
        _newHeader.Text = "변경 후";
        _visualRows.Clear();
        _diffRowToFirstVisualLine.Clear();
        _oldOverview.SetLines(Array.Empty<DiffOverviewLine>());
        _newOverview.SetLines(Array.Empty<DiffOverviewLine>());
        ConfigureScrollBars();
        _canvas.SetRows(_visualRows);
    }

    public void SetDiff(DocumentDiff diff)
    {
        _lastDiff = diff;
        Render(diff, resetScroll: true);
    }

    private void QueueReflow()
    {
        if (_lastDiff is null || _reflowQueued || !IsHandleCreated) return;
        _reflowQueued = true;
        BeginInvoke((MethodInvoker)delegate
        {
            _reflowQueued = false;
            if (!IsDisposed && _lastDiff is not null) Render(_lastDiff, resetScroll: false);
        });
    }

    private void Render(DocumentDiff diff, bool resetScroll)
    {
        var preservedDiffRow = resetScroll ? 0 : GetCurrentDiffRow();
        LayoutBody();
        _visualRows.Clear();
        _diffRowToFirstVisualLine.Clear();
        _oldTextWidth = 0;
        _newTextWidth = 0;

        _oldHeader.Text = $"변경 전  ·  {Path.GetFileName(diff.OldDocument.SourcePath)}";
        _newHeader.Text = $"변경 후  ·  {Path.GetFileName(diff.NewDocument.SourcePath)}";

        // The splitter can leave one side a pixel wider when the viewport width is odd.
        // Using the narrower text width for both sides makes equal text wrap identically.
        var commonAvailableWidth = Math.Max(24,
            Math.Min(_canvas.OldContentBounds.Width, _canvas.NewContentBounds.Width) - LineNumberWidth - 8);
        for (var rowIndex = 0; rowIndex < diff.Rows.Count; rowIndex++)
        {
            var row = diff.Rows[rowIndex];
            var oldLines = SplitFragments(row.OldFragments, commonAvailableWidth);
            var newLines = SplitFragments(row.NewFragments, commonAvailableWidth);
            var visualLineCount = Math.Max(1, Math.Max(oldLines.Count, newLines.Count));
            _diffRowToFirstVisualLine[rowIndex] = _visualRows.Count;

            for (var lineIndex = 0; lineIndex < visualLineCount; lineIndex++)
            {
                var oldFragments = lineIndex < oldLines.Count ? oldLines[lineIndex] : EmptyFragments;
                var newFragments = lineIndex < newLines.Count ? newLines[lineIndex] : EmptyFragments;
                _visualRows.Add(new VisualDiffRow(
                    rowIndex,
                    lineIndex == 0 ? row.OldLine : null,
                    lineIndex == 0 ? row.NewLine : null,
                    oldFragments,
                    newFragments,
                    row.Kind,
                    row.Presentation,
                    row.OldText is null,
                    row.NewText is null));
                _oldTextWidth = Math.Max(_oldTextWidth, MeasureFragments(oldFragments));
                _newTextWidth = Math.Max(_newTextWidth, MeasureFragments(newFragments));
            }
        }

        _oldOverview.SetLines(diff.Rows.Select(row => new DiffOverviewLine(row.Kind, row.OldText?.Length ?? 0, row.OldText is null)).ToArray());
        _newOverview.SetLines(diff.Rows.Select(row => new DiffOverviewLine(row.Kind, row.NewText?.Length ?? 0, row.NewText is null)).ToArray());
        _canvas.SetRows(_visualRows);
        ConfigureScrollBars();
        if (resetScroll) SetVerticalScrollValue(0);
        else ScrollToDiffRow(preservedDiffRow);
    }

    private void LayoutBody()
    {
        var width = Math.Max(0, _body.ClientSize.Width);
        var sideWidth = Math.Max(0, (width - SplitterWidth) / 2);
        var oldContentWidth = Math.Max(0, sideWidth - OverviewWidth);
        var newContentWidth = Math.Max(0, width - sideWidth - SplitterWidth - OverviewWidth);
        _canvas.ConfigureColumns(new Rectangle(0, 0, oldContentWidth, _body.ClientSize.Height),
            new Rectangle(sideWidth + SplitterWidth, 0, newContentWidth, _body.ClientSize.Height));
        _oldOverview.SetBounds(oldContentWidth, 0, OverviewWidth, _body.ClientSize.Height);
        _newOverview.SetBounds(width - OverviewWidth, 0, OverviewWidth, _body.ClientSize.Height);
        _oldOverview.BringToFront();
        _newOverview.BringToFront();
    }

    private IReadOnlyList<IReadOnlyList<InlineDiffFragment>> SplitFragments(IReadOnlyList<InlineDiffFragment> fragments, int availableWidth)
    {
        if (fragments.Count == 0) return Array.Empty<IReadOnlyList<InlineDiffFragment>>();
        if (!_wrapLongLines) return new[] { fragments };

        var lines = new List<IReadOnlyList<InlineDiffFragment>>();
        var current = new List<InlineDiffFragment>();
        var width = 0;
        foreach (var fragment in fragments)
        {
            foreach (var character in ToDisplayText(fragment.Text))
            {
                var characterWidth = MeasureCharacter(character);
                if (width > 0 && width + characterWidth > availableWidth)
                {
                    lines.Add(current);
                    current = new List<InlineDiffFragment>();
                    width = 0;
                }
                AppendFragmentCharacter(current, fragment.Kind, character);
                width += characterWidth;
            }
        }
        if (current.Count > 0) lines.Add(current);
        return lines;
    }

    private void ConfigureScrollBars()
    {
        // Use visual-row units rather than pixels. WinForms ScrollBar's
        // Maximum/LargeChange convention is easy to get wrong in pixels and
        // could leave the final report rows outside the reachable range.
        _visibleVisualRowCount = Math.Max(1, _canvas.ClientSize.Height / _canvas.RowHeight);
        _verticalMaximumRow = Math.Max(0, _visualRows.Count - _visibleVisualRowCount);
        ConfigureScrollBar(_verticalScroll, _verticalMaximumRow, _visibleVisualRowCount, 1);

        _horizontalScroll.Visible = !_wrapLongLines;
        if (_wrapLongLines)
        {
            _horizontalScroll.Value = 0;
        }
        else
        {
            var visibleTextWidth = Math.Max(1, Math.Min(_canvas.OldContentBounds.Width, _canvas.NewContentBounds.Width) - LineNumberWidth - 8);
            ConfigureScrollBar(_horizontalScroll, Math.Max(_oldTextWidth, _newTextWidth) - visibleTextWidth, visibleTextWidth, 24);
        }
        UpdateCanvasScroll();
    }

    private static void ConfigureScrollBar(ScrollBar scrollBar, int requestedMaximum, int largeChange, int smallChange)
    {
        var maximumValue = Math.Max(0, requestedMaximum);
        scrollBar.Minimum = 0;
        scrollBar.LargeChange = Math.Max(1, largeChange);
        scrollBar.SmallChange = Math.Max(1, smallChange);
        scrollBar.Maximum = maximumValue + scrollBar.LargeChange - 1;
        scrollBar.Enabled = maximumValue > 0;
        scrollBar.Value = Math.Clamp(scrollBar.Value, 0, maximumValue);
    }

    private void UpdateCanvasScroll()
    {
        _canvas.ScrollOffset = _verticalScroll.Value * _canvas.RowHeight;
        _canvas.HorizontalOffset = _horizontalScroll.Visible ? _horizontalScroll.Value : 0;
        UpdateOverviewViewports();
        _canvas.Invalidate();
        MemoGeometryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CanvasMouseWheel(object? sender, MouseEventArgs e)
    {
        var scrollLines = SystemInformation.MouseWheelScrollLines;
        var lineCount = scrollLines == -1 ? _visibleVisualRowCount : Math.Max(1, scrollLines);
        var steps = Math.Max(1, Math.Abs(e.Delta) / SystemInformation.MouseWheelScrollDelta);
        SetVerticalScrollValue(_verticalScroll.Value + ((e.Delta > 0 ? -1 : 1) * steps * lineCount));
    }

    private void ScrollToDiffRow(int diffRow)
    {
        if (!_diffRowToFirstVisualLine.TryGetValue(diffRow, out var visualRow)) return;
        SetVerticalScrollValue(visualRow);
    }

    private void SetVerticalScrollValue(int value)
    {
        var clamped = Math.Clamp(value, 0, _verticalMaximumRow);
        if (_verticalScroll.Value == clamped) UpdateCanvasScroll();
        else _verticalScroll.Value = clamped;
    }

    private int GetCurrentDiffRow()
    {
        if (_visualRows.Count == 0) return 0;
        var visualRow = Math.Clamp(_verticalScroll.Value, 0, _visualRows.Count - 1);
        return _visualRows[visualRow].DiffRowIndex;
    }

    private void UpdateOverviewViewports()
    {
        if (_visualRows.Count == 0) return;
        var firstVisualRow = Math.Clamp(_verticalScroll.Value, 0, _visualRows.Count - 1);
        var lastVisualRow = Math.Clamp(firstVisualRow + _visibleVisualRowCount - 1, 0, _visualRows.Count - 1);
        var firstDiffRow = _visualRows[firstVisualRow].DiffRowIndex;
        var lastDiffRow = _visualRows[lastVisualRow].DiffRowIndex;
        var visibleDiffRows = Math.Max(1, lastDiffRow - firstDiffRow + 1);
        _oldOverview.UpdateViewport(firstDiffRow, visibleDiffRows);
        _newOverview.UpdateViewport(firstDiffRow, visibleDiffRows);
    }

    private int MeasureFragments(IReadOnlyList<InlineDiffFragment> fragments) =>
        fragments.Sum(fragment => TextRenderer.MeasureText(fragment.Text, _canvas.Font, Size.Empty, TextFlags).Width);

    private int MeasureCharacter(char character) => character == '\t'
        ? MeasureCharacter(' ') * 4
        : TextRenderer.MeasureText(character.ToString(), _canvas.Font, Size.Empty, TextFlags).Width;

    private static void AppendFragmentCharacter(List<InlineDiffFragment> target, InlineDiffFragmentKind kind, char character)
    {
        if (target.LastOrDefault() is { } last && last.Kind == kind)
        {
            target[^1] = last with { Text = last.Text + character };
        }
        else
        {
            target.Add(new InlineDiffFragment(kind, character.ToString()));
        }
    }

    private static Label CreateHeader(string text) => new()
    {
        AutoEllipsis = true,
        BackColor = Color.FromArgb(245, 247, 250),
        Dock = DockStyle.Fill,
        Font = new Font("Segoe UI", 9f, FontStyle.Bold),
        Padding = new Padding(10, 7, 10, 4),
        Text = text,
    };

    private static string ToDisplayText(string text) => text
        .Replace("\r\n", "↵", StringComparison.Ordinal)
        .Replace("\n", "↵", StringComparison.Ordinal)
        .Replace("\r", "↵", StringComparison.Ordinal);

    private static readonly TextFormatFlags TextFlags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    /// <summary>One comparison cell: the row plus the column it belongs to.</summary>
    internal readonly record struct DiffMemoTarget(int RowIndex, DiffMemoSide Side, int? MemoNumber = null);

    private sealed record VisualDiffRow(
        int DiffRowIndex,
        int? OldLine,
        int? NewLine,
        IReadOnlyList<InlineDiffFragment> OldFragments,
        IReadOnlyList<InlineDiffFragment> NewFragments,
        DiffChangeKind Kind,
        DiffRowPresentationKind Presentation,
        bool OldImaginary,
        bool NewImaginary);

    private sealed class DiffCanvas : Control
    {
        private const string DocumentFontFamily = "맑은 고딕";
        private const int RowVerticalPadding = 7;
        private const int MemoAccentWidth = 3;
        private const int MemoFlagWidth = 22;
        private const int MemoFlagRightMargin = 6;
        private const int MemoAddSize = 22;
        private IReadOnlyList<VisualDiffRow> _rows = Array.Empty<VisualDiffRow>();
        private IReadOnlyDictionary<int, (IReadOnlyList<int> Old, IReadOnlyList<int> New)> _memoNumbers =
            new Dictionary<int, (IReadOnlyList<int>, IReadOnlyList<int>)>();
        private int? _highlightedDiffRow;
        private int? _pinnedDiffRow;
        private DiffMemoTarget? _pinnedMemoTarget;
        private DiffMemoTarget? _hoverMemoTarget;
        private int _hoverVisualRow = -1;
        private Rectangle _oldContentBounds;
        private Rectangle _newContentBounds;
        private Font _documentFont;
        private Font _sectionHeaderFont;
        // Chrome rather than document text, so this one does not follow the
        // reader's document font size.
        private readonly Font _memoFlagFont = new("Segoe UI", 7.5f, FontStyle.Bold);
        private HdiffThemePalette _theme = HdiffThemes.Light;
        private bool _showRowSeparators;
        private bool _textSelectionEnabled = true;
        private TextPosition? _selectionAnchor;
        private TextPosition? _selectionEnd;
        private bool _selecting;
        private readonly ContextMenuStrip _selectionMenu = new();
        private readonly ToolStripMenuItem _copySelectionItem = new("복사");
        private readonly ToolStripMenuItem _selectAllItem = new("전체 선택");
        private readonly ToolStripSeparator _memoSeparator = new();
        private readonly ToolStripMenuItem _addMemoItem = new("검토 메모 추가…");
        private readonly ToolStripMenuItem _openMemoItem = new("검토 메모 보기…");
        private int _contextDiffRow = -1;
        private DiffMemoSide _contextSide = DiffMemoSide.New;

        public DiffCanvas()
        {
            BackColor = Color.White;
            // HWP reports are predominantly Korean prose. Consolas has no
            // Korean glyphs, so Windows silently mixed in a fallback font and
            // made the line rhythm look cramped. Use the standard Korean UI
            // family consistently instead.
            _documentFont = CreateDocumentFont(10.5f);
            _sectionHeaderFont = CreateSectionHeaderFont(_documentFont);
            Font = _documentFont;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            TabStop = true;

            _copySelectionItem.ShortcutKeyDisplayString = "Ctrl+C";
            _copySelectionItem.Click += (_, _) => CopySelection();
            _selectAllItem.ShortcutKeyDisplayString = "Ctrl+A";
            _selectAllItem.Click += (_, _) => SelectAllOnActiveSide();
            _addMemoItem.ShortcutKeyDisplayString = "Ctrl+M";
            _addMemoItem.Click += (_, _) => RequestMemo(MemoAddRequested);
            _openMemoItem.Click += (_, _) => RequestMemo(MemoOpenRequested);
            _selectionMenu.Items.AddRange(new ToolStripItem[]
            {
                _copySelectionItem, _selectAllItem, _memoSeparator, _addMemoItem, _openMemoItem,
            });
            _selectionMenu.Opening += (_, e) =>
            {
                // The memo commands stay usable when text selection is off,
                // so the menu itself is no longer cancelled in that case.
                var location = PointToClient(Cursor.Position);
                _contextDiffRow = GetDiffRowAt(location);
                // The column the reviewer right-clicked is the column the memo
                // is about, so no extra chooser is needed.
                _contextSide = ResolveSide(_contextDiffRow, GetSide(location) == true ? DiffMemoSide.Old : DiffMemoSide.New);
                _copySelectionItem.Enabled = _textSelectionEnabled && HasSelection;
                _selectAllItem.Enabled = _textSelectionEnabled && _rows.Count > 0;
                _addMemoItem.Text = _contextSide == DiffMemoSide.Old ? "변경 전에 검토 메모 추가…" : "변경 후에 검토 메모 추가…";
                _addMemoItem.Enabled = _contextDiffRow >= 0;
                _openMemoItem.Enabled = _contextDiffRow >= 0
                    && GetMemoCount(_contextDiffRow, _contextSide == DiffMemoSide.Old) > 0;
            };
            ContextMenuStrip = _selectionMenu;
        }

        /// <summary>A reviewer asked for a new memo on this comparison row.</summary>
        public event EventHandler<DiffMemoTarget>? MemoAddRequested;

        /// <summary>A reviewer clicked the memo flag of this comparison row.</summary>
        public event EventHandler<DiffMemoTarget>? MemoOpenRequested;

        public int? HighlightedDiffRow
        {
            get => _highlightedDiffRow;
            set
            {
                if (_highlightedDiffRow == value) return;
                _highlightedDiffRow = value;
                Invalidate();
            }
        }

        public int? PinnedDiffRow
        {
            get => _pinnedDiffRow;
            set
            {
                if (_pinnedDiffRow == value) return;
                _pinnedDiffRow = value;
                _pinnedMemoTarget = value is null ? null : new DiffMemoTarget(value.Value,DiffMemoSide.New);
                Invalidate();
            }
        }
        public DiffMemoTarget? PinnedMemoTarget
        {
            get => _pinnedMemoTarget;
            set { if(_pinnedMemoTarget==value)return;_pinnedMemoTarget=value;_pinnedDiffRow=value?.RowIndex;Invalidate(); }
        }

        public bool TryGetPinnedMemoFlagBounds(out Rectangle bounds)
        {
            bounds = Rectangle.Empty;
            if (_pinnedMemoTarget is not { } target) return false;

            for (var index = 0; index < _rows.Count; index++)
            {
                var row = _rows[index];
                if (row.DiffRowIndex != target.RowIndex || !IsFirstVisualLineOfDiffRow(index)) continue;

                var y = (index * RowHeight) - ScrollOffset;
                if (y + RowHeight <= 0 || y >= ClientSize.Height) return false;
                var numbers = GetMemoNumbers(row.DiffRowIndex, target.Side == DiffMemoSide.Old);
                var flagIndex = target.MemoNumber is { } number ? IndexOf(numbers, number) : 0;
                if (flagIndex < 0) return false;
                bounds = GetMemoFlagBounds(row, y, target.Side == DiffMemoSide.Old, flagIndex);
                return !bounds.IsEmpty;
            }

            return false;
        }

        public void SetMemoNumbers(
            IReadOnlyDictionary<int, (IReadOnlyList<int> Old, IReadOnlyList<int> New)> numbersByDiffRow)
        {
            _memoNumbers = numbersByDiffRow;
            Invalidate();
        }

        private (IReadOnlyList<int> Old, IReadOnlyList<int> New) GetMemoNumbers(int diffRowIndex) =>
            _memoNumbers.TryGetValue(diffRowIndex, out var numbers)
                ? numbers
                : (Array.Empty<int>(), Array.Empty<int>());

        private IReadOnlyList<int> GetMemoNumbers(int diffRowIndex, bool oldSide)
        {
            var numbers = GetMemoNumbers(diffRowIndex);
            return oldSide ? numbers.Old : numbers.New;
        }

        private int GetMemoCount(int diffRowIndex, bool oldSide) =>
            GetMemoNumbers(diffRowIndex, oldSide).Count;

        private int GetMemoCount(int diffRowIndex)
        {
            var numbers = GetMemoNumbers(diffRowIndex);
            return numbers.Old.Count + numbers.New.Count;
        }

        private static int IndexOf(IReadOnlyList<int> numbers, int number)
        {
            for (var index = 0; index < numbers.Count; index++)
                if (numbers[index] == number) return index;
            return -1;
        }

        private void RequestMemo(EventHandler<DiffMemoTarget>? handler)
        {
            if (_contextDiffRow < 0) return;
            handler?.Invoke(this, new DiffMemoTarget(_contextDiffRow, _contextSide));
        }

        public int ScrollOffset { get; set; }
        public int HorizontalOffset { get; set; }
        public int RowHeight => Font.Height + RowVerticalPadding;
        public float DocumentFontSizePoints => _documentFont.SizeInPoints;
        public Rectangle OldContentBounds => _oldContentBounds;
        public Rectangle NewContentBounds => _newContentBounds;
        public bool TextSelectionEnabled
        {
            get => _textSelectionEnabled;
            set
            {
                if (_textSelectionEnabled == value) return;
                _textSelectionEnabled = value;
                Cursor = value ? Cursors.IBeam : Cursors.Default;
                if (!value) ClearSelection();
            }
        }

        public bool ShowRowSeparators
        {
            get => _showRowSeparators;
            set
            {
                if (_showRowSeparators == value) return;
                _showRowSeparators = value;
                Invalidate();
            }
        }

        public void SetDocumentFontSize(float points)
        {
            var nextFont = CreateDocumentFont(points);
            var nextHeaderFont = CreateSectionHeaderFont(nextFont);
            var previousFont = _documentFont;
            var previousHeaderFont = _sectionHeaderFont;
            _documentFont = nextFont;
            _sectionHeaderFont = nextHeaderFont;
            Font = nextFont;
            previousFont.Dispose();
            previousHeaderFont.Dispose();
        }

        private static Font CreateDocumentFont(float points) =>
            new(DocumentFontFamily, points, FontStyle.Regular, GraphicsUnit.Point);

        private static Font CreateSectionHeaderFont(Font documentFont) =>
            new(documentFont, FontStyle.Bold);

        public void ApplyTheme(HdiffThemePalette theme)
        {
            _theme = theme;
            BackColor = theme.CanvasBack;
            Invalidate();
        }

        public void SetRows(IReadOnlyList<VisualDiffRow> rows)
        {
            _rows = rows;
            ClearSelection();
            Invalidate();
        }

        public void ConfigureColumns(Rectangle oldContentBounds, Rectangle newContentBounds)
        {
            _oldContentBounds = oldContentBounds;
            _newContentBounds = newContentBounds;
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _selectionMenu.Dispose();
                _documentFont.Dispose();
                _sectionHeaderFont.Dispose();
                _memoFlagFont.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_textSelectionEnabled && keyData == (Keys.Control | Keys.C))
            {
                CopySelection();
                return true;
            }
            if (_textSelectionEnabled && keyData == (Keys.Control | Keys.A))
            {
                SelectAllOnActiveSide();
                return true;
            }
            if (_textSelectionEnabled && keyData == Keys.Escape)
            {
                ClearSelection();
                return true;
            }
            if (keyData == (Keys.Control | Keys.M))
            {
                var target = GetMemoTarget();
                if (target is null) return true;
                MemoAddRequested?.Invoke(this, target.Value);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>
        /// Ctrl+M follows the reader's attention: the selected paragraph first,
        /// then whatever the pointer is over, then the top visible row.
        /// </summary>
        private DiffMemoTarget? GetMemoTarget()
        {
            if (_rows.Count == 0) return null;
            if (_selectionAnchor is { } anchor && anchor.VisualRow >= 0 && anchor.VisualRow < _rows.Count)
            {
                var selectedRow = _rows[anchor.VisualRow].DiffRowIndex;
                var selectedSide = anchor.OldSide ? DiffMemoSide.Old : DiffMemoSide.New;
                return new DiffMemoTarget(selectedRow, ResolveSide(selectedRow, selectedSide));
            }

            var location = PointToClient(Cursor.Position);
            var pointerRow = GetDiffRowAt(location);
            if (pointerRow >= 0)
            {
                var pointerSide = GetSide(location) == true ? DiffMemoSide.Old : DiffMemoSide.New;
                return new DiffMemoTarget(pointerRow, ResolveSide(pointerRow, pointerSide));
            }

            var visibleRow = _rows[Math.Clamp(ScrollOffset / RowHeight, 0, _rows.Count - 1)].DiffRowIndex;
            return new DiffMemoTarget(visibleRow, ResolveSide(visibleRow, DiffMemoSide.New));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if(e.Button==MouseButtons.Left&&TryGetMemoAdd(e.Location,out var add)){Focus();MemoAddRequested?.Invoke(this,add);return;}
            if (e.Button == MouseButtons.Left && TryGetMemoFlag(e.Location, out var memoTarget))
            {
                Focus();
                MemoOpenRequested?.Invoke(this, memoTarget);
                return;
            }
            if (!_textSelectionEnabled || e.Button != MouseButtons.Left) return;

            var position = HitTest(e.Location);
            if (position is null) return;
            Focus();
            Capture = true;
            _selecting = true;
            _selectionAnchor = position;
            _selectionEnd = position;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            UpdateHover(e.Location);
            Cursor = TryGetMemoAdd(e.Location,out _) || TryGetMemoFlag(e.Location, out _)
                ? Cursors.Hand
                : _textSelectionEnabled && IsInsideDocumentColumn(e.Location)
                    ? Cursors.IBeam
                    : Cursors.Default;
            if (!_selecting || _selectionAnchor is null) return;

            var position = HitTest(e.Location, _selectionAnchor.Value.OldSide);
            if (position is null) return;
            _selectionEnd = position;
            Invalidate();
        }
        protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);if(_hoverMemoTarget is null)return;_hoverMemoTarget=null;_hoverVisualRow=-1;Invalidate();}

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            _selecting = false;
            Capture = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.Clear(_theme.CanvasBack);
            if (_rows.Count > 0)
            {
                var firstRow = Math.Clamp(ScrollOffset / RowHeight, 0, _rows.Count - 1);
                var y = -(ScrollOffset % RowHeight);
                for (var index = firstRow; index < _rows.Count && y < ClientSize.Height; index++, y += RowHeight)
                {
                    var row = _rows[index];
                    DrawCell(e.Graphics, _oldContentBounds, y, index, row.OldLine, row.OldFragments, row.Kind, row.Presentation, oldSide: true, row.OldImaginary);
                    DrawCell(e.Graphics, _newContentBounds, y, index, row.NewLine, row.NewFragments, row.Kind, row.Presentation, oldSide: false, row.NewImaginary);
                    DrawMemoIndicators(e.Graphics, y, index, row);
                    DrawMemoAdd(e.Graphics,y,index,row);
                    if (_showRowSeparators && IsLastVisualLineOfDiffRow(index))
                        DrawRowSeparator(e.Graphics, y + RowHeight - 1);
                }
            }

            DrawCenterDivider(e.Graphics);
        }

        private void DrawCenterDivider(Graphics graphics)
        {
            if (_newContentBounds.X <= 0) return;
            var dividerLeft = _newContentBounds.X - SplitterWidth;
            using var dividerBack = new SolidBrush(_theme.AppBack);
            graphics.FillRectangle(dividerBack, dividerLeft, 0, SplitterWidth, ClientSize.Height);
            using var divider = new Pen(_theme.Border);
            graphics.DrawLine(divider, dividerLeft + (SplitterWidth / 2), 0,
                dividerLeft + (SplitterWidth / 2), ClientSize.Height);
        }

        private void DrawRowSeparator(Graphics graphics, int y)
        {
            if (y < 0 || y >= ClientSize.Height) return;
            using var separator = new Pen(Color.FromArgb(80, _theme.Border));
            graphics.DrawLine(separator, 0, y, ClientSize.Width - 1, y);
        }

        private bool IsLastVisualLineOfDiffRow(int visualRowIndex) =>
            visualRowIndex >= _rows.Count - 1
            || _rows[visualRowIndex + 1].DiffRowIndex != _rows[visualRowIndex].DiffRowIndex;

        private bool IsFirstVisualLineOfDiffRow(int visualRowIndex) =>
            visualRowIndex <= 0
            || _rows[visualRowIndex - 1].DiffRowIndex != _rows[visualRowIndex].DiffRowIndex;

        /// <summary>
        /// Draws the memo signals over both cells: an accent rail on every
        /// visual line of the row and one numbered flag on its first line, the
        /// way a Word comment anchor marks the commented text.
        /// </summary>
        private void DrawMemoIndicators(Graphics graphics, int y, int visualRowIndex, VisualDiffRow row)
        {
            var numbers = GetMemoNumbers(row.DiffRowIndex);
            var marked = _highlightedDiffRow == row.DiffRowIndex || _pinnedDiffRow == row.DiffRowIndex;
            if (numbers.Old.Count == 0 && numbers.New.Count == 0 && !marked) return;

            using (var accent = new SolidBrush(_theme.MemoAccent))
            {
                if (numbers.Old.Count > 0 && _oldContentBounds.Width > 0)
                    graphics.FillRectangle(accent, _oldContentBounds.X, y, MemoAccentWidth, RowHeight);
                if (numbers.New.Count > 0 && _newContentBounds.Width > 0)
                    graphics.FillRectangle(accent, _newContentBounds.X, y, MemoAccentWidth, RowHeight);
            }

            if (marked)
            {
                using var border = new Pen(_theme.MemoAccent);
                if (IsFirstVisualLineOfDiffRow(visualRowIndex))
                    graphics.DrawLine(border, 0, y, ClientSize.Width - 1, y);
                if (IsLastVisualLineOfDiffRow(visualRowIndex))
                    graphics.DrawLine(border, 0, y + RowHeight - 1, ClientSize.Width - 1, y + RowHeight - 1);
            }

            if (!IsFirstVisualLineOfDiffRow(visualRowIndex)) return;
            for (var index = 0; index < numbers.Old.Count; index++)
                DrawMemoFlag(graphics, GetMemoFlagBounds(row, y, oldSide: true, index), numbers.Old[index]);
            for (var index = 0; index < numbers.New.Count; index++)
                DrawMemoFlag(graphics, GetMemoFlagBounds(row, y, oldSide: false, index), numbers.New[index]);
        }

        private void DrawMemoAdd(Graphics g,int y,int visual,VisualDiffRow row)
        {
            if(_hoverMemoTarget is not { } t||t.RowIndex!=row.DiffRowIndex||visual!=_hoverVisualRow)return;
            var r=GetMemoAddBounds(row,y,t.Side==DiffMemoSide.Old);if(r.IsEmpty)return;var s=g.SmoothingMode;g.SmoothingMode=SmoothingMode.AntiAlias;
            using var fill=new SolidBrush(_theme.SurfaceBack);using var pen=new Pen(_theme.MemoAccent);g.FillEllipse(fill,r);g.DrawEllipse(pen,r);
            var x=r.Left+r.Width/2;var cy=r.Top+r.Height/2;g.DrawLine(pen,x-4,cy,x+4,cy);g.DrawLine(pen,x,cy-4,x,cy+4);g.SmoothingMode=s;
        }

        private Rectangle GetMemoAddBounds(VisualDiffRow row,int y,bool old)
        {
            if(old?row.OldImaginary:row.NewImaginary)return Rectangle.Empty;var cell=old?_oldContentBounds:_newContentBounds;
            var flagCount=GetMemoCount(row.DiffRowIndex,old);
            var right=cell.Right-MemoFlagRightMargin-(flagCount>0?flagCount*(MemoFlagWidth+3)+2:0);
            return new Rectangle(right-MemoAddSize,y+Math.Max(1,(RowHeight-MemoAddSize)/2),MemoAddSize,MemoAddSize);
        }

        private void UpdateHover(Point p)
        {
            DiffMemoTarget? next=null;var visual=-1;var row=GetDiffRowAt(p);var side=GetSide(p);
            if(row>=0&&side is not null){next=new DiffMemoTarget(row,ResolveSide(row,side.Value?DiffMemoSide.Old:DiffMemoSide.New));visual=(ScrollOffset+p.Y)/RowHeight;}
            if(next==_hoverMemoTarget&&visual==_hoverVisualRow)return;_hoverMemoTarget=next;_hoverVisualRow=visual;Invalidate();
        }

        private bool TryGetMemoAdd(Point p,out DiffMemoTarget target)
        {
            target=default;if(_hoverMemoTarget is not { } t||_rows.Count==0)return false;var visual=(ScrollOffset+p.Y)/RowHeight;
            if(visual<0||visual>=_rows.Count||visual!=_hoverVisualRow)return false;var row=_rows[visual];if(row.DiffRowIndex!=t.RowIndex)return false;
            var y=visual*RowHeight-ScrollOffset;if(!GetMemoAddBounds(row,y,t.Side==DiffMemoSide.Old).Contains(p))return false;target=t;return true;
        }

        private void DrawMemoFlag(Graphics graphics, Rectangle bounds, int number)
        {
            if (bounds.Width <= 0) return;

            var smoothing = graphics.SmoothingMode;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = CreateRoundedRectangle(bounds, bounds.Height / 2))
            using (var fill = new SolidBrush(_theme.MemoAccent))
            {
                graphics.FillPath(fill, path);
            }
            graphics.SmoothingMode = smoothing;
            TextRenderer.DrawText(graphics, number.ToString(), _memoFlagFont, bounds, _theme.MemoFlagText,
                TextFlags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        /// <summary>Each column carries its own flag; an empty cell never gets one.</summary>
        private Rectangle GetMemoFlagBounds(VisualDiffRow row, int y, bool oldSide, int flagIndex)
        {
            if (oldSide ? row.OldImaginary : row.NewImaginary) return Rectangle.Empty;
            var bounds = oldSide ? _oldContentBounds : _newContentBounds;
            if (bounds.Width <= LineNumberWidth + MemoFlagWidth + MemoFlagRightMargin) return Rectangle.Empty;
            var numbers = GetMemoNumbers(row.DiffRowIndex, oldSide);
            if (flagIndex < 0 || flagIndex >= numbers.Count) return Rectangle.Empty;
            var height = Math.Max(12, RowHeight - 8);
            var flagsToRight = numbers.Count - flagIndex - 1;
            return new Rectangle(
                bounds.Right - MemoFlagWidth - MemoFlagRightMargin - (flagsToRight * (MemoFlagWidth + 3)),
                y + ((RowHeight - height) / 2),
                MemoFlagWidth,
                height);
        }

        private bool TryGetMemoFlag(Point location, out DiffMemoTarget target)
        {
            target = default;
            if (_rows.Count == 0 || location.Y < 0) return false;
            var visualRow = (ScrollOffset + location.Y) / RowHeight;
            if (visualRow < 0 || visualRow >= _rows.Count) return false;
            var row = _rows[visualRow];
            if (!IsFirstVisualLineOfDiffRow(visualRow)) return false;
            var y = (visualRow * RowHeight) - ScrollOffset;

            foreach (var oldSide in new[] { true, false })
            {
                var numbers = GetMemoNumbers(row.DiffRowIndex, oldSide);
                for (var index = 0; index < numbers.Count; index++)
                {
                    var flag = GetMemoFlagBounds(row, y, oldSide, index);
                    if (flag.Width <= 0 || !flag.Contains(location)) continue;
                    target = new DiffMemoTarget(row.DiffRowIndex,
                        oldSide ? DiffMemoSide.Old : DiffMemoSide.New, numbers[index]);
                    return true;
                }
            }
            return false;
        }

        /// <summary>A memo cannot be written on an empty cell, so one-sided rows force their column.</summary>
        private DiffMemoSide ResolveSide(int diffRowIndex, DiffMemoSide requested)
        {
            if (diffRowIndex < 0) return requested;
            var row = _rows.FirstOrDefault(candidate => candidate.DiffRowIndex == diffRowIndex);
            if (row is null) return requested;
            if (row.OldImaginary) return DiffMemoSide.New;
            if (row.NewImaginary) return DiffMemoSide.Old;
            return requested;
        }

        private int GetDiffRowAt(Point location)
        {
            if (_rows.Count == 0 || location.Y < 0) return -1;
            if (!_oldContentBounds.Contains(location) && !_newContentBounds.Contains(location)) return -1;
            var visualRow = (ScrollOffset + location.Y) / RowHeight;
            return visualRow < 0 || visualRow >= _rows.Count ? -1 : _rows[visualRow].DiffRowIndex;
        }

        private static GraphicsPath CreateRoundedRectangle(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            var diameter = Math.Max(1, radius * 2);
            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void DrawCell(
            Graphics graphics,
            Rectangle bounds,
            int y,
            int visualRowIndex,
            int? lineNumber,
            IReadOnlyList<InlineDiffFragment> fragments,
            DiffChangeKind kind,
            DiffRowPresentationKind presentation,
            bool oldSide,
            bool imaginary)
        {
            if (bounds.Width <= 0) return;
            var lineBounds = new Rectangle(bounds.X, y, bounds.Width, RowHeight);
            var lineBackColor = presentation == DiffRowPresentationKind.SectionHeader && kind == DiffChangeKind.Unchanged
                ? _theme.HeaderBack
                : GetLineBackColor(kind, oldSide);
            using var lineBrush = new SolidBrush(lineBackColor);
            graphics.FillRectangle(lineBrush, lineBounds);

            var gutter = new Rectangle(bounds.X, y, Math.Min(LineNumberWidth, bounds.Width), RowHeight);
            var lineText = presentation is DiffRowPresentationKind.SectionHeader or DiffRowPresentationKind.Spacer
                ? string.Empty
                : lineNumber?.ToString() ?? string.Empty;
            var markerBounds = new Rectangle(gutter.X, y, Math.Min(16, gutter.Width), RowHeight);
            var numberBounds = Rectangle.FromLTRB(markerBounds.Right, y, gutter.Right - 2, y + RowHeight);
            using var gutterBrush = new SolidBrush(_theme.GutterBack);
            graphics.FillRectangle(gutterBrush, gutter);
            var marker = GetChangeMarker(kind, oldSide, imaginary, lineNumber.HasValue);
            if (marker is not null)
            {
                var markerColor = marker switch
                {
                    '+' => _theme.AddedText,
                    '−' => _theme.RemovedText,
                    _ => _theme.MutedText,
                };
                TextRenderer.DrawText(graphics, marker.Value.ToString(), Font, markerBounds, markerColor,
                    TextFlags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            TextRenderer.DrawText(graphics, lineText, Font, numberBounds, _theme.GutterText, TextFlags | TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            using var gutterPen = new Pen(_theme.Border);
            graphics.DrawLine(gutterPen, gutter.Right - 1, y + 2, gutter.Right - 1, y + RowHeight - 3);

            var textBounds = Rectangle.FromLTRB(gutter.Right + 5, y, bounds.Right, y + RowHeight);
            var saved = graphics.Save();
            try
            {
                graphics.SetClip(textBounds);
                var x = textBounds.X - HorizontalOffset;
                if (fragments.Count == 0)
                {
                    return;
                }

                foreach (var fragment in fragments)
                {
                    var textFont = presentation == DiffRowPresentationKind.SectionHeader
                        ? _sectionHeaderFont
                        : Font;
                    var width = TextRenderer.MeasureText(fragment.Text, textFont, Size.Empty, TextFlags).Width;
                    var (foreColor, backColor) = GetFragmentColors(fragment.Kind, lineBackColor);
                    using var fragmentBrush = new SolidBrush(backColor);
                    graphics.FillRectangle(fragmentBrush, x, y, width, RowHeight);
                    TextRenderer.DrawText(graphics, fragment.Text, textFont, new Point(x, y + 2), foreColor, TextFlags);
                    x += width;
                }

                DrawSelection(graphics, textBounds, y, visualRowIndex, oldSide, string.Concat(fragments.Select(fragment => fragment.Text)));
            }
            finally
            {
                graphics.Restore(saved);
            }
        }

        private void DrawSelection(Graphics graphics, Rectangle textBounds, int y, int visualRowIndex, bool oldSide, string text)
        {
            var range = GetSelectionRange(visualRowIndex, oldSide, text.Length);
            if (range is null || range.Value.Start == range.Value.End) return;

            var startX = textBounds.X - HorizontalOffset + MeasureTextWidth(text.AsSpan(0, range.Value.Start));
            var endX = textBounds.X - HorizontalOffset + MeasureTextWidth(text.AsSpan(0, range.Value.End));
            using var selectionBrush = new SolidBrush(Color.FromArgb(105, SystemColors.Highlight));
            graphics.FillRectangle(selectionBrush, startX, y, Math.Max(1, endX - startX), RowHeight);
        }

        private TextPosition? HitTest(Point location, bool? lockedOldSide = null)
        {
            if (_rows.Count == 0) return null;
            var oldSide = lockedOldSide ?? GetSide(location);
            if (oldSide is null) return null;

            var bounds = oldSide.Value ? _oldContentBounds : _newContentBounds;
            var clampedY = Math.Clamp(location.Y, 0, Math.Max(0, ClientSize.Height - 1));
            var visualRow = Math.Clamp((ScrollOffset + clampedY) / RowHeight, 0, _rows.Count - 1);
            var text = GetRowText(_rows[visualRow], oldSide.Value);
            var textStartX = bounds.X + Math.Min(LineNumberWidth, bounds.Width) + 5 - HorizontalOffset;
            var relativeX = Math.Max(0, location.X - textStartX);
            return new TextPosition(oldSide.Value, visualRow, FindCharacterIndex(text, relativeX));
        }

        private bool? GetSide(Point location)
        {
            if (_oldContentBounds.Contains(location)) return true;
            if (_newContentBounds.Contains(location)) return false;
            return null;
        }

        private bool IsInsideDocumentColumn(Point location) => GetSide(location) is not null;

        private int FindCharacterIndex(string text, int relativeX)
        {
            if (relativeX <= 0 || text.Length == 0) return 0;
            var low = 0;
            var high = text.Length;
            while (low < high)
            {
                var middle = (low + high) / 2;
                var width = MeasureTextWidth(text.AsSpan(0, middle));
                if (width < relativeX) low = middle + 1;
                else high = middle;
            }

            if (low == 0) return 0;
            var leftWidth = MeasureTextWidth(text.AsSpan(0, low - 1));
            var rightWidth = MeasureTextWidth(text.AsSpan(0, low));
            return relativeX - leftWidth < rightWidth - relativeX ? low - 1 : low;
        }

        private int MeasureTextWidth(ReadOnlySpan<char> text) => text.IsEmpty
            ? 0
            : TextRenderer.MeasureText(text.ToString(), Font, Size.Empty, TextFlags).Width;

        private static string GetRowText(VisualDiffRow row, bool oldSide) =>
            string.Concat((oldSide ? row.OldFragments : row.NewFragments).Select(fragment => fragment.Text));

        private bool HasSelection =>
            _selectionAnchor is not null && _selectionEnd is not null && _selectionAnchor.Value != _selectionEnd.Value;

        private (int Start, int End)? GetSelectionRange(int visualRow, bool oldSide, int textLength)
        {
            if (!HasSelection || _selectionAnchor!.Value.OldSide != oldSide) return null;
            var (start, end) = NormalizedSelection();
            if (visualRow < start.VisualRow || visualRow > end.VisualRow) return null;
            var rangeStart = visualRow == start.VisualRow ? start.CharacterIndex : 0;
            var rangeEnd = visualRow == end.VisualRow ? end.CharacterIndex : textLength;
            return (Math.Clamp(rangeStart, 0, textLength), Math.Clamp(rangeEnd, 0, textLength));
        }

        private (TextPosition Start, TextPosition End) NormalizedSelection()
        {
            var anchor = _selectionAnchor!.Value;
            var end = _selectionEnd!.Value;
            return Compare(anchor, end) <= 0 ? (anchor, end) : (end, anchor);
        }

        private static int Compare(TextPosition left, TextPosition right)
        {
            var rowComparison = left.VisualRow.CompareTo(right.VisualRow);
            return rowComparison != 0 ? rowComparison : left.CharacterIndex.CompareTo(right.CharacterIndex);
        }

        private void CopySelection()
        {
            var selectedText = GetSelectedText();
            if (selectedText.Length == 0) return;
            try
            {
                Clipboard.SetText(selectedText);
            }
            catch (ExternalException)
            {
                System.Media.SystemSounds.Beep.Play();
            }
        }

        private string GetSelectedText()
        {
            if (!HasSelection) return string.Empty;
            var (start, end) = NormalizedSelection();
            var builder = new System.Text.StringBuilder();
            int? lastSourceRow = null;

            for (var visualRow = start.VisualRow; visualRow <= end.VisualRow; visualRow++)
            {
                var row = _rows[visualRow];
                var text = GetRowText(row, start.OldSide);
                var imaginary = start.OldSide ? row.OldImaginary : row.NewImaginary;
                if (imaginary) continue;

                var rangeStart = visualRow == start.VisualRow ? Math.Clamp(start.CharacterIndex, 0, text.Length) : 0;
                var rangeEnd = visualRow == end.VisualRow ? Math.Clamp(end.CharacterIndex, 0, text.Length) : text.Length;
                if (lastSourceRow is not null && lastSourceRow != row.DiffRowIndex)
                    builder.AppendLine();
                if (rangeEnd > rangeStart)
                    builder.Append(text, rangeStart, rangeEnd - rangeStart);
                lastSourceRow = row.DiffRowIndex;
            }

            return builder.ToString();
        }

        private void SelectAllOnActiveSide()
        {
            if (!_textSelectionEnabled || _rows.Count == 0) return;
            var oldSide = _selectionAnchor?.OldSide ?? true;
            var first = Enumerable.Range(0, _rows.Count)
                .FirstOrDefault(index => !(oldSide ? _rows[index].OldImaginary : _rows[index].NewImaginary));
            var last = Enumerable.Range(0, _rows.Count)
                .LastOrDefault(index => !(oldSide ? _rows[index].OldImaginary : _rows[index].NewImaginary));
            _selectionAnchor = new TextPosition(oldSide, first, 0);
            _selectionEnd = new TextPosition(oldSide, last, GetRowText(_rows[last], oldSide).Length);
            Invalidate();
        }

        private void ClearSelection()
        {
            _selectionAnchor = null;
            _selectionEnd = null;
            _selecting = false;
            Invalidate();
        }

        private (Color ForeColor, Color BackColor) GetFragmentColors(InlineDiffFragmentKind kind, Color lineBackColor) => kind switch
        {
            InlineDiffFragmentKind.Removed => (_theme.RemovedText, _theme.RemovedInlineBack),
            InlineDiffFragmentKind.Added => (_theme.AddedText, _theme.AddedInlineBack),
            _ => (_theme.Text, lineBackColor),
        };

        private static char? GetChangeMarker(DiffChangeKind kind, bool oldSide, bool imaginary, bool isFirstVisualLine)
        {
            if (imaginary || !isFirstVisualLine) return null;
            return kind switch
            {
                DiffChangeKind.Deleted when oldSide => '−',
                DiffChangeKind.Inserted when !oldSide => '+',
                DiffChangeKind.Modified => '~',
                _ => null,
            };
        }

        private Color GetLineBackColor(DiffChangeKind kind, bool oldSide) => kind switch
        {
            DiffChangeKind.Inserted when oldSide => _theme.EmptyLineBack,
            DiffChangeKind.Deleted when !oldSide => _theme.EmptyLineBack,
            DiffChangeKind.Deleted when oldSide => _theme.DeletedLineBack,
            DiffChangeKind.Inserted when !oldSide => _theme.InsertedLineBack,
            DiffChangeKind.Modified when oldSide => _theme.DeletedLineBack,
            DiffChangeKind.Modified => _theme.InsertedLineBack,
            _ => _theme.CanvasBack,
        };

        private readonly record struct TextPosition(bool OldSide, int VisualRow, int CharacterIndex);
    }
}
