using Hdiff.Core.Diff;

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
    private readonly DiffOverviewMap _oldOverview = new(oldSide: true) { Dock = DockStyle.None };
    private readonly DiffOverviewMap _newOverview = new(oldSide: false) { Dock = DockStyle.None };
    private readonly VScrollBar _verticalScroll = new() { Dock = DockStyle.Right };
    private readonly HScrollBar _horizontalScroll = new() { Dock = DockStyle.Bottom, Visible = false };
    private readonly DiffCanvas _canvas = new() { Dock = DockStyle.Fill };
    private readonly Panel _body = new() { Dock = DockStyle.Fill, BackColor = Color.White };
    private readonly List<VisualDiffRow> _visualRows = new();
    private readonly Dictionary<int, int> _diffRowToFirstVisualLine = new();
    private DocumentDiff? _lastDiff;
    private bool _wrapLongLines = true;
    private bool _reflowQueued;
    private int _oldTextWidth;
    private int _newTextWidth;

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
        headers.Controls.Add(new Panel { BackColor = BackColor, Dock = DockStyle.Fill }, 1, 0);
        headers.Controls.Add(_newHeader, 2, 0);

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
        _body.Resize += (_, _) => QueueReflow();

        Controls.Add(_body);
        Controls.Add(_horizontalScroll);
        Controls.Add(_verticalScroll);
        Controls.Add(headers);
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

    public void Clear()
    {
        _lastDiff = null;
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
        var contentHeight = _visualRows.Count * _canvas.RowHeight;
        ConfigureScrollBar(_verticalScroll, contentHeight - _canvas.ClientSize.Height, _canvas.ClientSize.Height, _canvas.RowHeight);

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
        _canvas.ScrollOffset = _verticalScroll.Value;
        _canvas.HorizontalOffset = _horizontalScroll.Visible ? _horizontalScroll.Value : 0;
        UpdateOverviewViewports();
        _canvas.Invalidate();
    }

    private void CanvasMouseWheel(object? sender, MouseEventArgs e)
    {
        var scrollLines = SystemInformation.MouseWheelScrollLines;
        var lineCount = scrollLines == -1 ? Math.Max(1, _verticalScroll.LargeChange / _canvas.RowHeight) : Math.Max(1, scrollLines);
        var steps = Math.Max(1, Math.Abs(e.Delta) / SystemInformation.MouseWheelScrollDelta);
        SetVerticalScrollValue(_verticalScroll.Value + ((e.Delta > 0 ? -1 : 1) * steps * lineCount * _canvas.RowHeight));
    }

    private void ScrollToDiffRow(int diffRow)
    {
        if (!_diffRowToFirstVisualLine.TryGetValue(diffRow, out var visualRow)) return;
        SetVerticalScrollValue(visualRow * _canvas.RowHeight);
    }

    private void SetVerticalScrollValue(int value)
    {
        var maximum = Math.Max(0, _verticalScroll.Maximum - _verticalScroll.LargeChange + 1);
        var clamped = Math.Clamp(value, 0, maximum);
        if (_verticalScroll.Value == clamped) UpdateCanvasScroll();
        else _verticalScroll.Value = clamped;
    }

    private int GetCurrentDiffRow()
    {
        if (_visualRows.Count == 0) return 0;
        var visualRow = Math.Clamp(_verticalScroll.Value / _canvas.RowHeight, 0, _visualRows.Count - 1);
        return _visualRows[visualRow].DiffRowIndex;
    }

    private void UpdateOverviewViewports()
    {
        if (_visualRows.Count == 0) return;
        var firstVisualRow = Math.Clamp(_verticalScroll.Value / _canvas.RowHeight, 0, _visualRows.Count - 1);
        var visibleRows = Math.Max(1, (_canvas.ClientSize.Height / _canvas.RowHeight) + 1);
        var lastVisualRow = Math.Clamp(firstVisualRow + visibleRows - 1, 0, _visualRows.Count - 1);
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

    private sealed record VisualDiffRow(
        int DiffRowIndex,
        int? OldLine,
        int? NewLine,
        IReadOnlyList<InlineDiffFragment> OldFragments,
        IReadOnlyList<InlineDiffFragment> NewFragments,
        DiffChangeKind Kind,
        bool OldImaginary,
        bool NewImaginary);

    private sealed class DiffCanvas : Control
    {
        private IReadOnlyList<VisualDiffRow> _rows = Array.Empty<VisualDiffRow>();
        private Rectangle _oldContentBounds;
        private Rectangle _newContentBounds;
        private Font _documentFont;

        public DiffCanvas()
        {
            BackColor = Color.White;
            _documentFont = new Font("Consolas", 10.5f);
            Font = _documentFont;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            TabStop = false;
        }

        public int ScrollOffset { get; set; }
        public int HorizontalOffset { get; set; }
        public int RowHeight => Font.Height + 4;
        public float DocumentFontSizePoints => _documentFont.SizeInPoints;
        public Rectangle OldContentBounds => _oldContentBounds;
        public Rectangle NewContentBounds => _newContentBounds;

        public void SetDocumentFontSize(float points)
        {
            var nextFont = new Font("Consolas", points, FontStyle.Regular, GraphicsUnit.Point);
            var previousFont = _documentFont;
            _documentFont = nextFont;
            Font = nextFont;
            previousFont.Dispose();
        }

        public void SetRows(IReadOnlyList<VisualDiffRow> rows)
        {
            _rows = rows;
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
            if (disposing) _documentFont.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.Clear(Color.White);
            if (_rows.Count == 0) return;

            var firstRow = Math.Clamp(ScrollOffset / RowHeight, 0, _rows.Count - 1);
            var y = -(ScrollOffset % RowHeight);
            for (var index = firstRow; index < _rows.Count && y < ClientSize.Height; index++, y += RowHeight)
            {
                var row = _rows[index];
                DrawCell(e.Graphics, _oldContentBounds, y, row.OldLine, row.OldFragments, row.Kind, oldSide: true, row.OldImaginary);
                DrawCell(e.Graphics, _newContentBounds, y, row.NewLine, row.NewFragments, row.Kind, oldSide: false, row.NewImaginary);
            }

            using var divider = new Pen(Color.FromArgb(220, 224, 230));
            var dividerX = _oldContentBounds.Right + (SplitterWidth / 2);
            e.Graphics.DrawLine(divider, dividerX, 0, dividerX, ClientSize.Height);
        }

        private void DrawCell(
            Graphics graphics,
            Rectangle bounds,
            int y,
            int? lineNumber,
            IReadOnlyList<InlineDiffFragment> fragments,
            DiffChangeKind kind,
            bool oldSide,
            bool imaginary)
        {
            if (bounds.Width <= 0) return;
            var lineBounds = new Rectangle(bounds.X, y, bounds.Width, RowHeight);
            var lineBackColor = GetLineBackColor(kind, oldSide);
            using var lineBrush = new SolidBrush(lineBackColor);
            graphics.FillRectangle(lineBrush, lineBounds);

            var gutter = new Rectangle(bounds.X, y, Math.Min(LineNumberWidth, bounds.Width), RowHeight);
            var lineText = lineNumber?.ToString() ?? string.Empty;
            TextRenderer.DrawText(graphics, lineText, Font, gutter, Color.FromArgb(105, 112, 122), TextFlags | TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            using var gutterPen = new Pen(Color.FromArgb(220, 224, 230));
            graphics.DrawLine(gutterPen, gutter.Right - 1, y + 2, gutter.Right - 1, y + RowHeight - 3);

            var textBounds = Rectangle.FromLTRB(gutter.Right + 5, y, bounds.Right, y + RowHeight);
            var saved = graphics.Save();
            try
            {
                graphics.SetClip(textBounds);
                var x = textBounds.X - HorizontalOffset;
                if (fragments.Count == 0)
                {
                    if (imaginary)
                    {
                        TextRenderer.DrawText(graphics, "·", Font, new Point(x, y + 2), Color.FromArgb(145, 150, 158), TextFlags);
                    }
                    return;
                }

                foreach (var fragment in fragments)
                {
                    var width = TextRenderer.MeasureText(fragment.Text, Font, Size.Empty, TextFlags).Width;
                    var (foreColor, backColor) = GetFragmentColors(fragment.Kind, lineBackColor);
                    using var fragmentBrush = new SolidBrush(backColor);
                    graphics.FillRectangle(fragmentBrush, x, y, width, RowHeight);
                    TextRenderer.DrawText(graphics, fragment.Text, Font, new Point(x, y + 2), foreColor, TextFlags);
                    x += width;
                }
            }
            finally
            {
                graphics.Restore(saved);
            }
        }

        private static (Color ForeColor, Color BackColor) GetFragmentColors(InlineDiffFragmentKind kind, Color lineBackColor) => kind switch
        {
            InlineDiffFragmentKind.Removed => (Color.FromArgb(154, 31, 35), Color.FromArgb(255, 199, 206)),
            InlineDiffFragmentKind.Added => (Color.FromArgb(0, 104, 50), Color.FromArgb(198, 239, 206)),
            _ => (Color.FromArgb(35, 39, 47), lineBackColor),
        };

        private static Color GetLineBackColor(DiffChangeKind kind, bool oldSide) => kind switch
        {
            DiffChangeKind.Inserted when oldSide => Color.FromArgb(248, 249, 250),
            DiffChangeKind.Deleted when !oldSide => Color.FromArgb(248, 249, 250),
            DiffChangeKind.Deleted when oldSide => Color.FromArgb(255, 242, 242),
            DiffChangeKind.Inserted when !oldSide => Color.FromArgb(239, 252, 245),
            DiffChangeKind.Modified when oldSide => Color.FromArgb(255, 242, 242),
            DiffChangeKind.Modified => Color.FromArgb(239, 252, 245),
            _ => Color.White,
        };
    }
}
