using Hdiff.Core.Diff;
using Hdiff.Core.Review;

namespace Hdiff.UI;

internal sealed record DiffMemoRow(string Id, int Number, DiffChangeKind Kind, DiffMemoSide Side, int RowIndex,
    string Position, string? Quote, string Text, string Author, string When, bool Orphaned,
    IReadOnlyList<DiffMemoReply> Replies);
internal sealed record DiffMemoDraftTarget(
    int RowIndex,
    DiffChangeKind Kind,
    DiffMemoSide Side,
    string Position,
    string? Quote,
    string? SelectedText = null,
    int SelectionStart = -1);
internal sealed record DiffMemoSubmission(string? MemoId, DiffMemoDraftTarget Target, string Author, string Text);
internal sealed record DiffMemoReplySubmission(string MemoId, string Author, string Text);
internal sealed record DiffMemoReplyKey(string MemoId, string ReplyId);

/// <summary>HTML review UX mirrored as a native right-side card and thread pane.</summary>
internal sealed class DiffMemoListPanel : Panel
{
    private const int PanelWidth = 320;
    private readonly Label _title = new() { AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Margin = new Padding(0, 3, 0, 0) };
    private readonly Label _count = new() { AutoSize = true, Font = new Font("Segoe UI", 8f, FontStyle.Bold), Padding = new Padding(5, 1, 5, 1), Margin = new Padding(6, 2, 0, 0) };
    private readonly Label _dirty = new() { AutoSize = true, Text = "저장 안 됨", Font = new Font("Segoe UI", 8f, FontStyle.Bold), Padding = new Padding(5, 1, 5, 1), Margin = new Padding(6, 2, 0, 0), Visible = false };
    private readonly Button _close = new() { Text = "×", AutoSize = false, Size = new Size(24, 23), Margin = new Padding(3, 0, 0, 0) };
    private readonly TextBox _author = new() { Dock = DockStyle.Top, MaxLength = 60, PlaceholderText = "내 이름 (메모·회신 작성자)" };
    private readonly Label _hint = new() { AutoSize = true, Dock = DockStyle.Top, MaximumSize = new Size(285, 0), Padding = new Padding(0, 6, 0, 0), Text = "비교 행의 +로 메모를 추가합니다. Ctrl+Enter로 등록할 수 있습니다." };
    private readonly Panel _header = new() { Dock = DockStyle.Top, Height = 104, Padding = new Padding(12, 7, 8, 7) };
    private readonly FlowLayoutPanel _list = new() { AutoScroll = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(8), WrapContents = false };
    private IReadOnlyList<DiffMemoRow> _memos = Array.Empty<DiffMemoRow>();
    private HdiffThemePalette _theme = HdiffThemes.Light;
    private string? _selectedId;
    private Control? _editor;

