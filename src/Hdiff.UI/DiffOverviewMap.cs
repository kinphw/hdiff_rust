using Hdiff.Core.Diff;

namespace Hdiff.UI;

internal sealed record DiffOverviewLine(DiffChangeKind Kind, int TextLength, bool IsImaginary);

/// <summary>
/// A compact, clickable document map. It deliberately draws line length rather than text so a
/// long document remains legible as a shape while changed rows retain their diff colours.
/// </summary>
internal sealed class DiffOverviewMap : Control
{
    private const int ChangeRailWidth = 6;
    private const int MinimumChangeSignalHeight = 2;
    private readonly bool _oldSide;
    private readonly ToolTip _toolTip = new();
    private IReadOnlyList<DiffOverviewLine> _lines = Array.Empty<DiffOverviewLine>();
    private int _firstVisibleLine;
    private int _visibleLineCount = 1;
    private bool _dragging;

    public DiffOverviewMap(bool oldSide)
    {
        _oldSide = oldSide;
        BackColor = Color.FromArgb(248, 249, 251);
        Cursor = Cursors.Hand;
        Dock = DockStyle.Fill;
        DoubleBuffered = true;
        Margin = Padding.Empty;
        MinimumSize = new Size(42, 0);
        Size = new Size(42, 100);
        TabStop = false;
        _toolTip.SetToolTip(this, "변경 개요 — 왼쪽의 진한 색 막대는 변경 위치입니다. 클릭하거나 끌어서 해당 위치로 이동합니다.");
    }

    public event EventHandler<int>? NavigateToLineRequested;

    public void SetLines(IReadOnlyList<DiffOverviewLine> lines)
    {
        _lines = lines;
        _firstVisibleLine = 0;
        _visibleLineCount = 1;
        Invalidate();
    }

    public void UpdateViewport(int firstVisibleLine, int visibleLineCount)
    {
        _firstVisibleLine = Math.Max(0, firstVisibleLine);
        _visibleLineCount = Math.Max(1, visibleLineCount);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(BackColor);
        if (_lines.Count == 0 || ClientSize.Height <= 0) return;

        var maxTextLength = Math.Max(1, _lines.Max(x => x.TextLength));
        for (var index = 0; index < _lines.Count; index++)
        {
            var line = _lines[index];
            if (line.IsImaginary) continue;

            var y = ScaleLine(index);
            var nextY = ScaleLine(index + 1);
            var height = Math.Max(1, nextY - y);
            var width = Math.Max(3, (int)Math.Ceiling((ClientSize.Width - ChangeRailWidth - 7) * (line.TextLength / (double)maxTextLength)));
            var x = ClientSize.Width - width - 3;
            using var brush = new SolidBrush(GetLineColor(line.Kind));
            e.Graphics.FillRectangle(brush, x, y, width, height);
        }

        var viewportTop = ScaleLine(Math.Min(_firstVisibleLine, _lines.Count - 1));
        var viewportBottom = ScaleLine(Math.Min(_lines.Count, _firstVisibleLine + _visibleLineCount));
        var viewportHeight = Math.Max(16, viewportBottom - viewportTop);
        viewportTop = Math.Min(Math.Max(0, viewportTop), Math.Max(0, ClientSize.Height - viewportHeight));
        using var viewportFill = new SolidBrush(Color.FromArgb(38, 55, 65, 81));
        using var viewportBorder = new Pen(Color.FromArgb(125, 89, 98, 112));
        e.Graphics.FillRectangle(viewportFill, 1, viewportTop, Math.Max(1, ClientSize.Width - 2), viewportHeight);
        e.Graphics.DrawRectangle(viewportBorder, 0, viewportTop, Math.Max(1, ClientSize.Width - 1), Math.Max(1, viewportHeight - 1));

        // A literal one-row minimap mark disappears in a multi-thousand-row
        // report. Draw a high-contrast, minimum-two-pixel rail after the
        // viewport overlay so an isolated change remains discoverable.
        DrawChangeSignalRail(e.Graphics);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        Capture = true;
        RequestNavigation(e.Y);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) RequestNavigation(e.Y);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        _dragging = false;
        Capture = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _toolTip.Dispose();
        base.Dispose(disposing);
    }

    private void RequestNavigation(int y)
    {
        if (_lines.Count == 0 || ClientSize.Height <= 0) return;
        var line = (int)Math.Floor(y * (_lines.Count / (double)ClientSize.Height));
        var topLine = Math.Clamp(line - (_visibleLineCount / 2), 0, Math.Max(0, _lines.Count - 1));
        NavigateToLineRequested?.Invoke(this, topLine);
    }

    private int ScaleLine(int line) => (int)Math.Floor(line * (ClientSize.Height / (double)_lines.Count));

    private void DrawChangeSignalRail(Graphics graphics)
    {
        for (var index = 0; index < _lines.Count; index++)
        {
            var line = _lines[index];
            if (line.IsImaginary || line.Kind == DiffChangeKind.Unchanged) continue;

            var y = ScaleLine(index);
            var nextY = ScaleLine(index + 1);
            var height = Math.Max(MinimumChangeSignalHeight, nextY - y);
            height = Math.Min(height, ClientSize.Height - y);
            if (height <= 0) continue;

            using var brush = new SolidBrush(GetSignalColor(line.Kind));
            graphics.FillRectangle(brush, 1, y, ChangeRailWidth, height);
        }
    }

    private Color GetLineColor(DiffChangeKind kind) => kind switch
    {
        DiffChangeKind.Deleted when _oldSide => Color.FromArgb(212, 202, 93, 100),
        DiffChangeKind.Inserted when !_oldSide => Color.FromArgb(212, 84, 155, 108),
        DiffChangeKind.Modified when _oldSide => Color.FromArgb(212, 202, 93, 100),
        DiffChangeKind.Modified => Color.FromArgb(212, 84, 155, 108),
        _ => Color.FromArgb(92, 118, 126, 140),
    };

    private Color GetSignalColor(DiffChangeKind kind) => kind switch
    {
        DiffChangeKind.Deleted when _oldSide => Color.FromArgb(220, 38, 38),
        DiffChangeKind.Inserted when !_oldSide => Color.FromArgb(22, 163, 74),
        DiffChangeKind.Modified when _oldSide => Color.FromArgb(234, 88, 12),
        DiffChangeKind.Modified => Color.FromArgb(5, 150, 105),
        _ => Color.FromArgb(71, 85, 105),
    };
}
