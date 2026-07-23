using Hdiff.Core.Diff;

namespace Hdiff.UI;

/// <summary>Read-only, line-aligned text comparison view for extracted HWP/HWPX text.</summary>
internal sealed class SideBySideDiffView : UserControl
{
    private const int LineNumberColumnWidth = 6;

    private static readonly IReadOnlyList<InlineDiffFragment> EmptyFragments = Array.Empty<InlineDiffFragment>();
    private readonly DiffPane _oldPane;
    private readonly DiffPane _newPane;
    private readonly SplitContainer _split;
    private readonly VScrollBar _verticalScroll = new() { Dock = DockStyle.Right };
    private readonly HScrollBar _horizontalScroll = new() { Dock = DockStyle.Bottom, Visible = false };
    private readonly List<int> _visualToDiffRow = new();
    private readonly Dictionary<int, int> _diffRowToFirstVisualLine = new();
    private bool _wrapLongLines = true;
    private bool _reflowQueued;
    private bool _initialSplitApplied;
    private int _lineHeight;
    private int _contentHeight;
    private int _oldUnwrappedContentWidth;
    private int _newUnwrappedContentWidth;
    private DocumentDiff? _lastDiff;

    private static readonly Color TextBackColor = Color.White;
    private static readonly Color ImaginaryBackColor = Color.FromArgb(248, 249, 250);
    private static readonly Color RemovedLineBackColor = Color.FromArgb(255, 242, 242);
    private static readonly Color RemovedFragmentBackColor = Color.FromArgb(255, 199, 206);
    private static readonly Color AddedLineBackColor = Color.FromArgb(239, 252, 245);
    private static readonly Color AddedFragmentBackColor = Color.FromArgb(198, 239, 206);

    public SideBySideDiffView()
    {
        BackColor = Color.FromArgb(220, 224, 230);
        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            IsSplitterFixed = false,
            SplitterWidth = 5,
        };

        _oldPane = CreatePane("변경 전");
        _newPane = CreatePane("변경 후", oldSide: false);
        _oldPane.Overview.NavigateToLineRequested += (_, line) => ScrollToDiffRow(line);
        _newPane.Overview.NavigateToLineRequested += (_, line) => ScrollToDiffRow(line);
        _split.Panel1.Controls.Add(_oldPane.Root);
        _split.Panel2.Controls.Add(_newPane.Root);
        Controls.Add(_split);
        Controls.Add(_horizontalScroll);
        Controls.Add(_verticalScroll);