    public DiffMemoListPanel()
    {
        Dock = DockStyle.Right;
        Width = PanelWidth;
        MinimumSize = new Size(280, 0);
        Visible = false;
        var titleRow = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 4 };
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        titleRow.Controls.Add(_title, 0, 0); titleRow.Controls.Add(_count, 1, 0);
        titleRow.Controls.Add(_dirty, 2, 0); titleRow.Controls.Add(_close, 3, 0);
        _header.Controls.Add(_hint); _header.Controls.Add(_author); _header.Controls.Add(titleRow);
        _close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        _list.Resize += (_, _) => { ResizeChildren(); SelectionGeometryChanged?.Invoke(this, EventArgs.Empty); };
        _list.Scroll += (_, _) => SelectionGeometryChanged?.Invoke(this, EventArgs.Empty);
        _list.Layout += (_, _) => SelectionGeometryChanged?.Invoke(this, EventArgs.Empty);
        Controls.Add(_list); Controls.Add(_header);
        ApplyTheme(_theme);
    }

    public event EventHandler<string>? MemoActivated;
    public event EventHandler<DiffMemoSubmission>? MemoSubmitted;
    public event EventHandler<string>? DeleteRequested;
    public event EventHandler<DiffMemoReplySubmission>? ReplySubmitted;
    public event EventHandler<DiffMemoReplyKey>? ReplyDeleteRequested;
    public event EventHandler? CloseRequested;
    public event EventHandler? SelectionGeometryChanged;
    public string Author { get => string.IsNullOrWhiteSpace(_author.Text) ? "검토자" : _author.Text.Trim(); set => _author.Text = value; }

    public void SetMemos(IReadOnlyList<DiffMemoRow> memos)
    {
        _memos = memos;
        if (_selectedId is not null && memos.All(m => m.Id != _selectedId)) _selectedId = null;
        Rebuild();
    }

    public void SetDirty(bool dirty) => _dirty.Visible = dirty;

    public void BeginAdd(DiffMemoDraftTarget target)
    {
        CancelEditor(); _selectedId = null;
        ShowMemoEditor(null, target, string.Empty, 0);
    }

    public void BeginEdit(string id)
    {
        var memo = _memos.FirstOrDefault(m => m.Id == id);
        if (memo is null) return;
        CancelEditor(); _selectedId = id;
        ShowMemoEditor(id, new DiffMemoDraftTarget(memo.RowIndex, memo.Kind, memo.Side, memo.Position, memo.Quote),
            memo.Text, Math.Max(0, IndexOf(id) + 1));
    }

    public void SelectMemo(string id)
    {
        _selectedId = id; Control? selected = null;
        foreach (Control control in _list.Controls)
            if (control.Tag is string memoId)
            {
                control.Invalidate();
                if (memoId == id) selected = control;
            }
        if (selected is not null) _list.ScrollControlIntoView(selected);
        SelectionGeometryChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool TryGetSelectedCardAnchorScreen(out Point anchor)
    {
        anchor = default;
        if (!Visible || _selectedId is null) return false;
        var card = _list.Controls.Cast<Control>()
            .FirstOrDefault(control => control.Tag is string id && id == _selectedId);
        if (card is null) return false;

        var cardBounds = card.RectangleToScreen(card.ClientRectangle);
        var listBounds = _list.RectangleToScreen(_list.ClientRectangle);
        var visibleBounds = Rectangle.Intersect(cardBounds, listBounds);
        if (visibleBounds.IsEmpty) return false;

        anchor = new Point(cardBounds.Left,
            Math.Clamp(cardBounds.Top + Math.Min(18, cardBounds.Height / 2), visibleBounds.Top + 1, visibleBounds.Bottom - 1));
        return true;
    }

    public void ApplyTheme(HdiffThemePalette theme)
    {
        _theme = theme; BackColor = theme.SurfaceBack; _header.BackColor = theme.HeaderBack; _list.BackColor = theme.SurfaceBack;
        _title.ForeColor = theme.Text; _count.BackColor = theme.MemoSurfaceBack; _count.ForeColor = theme.MemoAccent;
        _dirty.ForeColor = theme.RemovedText; _hint.ForeColor = theme.MutedText;
        _author.BackColor = theme.CanvasBack; _author.ForeColor = theme.Text; _author.BorderStyle = BorderStyle.FixedSingle;
        StyleButton(_close, false); Rebuild();
    }

    private void Rebuild()
    {
        CancelEditor(); _list.SuspendLayout(); _list.Controls.Clear();
        foreach (var memo in _memos) _list.Controls.Add(CreateCard(memo));
        if (_memos.Count == 0) _list.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(270, 0),
            Padding = new Padding(10),
            BackColor = _theme.SurfaceBack,
            ForeColor = _theme.MutedText,
            Text = "아직 검토 메모가 없습니다. 비교 행 위의 +를 눌러 메모를 추가하세요."
        });
        _title.Text = "검토 메모"; _count.Text = _memos.Count.ToString(); ResizeChildren(); _list.ResumeLayout();
    }

    private Control CreateCard(DiffMemoRow memo)
    {
        var card = MemoCardTable(_theme.MemoSurfaceBack, new Padding(10, 8, 8, 8));
        card.Width = CardWidth(); card.Margin = new Padding(0, 0, 0, 8); card.Tag = memo.Id;
        card.Cursor = memo.Orphaned ? Cursors.Default : Cursors.Hand;
        card.TabStop = !memo.Orphaned;
        card.AccessibleRole = AccessibleRole.ListItem;
        card.AccessibleName = $"검토 메모 {memo.Number} · {memo.Author} · {Shorten(Flatten(memo.Text), 60)}";
        card.Paint += (_, e) => PaintCard(e.Graphics, card.ClientRectangle, memo, card.Focused);
        card.GotFocus += (_, _) => card.Invalidate();
        card.LostFocus += (_, _) => card.Invalidate();
        var head = new FlowLayoutPanel { AutoSize = true, BackColor = _theme.MemoSurfaceBack, Dock = DockStyle.Fill, WrapContents = true };
        head.Controls.Add(Chip(memo.Number.ToString(), _theme.MemoAccent, _theme.MemoFlagText));
        head.Controls.Add(Chip(memo.Side == DiffMemoSide.Old ? "전" : "후",
            memo.Side == DiffMemoSide.Old ? _theme.DeletedLineBack : _theme.InsertedLineBack,
            memo.Side == DiffMemoSide.Old ? _theme.RemovedText : _theme.AddedText));
        head.Controls.Add(Chip(KindLabel(memo.Kind), KindBack(memo.Kind), KindFore(memo.Kind)));
        head.Controls.Add(new Label
        {
            AutoSize = true,
            BackColor = _theme.MemoSurfaceBack,
            ForeColor = _theme.MutedText,
            Font = new Font("Segoe UI", 7.5f),
            Margin = new Padding(4, 3, 0, 0),
            Text = memo.Orphaned ? "위치 없음" : memo.Position
        });
        Add(card, head);
        // Only a highlighted phrase is quoted; without one the memo text starts
        // right away instead of being pushed down by the whole paragraph.
        if (memo.Quote is { } quoted)
        {
            var quote = Wrap(Shorten(Flatten(quoted), 160), _theme.MemoSurfaceBack, _theme.MutedText, 8.5f, 260);
            quote.Margin = new Padding(0, 7, 0, 0);
            quote.Padding = new Padding(7, 3, 0, 3);
            quote.Paint += (_, e) =>
            {
                using var accent = new Pen(_theme.MemoAccent, 2);
                e.Graphics.DrawLine(accent, 1, 1, 1, quote.Height - 2);
            };
            Add(card, quote);
        }
        var body = Wrap(memo.Text, _theme.MemoSurfaceBack, _theme.Text, 9f, 260); body.Margin = new Padding(0, 7, 0, 0); Add(card, body);
        var foot = new TableLayoutPanel { AutoSize = true, BackColor = _theme.MemoSurfaceBack, ColumnCount = 2, Dock = DockStyle.Fill, Margin = new Padding(0, 7, 0, 0) };
        foot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); foot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        foot.Controls.Add(Meta(memo.Author, ContentAlignment.MiddleLeft, HdiffThemes.MemoAuthorColor(_theme, memo.Author)), 0, 0);
        foot.Controls.Add(Meta(memo.When, ContentAlignment.MiddleRight), 1, 0); Add(card, foot);
        foreach (var reply in memo.Replies) Add(card, CreateReply(memo.Id, reply));
        var tools = new FlowLayoutPanel { AutoSize = true, BackColor = _theme.MemoSurfaceBack, Margin = new Padding(0, 8, 0, 0), WrapContents = false };
        var replyButton = ActionButton("회신"); var edit = ActionButton("편집"); var delete = ActionButton("삭제");
        replyButton.Click += (_, _) => BeginReply(memo.Id); edit.Click += (_, _) => BeginEdit(memo.Id);
        delete.Click += (_, _) => DeleteRequested?.Invoke(this, memo.Id);
        tools.Controls.AddRange(new Control[] { replyButton, edit, delete }); Add(card, tools);
        if (!memo.Orphaned)
        {
            AttachActivation(card, memo.Id);
            card.KeyDown += (_, e) =>
            {
                if (e.KeyCode is not (Keys.Enter or Keys.Space)) return;
                e.SuppressKeyPress = true;
                SelectMemo(memo.Id);
                MemoActivated?.Invoke(this, memo.Id);
            };
        }
        return card;
    }

    private Control CreateReply(string memoId, DiffMemoReply reply)
    {
        var color = HdiffThemes.MemoAuthorColor(_theme, reply.Author);
        var panel = Table(_theme.SurfaceBack, new Padding(9, 6, 6, 6)); panel.Dock = DockStyle.Fill; panel.Margin = new Padding(0, 8, 0, 0);
        panel.Paint += (_, e) => { using var b = new SolidBrush(color); e.Graphics.FillRectangle(b, 0, 0, 3, panel.Height); };
        var head = new TableLayoutPanel { AutoSize = true, BackColor = _theme.SurfaceBack, ColumnCount = 3, Dock = DockStyle.Fill };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        head.Controls.Add(new Label { AutoSize = true, BackColor = _theme.SurfaceBack, ForeColor = color, Font = new Font("Segoe UI", 7.5f, FontStyle.Bold), Text = reply.Author }, 0, 0);
        head.Controls.Add(new Label { AutoSize = true, BackColor = _theme.SurfaceBack, ForeColor = _theme.MutedText, Font = new Font("Segoe UI", 7.5f), Text = reply.CreatedAt.ToString("yyyy-MM-dd HH:mm") }, 1, 0);
        var remove = new Button { Text = "×", AutoSize = false, Size = new Size(18, 18), Margin = Padding.Empty }; StyleButton(remove, false);
        remove.Click += (_, _) => ReplyDeleteRequested?.Invoke(this, new DiffMemoReplyKey(memoId, reply.Id)); head.Controls.Add(remove, 2, 0); Add(panel, head);
        var text = Wrap(reply.Text, _theme.SurfaceBack, color, 8.5f, 245); text.Margin = new Padding(0, 4, 0, 0); Add(panel, text);
        return panel;
    }

    private void ShowMemoEditor(string? memoId, DiffMemoDraftTarget target, string text, int index)
    {
        var shell = EditorShell(memoId is null ? "새 검토 메모" : "검토 메모 편집", target.Quote);
        var area = TextArea(text, "메모 내용을 입력하세요."); Add(shell, area);
        void Commit()
        {
            var body = area.Text.Trim();
            if (body.Length == 0) return;
            var submission = new DiffMemoSubmission(memoId, target, Author, body);
            CancelEditor();
            MemoSubmitted?.Invoke(this, submission);
        }
        Add(shell, EditorButtons(Commit)); WireKeys(area, Commit); InsertEditor(shell, index); area.Focus(); area.SelectionStart = area.TextLength;
    }

    private void BeginReply(string memoId)
    {
        CancelEditor(); var memo = _memos.FirstOrDefault(m => m.Id == memoId); if (memo is null) return; _selectedId = memoId;
        var shell = EditorShell($"메모 {memo.Number}에 회신", memo.Text); var area = TextArea(string.Empty, "회신 내용을 입력하세요."); Add(shell, area);
        void Commit()
        {
            var body = area.Text.Trim();
            if (body.Length == 0) return;
            var submission = new DiffMemoReplySubmission(memoId, Author, body);
            CancelEditor();
            ReplySubmitted?.Invoke(this, submission);
        }
        Add(shell, EditorButtons(Commit)); WireKeys(area, Commit); InsertEditor(shell, Math.Max(0, IndexOf(memoId) + 1)); area.Focus();
    }

    private TableLayoutPanel EditorShell(string title, string? quote)
    {
        var shell = Table(_theme.SurfaceBack, new Padding(9));
        shell.Width = CardWidth();
        shell.Margin = new Padding(0, 0, 0, 8);
        shell.Paint += (_, e) =>
        {
            using var border = new Pen(_theme.PrimaryActionBack);
            e.Graphics.DrawRectangle(border, 0, 0, shell.Width - 1, shell.Height - 1);
        };
        Add(shell, new Label
        {
            AutoSize = true,
            BackColor = _theme.SurfaceBack,
            ForeColor = _theme.Text,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            Text = title,
        });
        if (quote is { } quoted)
        {
            var label = Wrap(Shorten(Flatten(quoted), 160), _theme.SurfaceBack, _theme.MutedText, 8f, 255);
            label.Margin = new Padding(0, 5, 0, 5);
            Add(shell, label);
        }
        return shell;
    }

    private TextBox TextArea(string text, string placeholder) => new()
    {
        AcceptsReturn = true,
        BackColor = _theme.CanvasBack,
        ForeColor = _theme.Text,
        Font = new Font("맑은 고딕", 9f),
        Height = 82,
        Multiline = true,
        PlaceholderText = placeholder,
        ScrollBars = ScrollBars.Vertical,
        Text = text,
        Width = Math.Max(220, CardWidth() - 20),
        WordWrap = true
    };
    private void WireKeys(TextBox area, Action commit) => area.KeyDown += (_, e) =>
    {
        if (e.Control && e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            commit();
        }
        else if (e.KeyCode == Keys.Escape)
        {
            e.SuppressKeyPress = true;
            CancelEditor();
        }
    };
    private Control EditorButtons(Action commit)
    {
        var row = new FlowLayoutPanel { AutoSize = true, BackColor = _theme.SurfaceBack, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 7, 0, 0) };
        var save = ActionButton("등록", true); var cancel = ActionButton("취소"); save.Click += (_, _) => commit(); cancel.Click += (_, _) => CancelEditor(); row.Controls.Add(save); row.Controls.Add(cancel); return row;
    }
    private void InsertEditor(Control editor, int index)
    {
        _editor = editor;
        _list.Controls.Add(editor);
        _list.Controls.SetChildIndex(editor, Math.Clamp(index, 0, _list.Controls.Count - 1));
        _list.ScrollControlIntoView(editor);
    }
    private void CancelEditor()
    {
        if (_editor is null) return;
        _list.Controls.Remove(_editor);
        _editor.Dispose();
        _editor = null;
    }
    /// <summary>Clicking anywhere on a card selects it, except on its own controls.</summary>
    private void AttachActivation(Control control, string id)
    {
        if (control is Button or TextBox) return;
        control.Click += (_, _) =>
        {
            SelectMemo(id);
            MemoActivated?.Invoke(this, id);
        };
        foreach (Control child in control.Controls) AttachActivation(child, id);
    }
    private void PaintCard(Graphics graphics, Rectangle bounds, DiffMemoRow memo, bool focused)
    {
        var active = _selectedId == memo.Id || focused;
        using var border = new Pen(active ? _theme.MemoAccent : _theme.Border, active ? 2 : 1);
        graphics.DrawRectangle(border, 0, 0, bounds.Width - 1, bounds.Height - 1);
        using var accent = new SolidBrush(memo.Orphaned
            ? _theme.MutedText
            : HdiffThemes.MemoAuthorColor(_theme, memo.Author));
        graphics.FillRectangle(accent, 0, 0, 3, bounds.Height);
    }
    private Label Chip(string t, Color b, Color f) => new() { AutoSize = true, BackColor = b, ForeColor = f, Font = new Font("Segoe UI", 7.5f, FontStyle.Bold), Margin = new Padding(0, 0, 5, 0), MinimumSize = new Size(18, 18), Padding = new Padding(4, 2, 4, 2), Text = t, TextAlign = ContentAlignment.MiddleCenter };
    private Label Meta(string t, ContentAlignment a, Color? color = null) => new() { AutoEllipsis = true, BackColor = _theme.MemoSurfaceBack, Dock = DockStyle.Fill, ForeColor = color ?? _theme.MutedText, Font = new Font("Segoe UI", 7.5f, color is null ? FontStyle.Regular : FontStyle.Bold), Text = t, TextAlign = a };
    private Button ActionButton(string text, bool primary = false)
    {
        var button = new Button
        {
            AutoSize = true,
            Text = text,
            Padding = new Padding(5, 0, 5, 0),
            Margin = new Padding(0, 0, 5, 0),
        };
        StyleButton(button, primary);
        return button;
    }
    private void StyleButton(Button button, bool primary)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = primary ? _theme.PrimaryActionBack : _theme.ButtonBorder;
        button.FlatAppearance.MouseOverBackColor = primary ? _theme.PrimaryActionHover : _theme.HeaderBack;
        button.BackColor = primary ? _theme.PrimaryActionBack : _theme.ButtonBack;
        button.ForeColor = primary ? _theme.PrimaryActionText : _theme.ButtonText;
    }
    private void ResizeChildren()
    {
        var width = CardWidth();
        foreach (Control control in _list.Controls)
        {
            if (control is TableLayoutPanel) control.Width = width;
        }
    }
    private int CardWidth() => Math.Max(250, _list.ClientSize.Width - _list.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 2);
    private int IndexOf(string id)
    {
        for (var index = 0; index < _list.Controls.Count; index++)
        {
            if (_list.Controls[index].Tag is string memoId && memoId == id) return index;
        }
        return -1;
    }
    private static TableLayoutPanel Table(Color backColor, Padding padding)
    {
        var table = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = backColor,
            ColumnCount = 1,
            Padding = padding,
            RowCount = 0,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return table;
    }
    private static SelectableMemoCard MemoCardTable(Color backColor, Padding padding)
    {
        var table = new SelectableMemoCard
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = backColor,
            ColumnCount = 1,
            Padding = padding,
            RowCount = 0,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return table;
    }
    private static Label Wrap(string t, Color b, Color f, float s, int w) => new() { AutoSize = true, BackColor = b, ForeColor = f, Font = new Font("맑은 고딕", s), MaximumSize = new Size(w, 0), Text = t };
    private static void Add(TableLayoutPanel table, Control control)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(control, 0, row);
    }
    private Color KindBack(DiffChangeKind k) => k switch { DiffChangeKind.Inserted => _theme.InsertedLineBack, DiffChangeKind.Deleted => _theme.DeletedLineBack, _ => _theme.HeaderBack };
    private Color KindFore(DiffChangeKind k) => k switch { DiffChangeKind.Inserted => _theme.AddedText, DiffChangeKind.Deleted => _theme.RemovedText, DiffChangeKind.Modified => _theme.Text, _ => _theme.MutedText };
    private static string KindLabel(DiffChangeKind k) => k switch { DiffChangeKind.Inserted => "추가", DiffChangeKind.Deleted => "삭제", DiffChangeKind.Modified => "수정", _ => "동일" };
    private static string Flatten(string t) => t.Replace("\r\n", "↵", StringComparison.Ordinal).Replace("\n", "↵", StringComparison.Ordinal).Replace("\r", "↵", StringComparison.Ordinal);
    private static string Shorten(string t, int n) => t.Length <= n ? t : t[..n] + "…";

    private sealed class SelectableMemoCard : TableLayoutPanel
    {
        public SelectableMemoCard() => SetStyle(ControlStyles.Selectable, true);
        protected override bool IsInputKey(Keys keyData) =>
            (keyData & Keys.KeyCode) is Keys.Enter or Keys.Space || base.IsInputKey(keyData);
    }
}
