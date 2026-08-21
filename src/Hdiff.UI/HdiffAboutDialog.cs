using System.Drawing.Drawing2D;

namespace Hdiff.UI;

internal sealed class HdiffAboutDialog : Form
{
    private readonly Bitmap? _logo;
    private readonly Icon? _windowIcon;

    public HdiffAboutDialog(string version, HdiffThemePalette theme)
    {
        Text = "HDiff 정보";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(480, 330);
        Font = new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.Dpi;

        (_windowIcon, _logo) = LoadBrandAssets();
        if (_windowIcon is not null) Icon = _windowIcon;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));

        root.Controls.Add(new AboutHeroPanel(_logo, version) { Dock = DockStyle.Fill }, 0, 0);
        root.Controls.Add(CreateInformationPanel(theme), 0, 1);
        root.Controls.Add(CreateFooter(theme), 0, 2);
        Controls.Add(root);

        BackColor = theme.SurfaceBack;
        ForeColor = theme.Text;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _logo?.Dispose();
            _windowIcon?.Dispose();
        }
        base.Dispose(disposing);
    }

    private static Control CreateInformationPanel(HdiffThemePalette theme)
    {
        var description = new Label
        {
            AutoSize = true,
            Text = "HWP · HWPX · Word · PDF · Excel 문서의 변경사항을 빠르고 정확하게 비교합니다.",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular),
            ForeColor = theme.Text,
            Margin = new Padding(0, 0, 0, 13),
        };
        var details = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = theme.SurfaceBack,
        };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        details.Controls.Add(CreateDetailLabel("제작자", theme.MutedText, bold: true), 0, 0);
        details.Controls.Add(CreateDetailLabel("kinphw", theme.Text), 1, 0);
        details.Controls.Add(CreateDetailLabel("GitHub", theme.MutedText, bold: true), 0, 1);
        details.Controls.Add(CreateDetailLabel("github.com/hdiff", theme.Text), 1, 1);

        var content = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(26, 20, 26, 14),
            BackColor = theme.SurfaceBack,
        };
        content.Controls.Add(description);
        content.Controls.Add(details);
        return content;
    }

    private Control CreateFooter(HdiffThemePalette theme)
    {
        var ok = new Button
        {
            Text = "확인",
            DialogResult = DialogResult.OK,
            AutoSize = false,
            Size = new Size(82, 30),
            Anchor = AnchorStyles.Right,
        };
        ok.FlatStyle = FlatStyle.Flat;
        ok.FlatAppearance.BorderColor = theme.PrimaryActionBack;
        ok.FlatAppearance.MouseOverBackColor = theme.PrimaryActionHover;
        ok.BackColor = theme.PrimaryActionBack;
        ok.ForeColor = theme.PrimaryActionText;
        AcceptButton = ok;
        CancelButton = ok;

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(24, 11, 24, 11),
            BackColor = theme.HeaderBack,
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(ok, 1, 0);
        return footer;
    }

    private static Label CreateDetailLabel(string text, Color color, bool bold = false) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = color,
        Font = new Font("Segoe UI", 9f, bold ? FontStyle.Bold : FontStyle.Regular),
        Margin = new Padding(0, 3, 8, 5),
    };

    private static (Icon? WindowIcon, Bitmap? Logo) LoadBrandAssets()
    {
        Icon? windowIcon = null;
        Bitmap? logo = null;
        try
        {
            using var windowStream = typeof(HdiffAboutDialog).Assembly
                .GetManifestResourceStream("Hdiff.ApplicationIcon");
            if (windowStream is not null)
                windowIcon = new Icon(windowStream);

            using var logoStream = typeof(HdiffAboutDialog).Assembly
                .GetManifestResourceStream("Hdiff.ApplicationIcon");
            if (logoStream is not null)
            {
                using var largeIcon = new Icon(logoStream, new Size(96, 96));
                logo = largeIcon.ToBitmap();
            }
        }
        catch
        {
            windowIcon?.Dispose();
            logo?.Dispose();
            return (null, null);
        }
        return (windowIcon, logo);
    }

    private sealed class AboutHeroPanel : Panel
    {
        private readonly Bitmap? _logo;
        private readonly string _version;

        public AboutHeroPanel(Bitmap? logo, string version)
        {
            _logo = logo;
            _version = version;
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

            using var background = new LinearGradientBrush(
                ClientRectangle,
                Color.FromArgb(10, 24, 43),
                Color.FromArgb(3, 105, 161),
                LinearGradientMode.Horizontal);
            e.Graphics.FillRectangle(background, ClientRectangle);

            if (_logo is not null)
                e.Graphics.DrawImage(_logo, new Rectangle(24, 27, 96, 96));

            using var titleFont = new Font("Segoe UI", 26f, FontStyle.Bold, GraphicsUnit.Point);
            using var subtitleFont = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point);
            using var versionFont = new Font("Segoe UI", 8.5f, FontStyle.Bold, GraphicsUnit.Point);
            using var titleBrush = new SolidBrush(Color.White);
            using var subtitleBrush = new SolidBrush(Color.FromArgb(210, 231, 245));
            e.Graphics.DrawString("HDiff", titleFont, titleBrush, 140, 26);
            e.Graphics.DrawString("HWP · HWPX · Word · PDF · Excel 변경 비교", subtitleFont, subtitleBrush, 143, 73);

            var badge = new RectangleF(142, 101, 84, 25);
            using var badgePath = CreateRoundedRectangle(badge, 12);
            using var badgeBrush = new SolidBrush(Color.FromArgb(55, 255, 255, 255));
            e.Graphics.FillPath(badgeBrush, badgePath);
            using var badgeTextBrush = new SolidBrush(Color.White);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            e.Graphics.DrawString($"v{_version}", versionFont, badgeTextBrush, badge, format);
        }

        private static GraphicsPath CreateRoundedRectangle(RectangleF bounds, float radius)
        {
            var diameter = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
