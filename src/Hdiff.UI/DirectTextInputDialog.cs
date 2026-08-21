namespace Hdiff.UI;

internal sealed class DirectTextInputDialog : Form
{
    private readonly TextBox _editor = new()
    {
        AcceptsReturn = true,
        AcceptsTab = true,
        Dock = DockStyle.Fill,
        Font = new Font("맑은 고딕", 10.5f),
        Multiline = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = true,
    };
    private readonly Label _count = new() { AutoSize = true, Anchor = AnchorStyles.Left };

    public DirectTextInputDialog(string sideCaption, string initialText, HdiffThemePalette theme)
    {
        Text = $"직접 입력 — {sideCaption}";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(620, 460);
        Size = new Size(760, 600);
        ShowInTaskbar = false;
        Font = new Font("Segoe UI", 9f);
        Padding = new Padding(16);
        AutoScaleMode = AutoScaleMode.Dpi;

        var description = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Text = "AI 응답이나 Markdown·일반 텍스트를 붙여 넣으세요. 입력한 줄 단위로 원문과 비교합니다.",
            Padding = new Padding(0, 0, 0, 8),
        };
        var cancel = new Button { Text = "취소", AutoSize = true, DialogResult = DialogResult.Cancel };
        var apply = new Button { Text = "적용", AutoSize = true, DialogResult = DialogResult.OK };
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(apply);

        var footer = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(0, 10, 0, 0),
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(_count, 0, 0);
        footer.Controls.Add(buttons, 1, 0);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(description, 0, 0);
        root.Controls.Add(_editor, 0, 1);
        root.Controls.Add(footer, 0, 2);
        Controls.Add(root);

        _editor.Text = initialText;
        _editor.TextChanged += (_, _) => UpdateCount();
        ApplyTheme(theme, description, footer, buttons, cancel, apply);
        UpdateCount();
        AcceptButton = apply;
        CancelButton = cancel;
        Shown += (_, _) =>
        {
            _editor.Focus();
            _editor.SelectionStart = _editor.TextLength;
        };
    }

    public string InputText => _editor.Text;

    private void UpdateCount()
    {
        var lineCount = _editor.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Count(character => character == '\n') + 1;
        _count.Text = $"{_editor.TextLength:N0}자 · {lineCount:N0}줄";
    }

    private void ApplyTheme(
        HdiffThemePalette theme,
        Label description,
        TableLayoutPanel footer,
        FlowLayoutPanel buttons,
        params Button[] actionButtons)
    {
        BackColor = theme.AppBack;
        ForeColor = theme.Text;
        description.BackColor = theme.AppBack;
        description.ForeColor = theme.Text;
        footer.BackColor = theme.AppBack;
        buttons.BackColor = theme.AppBack;
        _count.BackColor = theme.AppBack;
        _count.ForeColor = theme.MutedText;
        _editor.BackColor = theme.CanvasBack;
        _editor.ForeColor = theme.Text;
        _editor.BorderStyle = BorderStyle.FixedSingle;
        foreach (var button in actionButtons)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = theme.ButtonBorder;
            button.FlatAppearance.MouseOverBackColor = theme.HeaderBack;
            button.BackColor = theme.ButtonBack;
            button.ForeColor = theme.ButtonText;
            button.Padding = new Padding(8, 1, 8, 1);
        }
    }
}
