using System.Drawing.Drawing2D;

namespace Hdiff.UI;

/// <summary>
/// Paints the selected memo's leader line above the comparison and memo pane.
/// The window is click-through, so it never interferes with text selection or
/// controls below it.
/// </summary>
internal sealed class MemoLinkOverlay : Form
{
    private const int WsExTransparent = 0x20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WmNcHitTest = 0x0084;
    private static readonly IntPtr HtTransparent = new(-1);
    private static readonly Color TransparencyColor = Color.Fuchsia;

    private Point? _start;
    private Point? _end;
    private Color _accent = HdiffThemes.Light.MemoAccent;

    public MemoLinkOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = TransparencyColor;
        TransparencyKey = TransparencyColor;
        DoubleBuffered = true;
        TabStop = false;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExTransparent | WsExNoActivate | WsExToolWindow;
            return parameters;
        }
    }

    public void ApplyTheme(HdiffThemePalette theme)
    {
        _accent = theme.MemoAccent;
        Invalidate();
    }

    public void ShowLink(Rectangle boundsScreen, Point startScreen, Point endScreen, IWin32Window owner)
    {
        Bounds = boundsScreen;
        _start = PointToClient(startScreen);
        _end = PointToClient(endScreen);
        if (!Visible) Show(owner);
        Invalidate();
    }

    public void ClearLink()
    {
        _start = null;
        _end = null;
        if (Visible) Hide();
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
