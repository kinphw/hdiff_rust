namespace Hdiff.UI;

/// <summary>
/// Writes one review memo. The compared paragraph is shown read-only above the
/// editor so the reviewer can see exactly what the note will be attached to.
/// </summary>
internal sealed class DiffMemoEditorDialog : Form
{
    private readonly TextBox _author = new() { Dock = DockStyle.Fill, MaxLength = 60 };
    private readonly TextBox _editor = new()
    {
        AcceptsReturn = true,
        Dock = DockStyle.Fill,
        Font = new Font("맑은 고딕", 10.5f),
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        WordWrap = true,
    };
    private readonly Label _quote = new()
    {
        AutoEllipsis = true,
        Dock = DockStyle.Fill,
        Padding = new Padding(9, 7, 9, 7),
    };

    public DiffMemoEditorDialog(
        string positionCaption,
        string quotedParagraph,
        string author,
        string memoText,
        bool editing,
        HdiffThemePalette theme)
    {
        Text = editing ? "검토 메모 편집" : "검토 메모 추가";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(520, 380);
        Size = new Size(600, 420);
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        Font = new Font("Segoe UI", 9f);
        Padding = new Padding(16);
        AutoScaleMode = AutoScaleMode.Dpi;

        var position = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Text = positionCaption,
            Padding = new Padding(0, 0, 0, 5),
        };
        _quote.Text = string.IsNullOrWhiteSpace(quotedParagraph) ? "(빈 문단)" : Flatten(quotedParagraph);
        _author.Text = author;
        _editor.Text = memoText;

        var authorRow = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        authorRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        authorRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var authorLabel = new Label { AutoSize = true, Text = "작성자", Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) };
        authorRow.Controls.Add(authorLabel, 0, 0);
        authorRow.Controls.Add(_author, 1, 0);

        var cancel = new Button { Text = "취소", AutoSize = true, DialogResult = DialogResult.Cancel };
        var save = new Button { Text = "저장", AutoSize = true, DialogResult = DialogResult.OK };
        var delete = new Button { Text = "삭제", AutoSize = true, Visible = editing, DialogResult = DialogResult.Abort };
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };
        buttons.Controls.Add(delete);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);

        var hint = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Text = "Ctrl+Enter로 저장",
            Margin = new Padding(0, 7, 0, 0),
        };
        var footer = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(0, 10, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(hint, 0, 0);
        footer.Controls.Add(buttons, 1, 0);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(position, 0, 0);
        root.Controls.Add(_quote, 0, 1);
        root.Controls.Add(authorRow, 0, 2);
        root.Controls.Add(_editor, 0, 3);
        root.Controls.Add(footer, 0, 4);
        Controls.Add(root);

        ApplyTheme(theme, root, position, authorRow, authorLabel, hint, footer, buttons, cancel, save, delete);
        AcceptButton = null;
        CancelButton = cancel;
        save.Click += (_, _) => DialogResult = DialogResult.OK;
        Shown += (_, _) =>
        {
            _editor.Focus();
            _editor.SelectionStart = _editor.TextLength;
        };
        FormClosing += (_, e) =>
        {
            if (DialogResult != DialogResult.OK) return;
            if (!string.IsNullOrWhiteSpace(_editor.Text)) return;
            // An empty memo would show as an anonymous flag on the row.
            MessageBox.Show(this, "메모 내용을 입력해 주세요.", "Hdiff", MessageBoxButtons.OK, MessageBoxIcon.Information);
            e.Cancel = true;
            _editor.Focus();
        };
    }

    public string MemoText => _editor.Text.Trim();

    public string Author
    {
        get
        {
            var author = _author.Text.Trim();
            return author.Length == 0 ? "검토자" : author;
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Enter))
        {
            DialogResult = DialogResult.OK;
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private static string Flatten(string text) => text
        .Replace("\r\n", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal);

    private void ApplyTheme(
        HdiffThemePalette theme,
        Control root,
        Label position,
        Control authorRow,
        Label authorLabel,
        Label hint,
        Control footer,
        Control buttons,
        Button cancel,
        Button save,
        Button delete)
    {
        BackColor = theme.AppBack;
        ForeColor = theme.Text;
        foreach (var container in new[] { root, authorRow, footer, buttons })
        {
            container.BackColor = theme.AppBack;
            container.ForeColor = theme.Text;
        }
        position.ForeColor = theme.MutedText;
        authorLabel.ForeColor = theme.Text;
        hint.ForeColor = theme.MutedText;
        _quote.BackColor = theme.MemoSurfaceBack;
        _quote.ForeColor = theme.Text;
        _quote.BorderStyle = BorderStyle.FixedSingle;
        foreach (var input in new[] { _author, _editor })
        {
            input.BackColor = theme.CanvasBack;
            input.ForeColor = theme.Text;
            input.BorderStyle = BorderStyle.FixedSingle;
        }
        foreach (var button in new[] { cancel, save, delete })
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = theme.ButtonBorder;
            button.FlatAppearance.MouseOverBackColor = theme.HeaderBack;
            button.BackColor = theme.ButtonBack;
            button.ForeColor = theme.ButtonText;
            button.Padding = new Padding(8, 1, 8, 1);
        }
        save.FlatAppearance.BorderColor = theme.PrimaryActionBack;
        save.FlatAppearance.MouseOverBackColor = theme.PrimaryActionHover;
        save.BackColor = theme.PrimaryActionBack;
        save.ForeColor = theme.PrimaryActionText;
        delete.ForeColor = theme.RemovedText;
    }
}