        _verticalScroll.ValueChanged += (_, _) => ApplyScrollOffsets();
        _horizontalScroll.ValueChanged += (_, _) => ApplyScrollOffsets();
        _split.SplitterMoved += (_, _) => QueueReflow();
        foreach (var box in ContentTextBoxes)
        {
            box.MouseWheel += ContentMouseWheel;
        }
        ApplyWrappingVisuals();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        BeginInvoke((MethodInvoker)delegate
        {
            SetInitialSplitEqually();
            LayoutContentSurfaces();
        });
    }

    /// <summary>Like VS Code's word-wrap: wrap long extracted paragraphs while retaining side-by-side row alignment.</summary>
    public bool WrapLongLines
    {
        get => _wrapLongLines;
        set
        {
            if (_wrapLongLines == value) return;
            _wrapLongLines = value;
            ApplyWrappingVisuals();
            if (_lastDiff is not null) Render(_lastDiff, resetScroll: false);
        }
    }

    public void Clear()
    {
        _lastDiff = null;
        _oldPane.Header.Text = "변경 전";
        _newPane.Header.Text = "변경 후";
        ClearRenderedText();
    }

    public void SetDiff(DocumentDiff diff)
    {
        _lastDiff = diff;
        Render(diff, resetScroll: true);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        QueueReflow();
    }

    private void QueueReflow()
    {
        if (_lastDiff is null || _reflowQueued || !IsHandleCreated) return;
        _reflowQueued = true;
        BeginInvoke((MethodInvoker)delegate
        {
            _reflowQueued = false;
            if (IsDisposed || _lastDiff is null) return;
            if (_wrapLongLines) Render(_lastDiff, resetScroll: false);
            else LayoutContentSurfaces();
        });
    }

    private void SetInitialSplitEqually()
    {
        if (_initialSplitApplied || IsDisposed || _split.ClientSize.Width <= _split.SplitterWidth) return;
        _split.SplitterDistance = (_split.ClientSize.Width - _split.SplitterWidth) / 2;
        _initialSplitApplied = true;
    }

    private void Render(DocumentDiff diff, bool resetScroll)
    {
        var preservedDiffRow = resetScroll
            ? 0
            : GetDiffRowForVisualLine(_verticalScroll.Value / Math.Max(1, _lineHeight));
        SuspendLayout();
        try
        {
            ClearRenderedText();
            _oldPane.Header.Text = $"변경 전  ·  {Path.GetFileName(diff.OldDocument.SourcePath)}";
            _newPane.Header.Text = $"변경 후  ·  {Path.GetFileName(diff.NewDocument.SourcePath)}";
            // A visual row is shared by the two documents.  Always use the narrower
            // pane's capacity, otherwise identical prose can wrap at different
            // places simply because its neighbour has a few more pixels.
            PrepareContentWidths(diff);
            var sharedColumns = Math.Min(
                GetWrapColumnCount(_oldPane),
                GetWrapColumnCount(_newPane));

            for (var rowIndex = 0; rowIndex < diff.Rows.Count; rowIndex++)
            {
                var row = diff.Rows[rowIndex];
                var oldLines = CreateVisualLines(row.OldFragments, sharedColumns);
                var newLines = CreateVisualLines(row.NewFragments, sharedColumns);
                var visualLineCount = Math.Max(1, Math.Max(oldLines.Count, newLines.Count));
                _diffRowToFirstVisualLine[rowIndex] = _visualToDiffRow.Count;

                for (var visualLine = 0; visualLine < visualLineCount; visualLine++)
                {
                    var oldFragments = visualLine < oldLines.Count ? oldLines[visualLine] : EmptyFragments;
                    var newFragments = visualLine < newLines.Count ? newLines[visualLine] : EmptyFragments;
                    WriteContent(
                        _oldPane.Content,
                        visualLine == 0 ? row.OldLine : null,
                        oldFragments,
                        row.Kind,
                        oldSide: true,
                        row.OldText is null);
                    WriteContent(
                        _newPane.Content,
                        visualLine == 0 ? row.NewLine : null,
                        newFragments,
                        row.Kind,
                        oldSide: false,
                        row.NewText is null);
                    _visualToDiffRow.Add(rowIndex);
                }
            }

            _oldPane.Overview.SetLines(diff.Rows.Select(row => new DiffOverviewLine(row.Kind, row.OldText?.Length ?? 0, row.OldText is null)).ToArray());
            _newPane.Overview.SetLines(diff.Rows.Select(row => new DiffOverviewLine(row.Kind, row.NewText?.Length ?? 0, row.NewText is null)).ToArray());
            LayoutContentSurfaces();
            if (resetScroll) ScrollToTop();
            else ScrollToDiffRow(preservedDiffRow);
        }
        finally
        {
            ResumeLayout();
        }
    }

    private IEnumerable<RichTextBox> AllTextBoxes => new[]
    {
        _oldPane.Content, _newPane.Content,
    };

    private IEnumerable<RichTextBox> ContentTextBoxes => new[] { _oldPane.Content, _newPane.Content };

    private static DiffPane CreatePane(string headerText, bool oldSide = true)
    {
        var header = new Label
        {
            AutoEllipsis = true,
            BackColor = Color.FromArgb(245, 247, 250),
            Dock = DockStyle.Top,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Height = 32,
            Padding = new Padding(10, 7, 10, 4),
            Text = headerText,
        };

        var content = CreateTextBox();
        var contentViewport = new Panel { BackColor = TextBackColor, Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty };
        contentViewport.Controls.Add(content);
        var overview = new DiffOverviewMap(oldSide);
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty, Padding = Padding.Empty };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
        body.Controls.Add(contentViewport, 0, 0);
        body.Controls.Add(overview, 1, 0);

        var root = new Panel { BackColor = TextBackColor, Dock = DockStyle.Fill };
        root.Controls.Add(body);
        root.Controls.Add(header);
        return new DiffPane(root, header, contentViewport, content, overview);
    }

    private static RichTextBox CreateTextBox() => new()
    {
        BackColor = TextBackColor,
        BorderStyle = BorderStyle.None,
        Cursor = Cursors.IBeam,
        DetectUrls = false,
        Dock = DockStyle.Fill,
        Font = new Font("Consolas", 10.25f),
        ForeColor = Color.FromArgb(35, 39, 47),
        HideSelection = false,
        ReadOnly = true,
        ScrollBars = RichTextBoxScrollBars.None,
        ShortcutsEnabled = true,
        TabStop = true,
        WordWrap = false,
    };

    private void ApplyWrappingVisuals()
    {
        _horizontalScroll.Visible = !_wrapLongLines;
    }

    private static void WriteContent(
        RichTextBox box,
        int? lineNumber,
        IReadOnlyList<InlineDiffFragment> fragments,
        DiffChangeKind rowKind,
        bool oldSide,
        bool imaginary)
    {
        var lineBackColor = GetLineBackColor(rowKind, oldSide);
        // The line number and its content must be rendered by the same RichTextBox.
        // Separate controls have independent internal margins and scroll ranges,
        // which made numbers drift from wrapped/imaginary comparison rows.
        var gutter = lineNumber?.ToString().PadLeft(LineNumberColumnWidth) ?? new string(' ', LineNumberColumnWidth);
        Append(box, gutter, Color.FromArgb(105, 112, 122), lineBackColor);
        Append(box, " │ ", Color.FromArgb(198, 203, 211), lineBackColor);
        if (fragments.Count == 0)
        {
            // Make the deliberate counterpart of an inserted/deleted row visible.
            Append(box, imaginary ? "·" : " ", Color.FromArgb(145, 150, 158), imaginary ? ImaginaryBackColor : lineBackColor);
        }
        else
        {
            foreach (var fragment in fragments)
            {
                var (foreColor, backColor) = GetFragmentColors(fragment.Kind, lineBackColor);
                Append(box, fragment.Text, foreColor, backColor);
            }
        }
        Append(box, Environment.NewLine, Color.FromArgb(35, 39, 47), lineBackColor);
    }

    private void PrepareContentWidths(DocumentDiff diff)
    {
        _oldUnwrappedContentWidth = MeasureUnwrappedContentWidth(diff.Rows, oldSide: true, _oldPane.Content.Font);
        _newUnwrappedContentWidth = MeasureUnwrappedContentWidth(diff.Rows, oldSide: false, _newPane.Content.Font);
    }

    private void LayoutContentSurfaces()
    {
        var oldViewport = _oldPane.ContentViewport.ClientSize;
        var newViewport = _newPane.ContentViewport.ClientSize;
        if (oldViewport.Width <= 0 || newViewport.Width <= 0) return;

        _lineHeight = Math.Max(1, _oldPane.Content.Font.Height);
        var visibleLineCount = Math.Max(1, _visualToDiffRow.Count);
        var requiredHeight = (visibleLineCount * _lineHeight) + 6;
        _contentHeight = Math.Max(requiredHeight, Math.Max(oldViewport.Height, newViewport.Height));

        var oldWidth = _wrapLongLines
            ? oldViewport.Width
            : Math.Max(oldViewport.Width, _oldUnwrappedContentWidth);
        var newWidth = _wrapLongLines
            ? newViewport.Width
            : Math.Max(newViewport.Width, _newUnwrappedContentWidth);
        _oldPane.Content.Size = new Size(oldWidth, _contentHeight);
        _newPane.Content.Size = new Size(newWidth, _contentHeight);

        ConfigureScrollBars(oldViewport, newViewport, oldWidth, newWidth);
        ApplyScrollOffsets();
    }

    private void ConfigureScrollBars(Size oldViewport, Size newViewport, int oldWidth, int newWidth)
    {
        var viewportHeight = Math.Max(1, Math.Min(oldViewport.Height, newViewport.Height));
        ConfigureScrollBar(_verticalScroll, _contentHeight - viewportHeight, viewportHeight, _lineHeight);

        _horizontalScroll.Visible = !_wrapLongLines;
        if (_wrapLongLines)
        {
            _horizontalScroll.Value = 0;
            return;
        }

        var oldHorizontalRange = Math.Max(0, oldWidth - oldViewport.Width);
        var newHorizontalRange = Math.Max(0, newWidth - newViewport.Width);
        ConfigureScrollBar(_horizontalScroll, Math.Max(oldHorizontalRange, newHorizontalRange), Math.Max(oldViewport.Width, newViewport.Width), 24);
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

    private void ApplyScrollOffsets()
    {
        var vertical = _verticalScroll.Value;
        var horizontal = _horizontalScroll.Visible ? _horizontalScroll.Value : 0;
        PositionContent(_oldPane, horizontal, vertical);
        PositionContent(_newPane, horizontal, vertical);
        UpdateOverviewViewports();
    }

    private static void PositionContent(DiffPane pane, int requestedHorizontal, int vertical)
    {
        var horizontal = Math.Clamp(requestedHorizontal, 0, Math.Max(0, pane.Content.Width - pane.ContentViewport.ClientSize.Width));
        pane.Content.Location = new Point(-horizontal, -vertical);
    }

    private void ContentMouseWheel(object? sender, MouseEventArgs e)
    {
        var scrollLines = SystemInformation.MouseWheelScrollLines;
        var lineCount = scrollLines == -1 ? Math.Max(1, _verticalScroll.LargeChange / Math.Max(1, _lineHeight)) : Math.Max(1, scrollLines);
        var steps = Math.Max(1, Math.Abs(e.Delta) / SystemInformation.MouseWheelScrollDelta);
        var direction = e.Delta > 0 ? -1 : 1;
        SetVerticalScrollValue(_verticalScroll.Value + (direction * steps * lineCount * _lineHeight));
    }

    private void SetVerticalScrollValue(int value)
    {
        var maximum = Math.Max(0, _verticalScroll.Maximum - _verticalScroll.LargeChange + 1);
        var clamped = Math.Clamp(value, 0, maximum);
        if (_verticalScroll.Value == clamped) ApplyScrollOffsets();
        else _verticalScroll.Value = clamped;
    }

    private int GetWrapColumnCount(DiffPane pane)
    {
        if (!_wrapLongLines) return int.MaxValue;
        var box = pane.Content;
        var availableWidth = pane.ContentViewport.ClientSize.Width - 12;
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        var columnWidth = Math.Max(
            TextRenderer.MeasureText("가", box.Font, Size.Empty, flags).Width,
            TextRenderer.MeasureText("W", box.Font, Size.Empty, flags).Width);
        var gutterWidth = TextRenderer.MeasureText(
            new string('0', LineNumberColumnWidth) + " │ ",
            box.Font,
            Size.Empty,
            flags).Width;
        availableWidth -= gutterWidth;
        if (availableWidth < 120) return 8;
        return Math.Max(8, availableWidth / Math.Max(1, columnWidth));
    }

    private static int MeasureUnwrappedContentWidth(IEnumerable<DiffRow> rows, bool oldSide, Font font)
    {
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        var maximum = 180;
        foreach (var row in rows)
        {
            var lineNumber = oldSide ? row.OldLine : row.NewLine;
            var text = oldSide ? row.OldText : row.NewText;
            var gutter = lineNumber?.ToString().PadLeft(LineNumberColumnWidth) ?? new string(' ', LineNumberColumnWidth);
            var display = gutter + " │ " + ToDisplayText(text ?? string.Empty);
            maximum = Math.Max(maximum, TextRenderer.MeasureText(display, font, new Size(100_000, 100), flags).Width + 6);
        }
        return maximum;
    }

    private static IReadOnlyList<IReadOnlyList<InlineDiffFragment>> CreateVisualLines(IReadOnlyList<InlineDiffFragment> fragments, int maxColumns)
    {
        if (fragments.Count == 0) return Array.Empty<IReadOnlyList<InlineDiffFragment>>();

        var lines = new List<IReadOnlyList<InlineDiffFragment>>();
        var current = new List<InlineDiffFragment>();
        var columns = 0;
        foreach (var fragment in fragments)
        {
            foreach (var character in ToDisplayText(fragment.Text))
            {
                var width = character == '\t' ? 4 : 1;
                if (columns > 0 && columns + width > maxColumns)
                {
                    lines.Add(current);
                    current = new List<InlineDiffFragment>();
                    columns = 0;
                }
                AppendFragmentCharacter(current, fragment.Kind, character);
                columns += width;
            }
        }
        if (current.Count > 0) lines.Add(current);
        return lines;
    }

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

    private static (Color ForeColor, Color BackColor) GetFragmentColors(InlineDiffFragmentKind kind, Color lineBackColor) => kind switch
    {
        InlineDiffFragmentKind.Removed => (Color.FromArgb(154, 31, 35), RemovedFragmentBackColor),
        InlineDiffFragmentKind.Added => (Color.FromArgb(0, 104, 50), AddedFragmentBackColor),
        _ => (Color.FromArgb(35, 39, 47), lineBackColor),
    };

    private static Color GetLineBackColor(DiffChangeKind kind, bool oldSide) => kind switch
    {
        DiffChangeKind.Inserted when oldSide => ImaginaryBackColor,
        DiffChangeKind.Deleted when !oldSide => ImaginaryBackColor,
        DiffChangeKind.Deleted when oldSide => RemovedLineBackColor,
        DiffChangeKind.Inserted when !oldSide => AddedLineBackColor,
        DiffChangeKind.Modified when oldSide => RemovedLineBackColor,
        DiffChangeKind.Modified => AddedLineBackColor,
        _ => TextBackColor,
    };

    private static string ToDisplayText(string text) => text
        .Replace("\r\n", "↵", StringComparison.Ordinal)
        .Replace("\n", "↵", StringComparison.Ordinal)
        .Replace("\r", "↵", StringComparison.Ordinal);

    private static void Append(RichTextBox box, string text, Color foreColor, Color backColor)
    {
        box.SelectionStart = box.TextLength;
        box.SelectionLength = 0;
        box.SelectionColor = foreColor;
        box.SelectionBackColor = backColor;
        box.AppendText(text);
    }

    private void ClearRenderedText()
    {
        foreach (var box in AllTextBoxes) box.Clear();
        _visualToDiffRow.Clear();
        _diffRowToFirstVisualLine.Clear();
        _oldPane.Overview.SetLines(Array.Empty<DiffOverviewLine>());
        _newPane.Overview.SetLines(Array.Empty<DiffOverviewLine>());
    }

    private void ScrollToDiffRow(int diffRow)
    {
        if (!_diffRowToFirstVisualLine.TryGetValue(diffRow, out var visualLine)) return;
        SetVerticalScrollValue(visualLine * _lineHeight);
    }

    private void UpdateOverviewViewports()
    {
        if (_visualToDiffRow.Count == 0) return;
        var firstVisualLine = Math.Clamp(_verticalScroll.Value / Math.Max(1, _lineHeight), 0, _visualToDiffRow.Count - 1);
        var viewportHeight = Math.Max(1, Math.Min(_oldPane.ContentViewport.ClientSize.Height, _newPane.ContentViewport.ClientSize.Height));
        var visibleVisualLines = Math.Max(1, (viewportHeight / Math.Max(1, _lineHeight)) + 1);
        var lastVisualLine = Math.Clamp(firstVisualLine + visibleVisualLines - 1, 0, _visualToDiffRow.Count - 1);
        var firstDiffRow = _visualToDiffRow[firstVisualLine];
        var lastDiffRow = _visualToDiffRow[lastVisualLine];
        var visibleDiffRows = Math.Max(1, lastDiffRow - firstDiffRow + 1);
        _oldPane.Overview.UpdateViewport(firstDiffRow, visibleDiffRows);
        _newPane.Overview.UpdateViewport(firstDiffRow, visibleDiffRows);
    }

    private int GetDiffRowForVisualLine(int visualLine)
    {
        if (_visualToDiffRow.Count == 0) return 0;
        return _visualToDiffRow[Math.Clamp(visualLine, 0, _visualToDiffRow.Count - 1)];
    }

    private void ScrollToTop()
    {
        SetVerticalScrollValue(0);
        _horizontalScroll.Value = 0;
    }

    private sealed record DiffPane(Panel Root, Label Header, Panel ContentViewport, RichTextBox Content, DiffOverviewMap Overview);
}
