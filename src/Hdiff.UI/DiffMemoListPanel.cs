using Hdiff.Core.Diff;
using Hdiff.Core.Review;

namespace Hdiff.UI;

internal sealed record DiffMemoRow(
    string Id,
    int Number,
    DiffChangeKind Kind,
    DiffMemoSide Side,
    string Position,
    string Quote,
    string Text,
    string Author,
    string When,
    bool Orphaned);

/// <summary>
/// The review pane: every memo of the current comparison in reading order.
/// Selecting a memo scrolls the comparison to its paragraph, the way the Word
/// review pane follows the document.
/// </summary>
internal sealed class DiffMemoListPanel : Panel
{
    private readonly Label _title = new()
    {
        Dock = DockStyle.Fill,
        Font = new Font("Segoe UI", 9f, FontStyle.Bold),
        Padding = new Padding(12, 0, 0, 0),
        TextAlign = ContentAlignment.MiddleLeft,
    };
    private readonly Button _editButton = new() { Text = "편집", AutoSize = true, Enabled = false };
    private readonly Button _deleteButton = new() { Text = "삭제", AutoSize = true, Enabled = false };
    private readonly Button _closeButton = new() { Text = "닫기", AutoSize = true };
    private readonly FlowLayoutPanel _actions = new()
    {
        AutoSize = true,
        Dock = DockStyle.Right,
        FlowDirection = FlowDirection.LeftToRight,
        Padding = new Padding(0, 3, 8, 0),
        WrapContents = false,
    };
    private readonly Panel _header = new() { Dock = DockStyle.Top, Height = 30 };
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
        HideSelection = false,
        MultiSelect = false,
        OwnerDraw = true,
        View = View.Details,
    };
    private readonly Font _cellFont = new("맑은 고딕", 9f);
    private readonly Font _headerFont = new("Segoe UI", 8.5f, FontStyle.Bold);
    private HdiffThemePalette _theme = HdiffThemes.Light;
    private bool _suppressSelectionEvents;

    public DiffMemoListPanel()
    {
        Dock = DockStyle.Bottom;
        Height = 168;
        Visible = false;

        _list.Columns.Add("#", 34, HorizontalAlignment.Right);
        _list.Columns.Add("대상", 48, HorizontalAlignment.Center);
        _list.Columns.Add("위치", 96);
        _list.Columns.Add("구분", 48, HorizontalAlignment.Center);
        _list.Columns.Add("대상 문단", 300);
        _list.Columns.Add("메모", 360);
        _list.Columns.Add("작성자", 92);
        _list.Columns.Add("작성 시각", 116);

        _list.DrawColumnHeader += DrawColumnHeader;
        _list.DrawItem += (_, e) => e.DrawDefault = false;
        _list.DrawSubItem += DrawSubItem;
        _list.SelectedIndexChanged += (_, _) =>
        {
            UpdateActionState();
            if (_suppressSelectionEvents) return;
            if (SelectedId is { } id) MemoActivated?.Invoke(this, id);
        };
        _list.MouseDoubleClick += (_, _) =>
        {
            if (SelectedId is { } id) EditRequested?.Invoke(this, id);
        };
        _list.KeyDown += (_, e) =>
        {
            if (SelectedId is not { } id) return;
            if (e.KeyCode == Keys.Delete) DeleteRequested?.Invoke(this, id);
            else if (e.KeyCode == Keys.Enter) EditRequested?.Invoke(this, id);
            else return;
            e.Handled = true;
        };

        _editButton.Click += (_, _) =>
        {
            if (SelectedId is { } id) EditRequested?.Invoke(this, id);
        };
        _deleteButton.Click += (_, _) =>
        {
            if (SelectedId is { } id) DeleteRequested?.Invoke(this, id);
        };
        _closeButton.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);

        _actions.Controls.AddRange(new Control[] { _editButton, _deleteButton, _closeButton });
        // Filling title first, right-docked actions after: WinForms lays docked
        // controls out from the last added to the first.
        _header.Controls.Add(_title);
        _header.Controls.Add(_actions);
        Controls.Add(_list);
        Controls.Add(_header);
        ApplyTheme(_theme);
    }

    public event EventHandler<string>? MemoActivated;
    public event EventHandler<string>? EditRequested;
    public event EventHandler<string>? DeleteRequested;
    public event EventHandler? CloseRequested;

    public string? SelectedId => _list.SelectedItems.Count == 0 ? null : (string)_list.SelectedItems[0].Tag!;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cellFont.Dispose();
            _headerFont.Dispose();
        }
        base.Dispose(disposing);
    }

    public void SetMemos(IReadOnlyList<DiffMemoRow> memos)
    {
        var previouslySelected = SelectedId;
        _suppressSelectionEvents = true;
        try
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var memo in memos)
            {
                var item = new ListViewItem(memo.Number.ToString()) { Tag = memo.Id };
                item.SubItems.Add(memo.Side == DiffMemoSide.Old ? "변경 전" : "변경 후");
                item.SubItems.Add(memo.Orphaned ? "위치 없음" : memo.Position);
                item.SubItems.Add(KindLabel(memo.Kind));
                item.SubItems.Add(Flatten(memo.Quote));
                item.SubItems.Add(Flatten(memo.Text));
                item.SubItems.Add(memo.Author);
                item.SubItems.Add(memo.When);
                _list.Items.Add(item);
                if (memo.Id == previouslySelected) item.Selected = true;
            }
            _list.EndUpdate();
        }
        finally
        {
            _suppressSelectionEvents = false;
        }

        _title.Text = memos.Count == 0 ? "검토 메모" : $"검토 메모 {memos.Count}";
        UpdateActionState();
    }

    /// <summary>Selects a memo without navigating again, for flag clicks in the comparison.</summary>
    public void SelectMemo(string id)
    {
        var item = _list.Items.Cast<ListViewItem>().FirstOrDefault(candidate => (string)candidate.Tag! == id);
        if (item is null) return;
        _suppressSelectionEvents = true;
        try
        {
            item.Selected = true;
            item.Focused = true;
            item.EnsureVisible();
        }
        finally
        {
            _suppressSelectionEvents = false;
        }
        UpdateActionState();
    }

    public void ApplyTheme(HdiffThemePalette theme)
    {
        _theme = theme;
        BackColor = theme.SurfaceBack;
        _header.BackColor = theme.HeaderBack;
        _title.BackColor = theme.HeaderBack;
        _title.ForeColor = theme.Text;
        _actions.BackColor = theme.HeaderBack;
        _list.BackColor = theme.CanvasBack;
        _list.ForeColor = theme.Text;
        _list.BorderStyle = BorderStyle.None;
        foreach (var button in new[] { _editButton, _deleteButton, _closeButton })
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = theme.ButtonBorder;
            button.FlatAppearance.MouseOverBackColor = theme.HeaderBack;
            button.BackColor = theme.ButtonBack;
            button.ForeColor = theme.ButtonText;
            button.Margin = new Padding(4, 0, 0, 0);
        }
        _list.Invalidate();
        Invalidate();
    }

    private void UpdateActionState()
    {
        var hasSelection = _list.SelectedItems.Count > 0;
        _editButton.Enabled = hasSelection;
        _deleteButton.Enabled = hasSelection;
    }

    private void DrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using var background = new SolidBrush(_theme.HeaderBack);
        e.Graphics.FillRectangle(background, e.Bounds);
        using var separator = new Pen(_theme.Border);
        e.Graphics.DrawLine(separator, e.Bounds.Right - 1, e.Bounds.Top + 4, e.Bounds.Right - 1, e.Bounds.Bottom - 5);
        e.Graphics.DrawLine(separator, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        TextRenderer.DrawText(e.Graphics, e.Header!.Text, _headerFont,
            Rectangle.Inflate(e.Bounds, -6, 0), _theme.MutedText,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private void DrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        var selected = e.Item!.Selected;
        var backColor = selected ? _theme.MemoSurfaceBack : _theme.CanvasBack;
        using (var background = new SolidBrush(backColor))
        {
            e.Graphics.FillRectangle(background, e.Bounds);
        }
        if (selected && e.ColumnIndex == 0)
        {
            using var marker = new SolidBrush(_theme.MemoAccent);
            e.Graphics.FillRectangle(marker, e.Bounds.X, e.Bounds.Y, 3, e.Bounds.Height);
        }

        var foreColor = e.ColumnIndex switch
        {
            1 => SideColor(e.Item.SubItems[1].Text),
            3 => KindColor(e.Item.SubItems[3].Text),
            4 => _theme.MutedText,
            _ => _theme.Text,
        };
        var alignment = e.ColumnIndex switch
        {
            0 => TextFormatFlags.Right,
            1 or 3 => TextFormatFlags.HorizontalCenter,
            _ => TextFormatFlags.Left,
        };
        TextRenderer.DrawText(e.Graphics, e.SubItem!.Text, _cellFont,
            Rectangle.Inflate(e.Bounds, -5, 0), foreColor,
            alignment | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private Color SideColor(string label) => label == "변경 전" ? _theme.RemovedText : _theme.AddedText;

    private Color KindColor(string label) => label switch
    {
        "추가" => _theme.AddedText,
        "삭제" => _theme.RemovedText,
        _ => _theme.MutedText,
    };

    private static string KindLabel(DiffChangeKind kind) => kind switch
    {
        DiffChangeKind.Inserted => "추가",
        DiffChangeKind.Deleted => "삭제",
        DiffChangeKind.Modified => "수정",
        _ => "동일",
    };

    private static string Flatten(string text) => text
        .Replace("\r\n", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal);
}
