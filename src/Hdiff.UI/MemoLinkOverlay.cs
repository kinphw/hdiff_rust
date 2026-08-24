using System.Drawing.Drawing2D;

namespace Hdiff.UI;

/// <summary>
/// Paints the selected memo's leader line above the comparison and memo pane.
/// The window is click-through, so it never interferes with text selection or
/// controls below it.
/// </summary>
internal sealed class MemoLinkOverlay : Control
{
    private const int WsExTransparent = 0x20;
    private const int WmNcHitTest = 0x0084;
    private static readonly IntPtr HtTransparent = new(-1);

    private Point? _start;
    private Point? _end;
    private Color _accent = HdiffThemes.Light.MemoAccent;

    public MemoLinkOverlay()
    {
        SetStyle(ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.SupportsTransparentBackColor
            | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        TabStop = false;
        Visible = false;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExTransparent;
            return parameters;
        }
    }

    public void ApplyTheme(HdiffThemePalette theme)
    {
        _accent = theme.MemoAccent;
        Invalidate();
    }

    public void ShowLink(Point startScreen, Point endScreen)
    {
        _start = PointToClient(startScreen);
        _end = PointToClient(endScreen);
        Visible = true;
        BringToFront();
        Invalidate();
    }

    public void ClearLink()
    {
        _start = null;
        _end = null;
        Visible = false;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // WS_EX_TRANSPARENT lets already-painted sibling controls show through.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_start is not { } start || _end is not { } end) return;

        var distance = Math.Max(0, end.X - start.X);
        var bend = Math.Max(24, distance / 3);
        using var path = new GraphicsPath();
        path.AddBezier(
            start,
            new Point(start.X + bend, start.Y),
            new Point(end.X - bend, end.Y),
            end);

        using var pen = new Pen(_accent, 1.5f)
        {
            DashPattern = new[] { 5f, 3f },
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        var previous = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawPath(pen, path);
        e.Graphics.SmoothingMode = previous;
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmNcHitTest)
        {
            message.Result = HtTransparent;
            return;
        }

        base.WndProc(ref message);
    }
}
