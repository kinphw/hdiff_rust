namespace Hdiff.UI;

internal sealed class HdiffSettingsDialog : Form
{
    private readonly ComboBox _themePicker = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210 };
    private readonly ComboBox _fontSizePicker = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210 };
    private readonly CheckBox _wrapLongLines = new() { Text = "Word Wrap", AutoSize = true };
    private readonly CheckBox _rowSeparators = new() { Text = "Row Separators", AutoSize = true };
    private readonly CheckBox _textSelection = new() { Text = "Text Selection", AutoSize = true };
    private readonly CheckBox _ignoreWhitespace = new() { Text = "Ignore Whitespace Changes", AutoSize = true };
    private readonly CheckBox _ignoreBlankLines = new() { Text = "Ignore Blank Lines", AutoSize = true };
    private readonly CheckBox _includeMemos = new() { Text = "Include Memos", AutoSize = true };
    private readonly CheckBox _reflowPdf = new() { Text = "Reflow PDF Paragraphs", AutoSize = true };

    public HdiffSettingsDialog(
        string[] themeLabels,
        int selectedThemeIndex,
        string[] fontSizeLabels,
        int selectedFontSizeIndex,
        bool wrapLongLines,
        bool showRowSeparators,
        bool textSelectionEnabled,
        bool ignoreWhitespaceChanges,
        bool ignoreBlankLines,
        bool includeMemos,
        bool reflowPdfParagraphs,
        HdiffThemePalette theme)
    {
        Text = "Hdiff 설정";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(440, 540);
        MinimumSize = new Size(480, 590);
        Padding = new Padding(16);
        Font = new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.Dpi;

        _themePicker.Items.AddRange(themeLabels);
        _themePicker.SelectedIndex = Math.Clamp(selectedThemeIndex, 0, themeLabels.Length - 1);
        _fontSizePicker.Items.AddRange(fontSizeLabels);
        _fontSizePicker.SelectedIndex = Math.Clamp(selectedFontSizeIndex, 0, fontSizeLabels.Length - 1);
        _wrapLongLines.Checked = wrapLongLines;
        _rowSeparators.Checked = showRowSeparators;
        _textSelection.Checked = textSelectionEnabled;
        _ignoreWhitespace.Checked = ignoreWhitespaceChanges;
        _ignoreBlankLines.Checked = ignoreBlankLines;
        _includeMemos.Checked = includeMemos;
        _reflowPdf.Checked = reflowPdfParagraphs;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(CreateAppearanceGroup(), 0, 0);
        root.Controls.Add(CreateCheckGroup("View", _wrapLongLines, _rowSeparators, _textSelection), 0, 1);
        root.Controls.Add(CreateCheckGroup("Comparison", _ignoreWhitespace, _ignoreBlankLines), 0, 2);
        root.Controls.Add(CreateCheckGroup("Document Content", _includeMemos, _reflowPdf), 0, 3);
        root.Controls.Add(CreateButtonRow(theme), 0, 5);
        Controls.Add(root);

        ApplyTheme(theme);
    }

    public int SelectedThemeIndex => _themePicker.SelectedIndex;
    public int SelectedFontSizeIndex => _fontSizePicker.SelectedIndex;
    public bool WrapLongLines => _wrapLongLines.Checked;
    public bool ShowRowSeparators => _rowSeparators.Checked;
    public bool TextSelectionEnabled => _textSelection.Checked;
    public bool IgnoreWhitespaceChanges => _ignoreWhitespace.Checked;
    public bool IgnoreBlankLines => _ignoreBlankLines.Checked;
    public bool IncludeMemos => _includeMemos.Checked;

    public bool ReflowPdfParagraphs => _reflowPdf.Checked;

    private GroupBox CreateAppearanceGroup()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(8, 6, 8, 8),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        layout.Controls.Add(CreateLabel("화면 테마"), 0, 0);
        layout.Controls.Add(_themePicker, 1, 0);
        layout.Controls.Add(CreateLabel("글자 크기"), 0, 1);
        layout.Controls.Add(_fontSizePicker, 1, 1);

        var group = CreateGroup("Appearance");
        group.Controls.Add(layout);
        return group;
    }

    private static GroupBox CreateCheckGroup(string title, params CheckBox[] options)
    {
        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(10, 6, 8, 8),
            WrapContents = false,
        };
        foreach (var option in options)
        {
            option.Margin = new Padding(0, 3, 0, 3);
            layout.Controls.Add(option);
        }

        var group = CreateGroup(title);
        group.Controls.Add(layout);
        return group;
    }

    private TableLayoutPanel CreateButtonRow(HdiffThemePalette theme)
    {
        var defaults = new Button { Text = "기본값 복원", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Bottom };
        var ok = new Button { Text = "확인", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "취소", AutoSize = true, DialogResult = DialogResult.Cancel };
        defaults.Click += (_, _) =>
        {
            _themePicker.SelectedIndex = 0;
            _fontSizePicker.SelectedIndex = Math.Min(1, _fontSizePicker.Items.Count - 1);
            _wrapLongLines.Checked = true;
            _rowSeparators.Checked = false;
            _textSelection.Checked = true;
            _ignoreWhitespace.Checked = true;
            _ignoreBlankLines.Checked = true;
            _includeMemos.Checked = false;
            _reflowPdf.Checked = true;
        };

        ApplyButtonTheme(defaults, theme);
        ApplyButtonTheme(ok, theme);
        ApplyButtonTheme(cancel, theme);
        AcceptButton = ok;
        CancelButton = cancel;

        var right = new FlowLayoutPanel
        {
            AutoSize = true,
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };
        right.Controls.Add(cancel);
        right.Controls.Add(ok);

        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 12, 0, 0),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(defaults, 0, 0);
        row.Controls.Add(right, 1, 0);
        return row;
    }

    private void ApplyTheme(HdiffThemePalette theme)
    {
        BackColor = theme.AppBack;
        ForeColor = theme.Text;
        foreach (var control in Descendants(this))
        {
            control.ForeColor = theme.Text;
            if (control is ComboBox picker)
            {
                picker.FlatStyle = FlatStyle.Flat;
                picker.BackColor = theme.SurfaceBack;
            }
            else if (control is GroupBox)
            {
                control.BackColor = theme.AppBack;
            }
            else if (control is TableLayoutPanel or FlowLayoutPanel or Label or CheckBox)
            {
                control.BackColor = theme.AppBack;
            }
        }
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

    private static GroupBox CreateGroup(string title) => new()
    {
        Text = title,
        Dock = DockStyle.Top,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Margin = new Padding(0, 0, 0, 10),
        Padding = new Padding(8),
    };

    private static Label CreateLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 7, 8, 7),
    };

    private static void ApplyButtonTheme(Button button, HdiffThemePalette theme)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = theme.ButtonBorder;
        button.FlatAppearance.MouseOverBackColor = theme.HeaderBack;
        button.BackColor = theme.ButtonBack;
        button.ForeColor = theme.ButtonText;
        button.Padding = new Padding(8, 1, 8, 1);
    }
}
