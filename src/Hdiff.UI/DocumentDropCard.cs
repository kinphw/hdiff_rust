using System.Drawing.Drawing2D;
using Hdiff.Core.Documents;

namespace Hdiff.UI;

/// <summary>File drop surface with an explicit attachment state instead of a bare path textbox.</summary>
internal sealed class DocumentDropCard : UserControl
{
    private readonly string _caption;
    private readonly Color _accentColor;
    private readonly ToolTip _toolTip = new();
    private readonly Label _badge = new();
    private readonly Label _captionLabel = new();
    private readonly Label _fileName = new();
    private readonly Label _metadata = new();
    private readonly Label _details = new();
    private readonly Button _browseButton = new();
    private readonly Button _clearButton = new();
    private string? _filePath;
    private bool _dragging;

    public DocumentDropCard(string caption, Color accentColor)
    {
        _caption = caption;
        _accentColor = accentColor;
        AllowDrop = true;
        BackColor = Color.White;
        Cursor = Cursors.Default;
        Height = 104;
        MinimumSize = new Size(350, 104);
        Margin = new Padding(4);
        Padding = new Padding(12);

        _badge.AutoSize = false;
        _badge.BackColor = Color.FromArgb(238, 246, 255);
        _badge.Font = new Font("Segoe UI", 7.5f, FontStyle.Bold);
        _badge.ForeColor = accentColor;
        _badge.Text = "HWP";
        _badge.TextAlign = ContentAlignment.MiddleCenter;

        _captionLabel.AutoEllipsis = true;
        _captionLabel.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        _captionLabel.ForeColor = Color.FromArgb(82, 90, 102);

        _fileName.AutoEllipsis = true;
        _fileName.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        _fileName.ForeColor = Color.FromArgb(35, 39, 47);

        _metadata.AutoEllipsis = true;
        _metadata.Font = new Font("Segoe UI", 8.5f);
        _metadata.ForeColor = Color.FromArgb(95, 104, 116);

        _details.AutoEllipsis = true;
        _details.Font = new Font("Segoe UI", 8f);
        _details.ForeColor = Color.FromArgb(111, 119, 131);

        _browseButton.AutoSize = false;
        _browseButton.Text = "파일 선택…";
        _browseButton.Click += (_, _) => Browse();

        _clearButton.AutoSize = false;
        _clearButton.Text = "×";
        _clearButton.Font = new Font("Segoe UI", 12f, FontStyle.Regular);
        _clearButton.Visible = false;
        _clearButton.Click += (_, _) => SetFile(null);

        Controls.AddRange(new Control[] { _badge, _captionLabel, _fileName, _metadata, _details, _browseButton, _clearButton });
        foreach (var control in Controls.OfType<Control>().Append(this))
        {
            control.AllowDrop = true;
            control.DragEnter += OnDragEnter;
            control.DragLeave += OnDragLeave;
            control.DragDrop += OnDragDrop;
        }

        UpdateVisualState();
    }

    public event EventHandler? FileChanged;

    public string? FilePath => _filePath;

    public void SetFile(string? path)
    {
        if (path is not null && (!File.Exists(path) || !IsSupportedDocument(path)))
        {
            throw new ArgumentException(".hwp 또는 .hwpx 파일만 첨부할 수 있습니다.", nameof(path));
        }

        _filePath = path;
        UpdateVisualState();
        FileChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetParsedDetails(ParsedDocument document)
    {
        if (!string.Equals(_filePath, document.SourcePath, StringComparison.OrdinalIgnoreCase)) return;
        var characterCount = document.Blocks.Sum(block => block.Text.Length);
        _details.Text = $"본문 {characterCount:N0}자 · {document.Blocks.Count:N0}문단 · {document.Reader}";
        _toolTip.SetToolTip(_details, _details.Text);
    }

    public void SetParsingState()
    {
        if (_filePath is null) return;
        _details.Text = "문서 구조와 본문을 읽는 중…";
        _toolTip.SetToolTip(_details, _details.Text);
    }

    public void SetParseFailure(string error)
    {
        if (_filePath is null) return;
        _details.Text = "읽기 확인 실패 — 비교 시 다시 시도합니다";
        _toolTip.SetToolTip(_details, error);
    }

    public void ClearParsedDetails()
    {
        if (_filePath is null) return;
        _details.Text = Path.GetDirectoryName(_filePath) ?? string.Empty;
        _toolTip.SetToolTip(_details, _filePath);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var buttonWidth = 82;
        var right = ClientSize.Width - Padding.Right;
        _browseButton.SetBounds(right - buttonWidth, 35, buttonWidth, 28);
        _clearButton.SetBounds(right - buttonWidth - 31, 35, 26, 28);

        _badge.SetBounds(Padding.Left, 17, 42, 28);
        var textLeft = _badge.Right + 10;
        var textRight = _clearButton.Visible ? _clearButton.Left - 8 : _browseButton.Left - 8;
        var textWidth = Math.Max(80, textRight - textLeft);
        _captionLabel.SetBounds(textLeft, 12, textWidth, 17);
        _fileName.SetBounds(textLeft, 30, textWidth, 22);
        _metadata.SetBounds(textLeft, 53, textWidth, 17);
        _details.SetBounds(Padding.Left, 75, Math.Max(80, ClientSize.Width - Padding.Horizontal), 17);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var borderColor = _dragging ? _accentColor : (_filePath is null ? Color.FromArgb(194, 202, 213) : Color.FromArgb(150, 195, 172));
        using var pen = new Pen(borderColor, _dragging ? 2f : 1f) { DashStyle = _filePath is null && !_dragging ? DashStyle.Dash : DashStyle.Solid };
        var bounds = ClientRectangle;
        bounds.Width--;
        bounds.Height--;
        e.Graphics.DrawRectangle(pen, bounds);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _toolTip.Dispose();
        base.Dispose(disposing);
    }

    private void Browse()
    {
        using var dialog = new OpenFileDialog { Filter = "한글 문서 (*.hwp;*.hwpx)|*.hwp;*.hwpx" };
        if (dialog.ShowDialog(this) == DialogResult.OK) SetFile(dialog.FileName);
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        var path = GetDroppedPath(e);
        _dragging = path is not null && IsSupportedDocument(path);
        e.Effect = _dragging ? DragDropEffects.Copy : DragDropEffects.None;
        UpdateVisualState();
    }

    private void OnDragLeave(object? sender, EventArgs e)
    {
        _dragging = false;
        UpdateVisualState();
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        var path = GetDroppedPath(e);
        _dragging = false;
        if (path is not null && IsSupportedDocument(path)) SetFile(path);
        else UpdateVisualState();
    }

    private void UpdateVisualState()
    {
        var hasFile = _filePath is not null;
        var filePath = _filePath!;
        BackColor = _dragging ? Color.FromArgb(239, 247, 255) : (hasFile ? Color.FromArgb(247, 252, 249) : Color.White);
        _captionLabel.Text = _dragging ? "이 위치에 놓으면 첨부됩니다" : _caption;
        _fileName.Text = _dragging ? ".hwp 또는 .hwpx 파일" : (hasFile ? Path.GetFileName(filePath) : "파일을 이 카드로 끌어 놓으세요");
        _metadata.Text = hasFile ? $"{Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant()} · {FormatFileSize(new FileInfo(filePath).Length)}" : "드래그하거나 [파일 선택…]을 누르세요";
        _clearButton.Visible = hasFile;
        _browseButton.Text = hasFile ? "바꾸기…" : "파일 선택…";
        if (!hasFile) _details.Text = "첨부 후 원본은 수정하지 않고 읽기 전용으로 비교합니다.";
        ClearParsedDetails();
        _toolTip.SetToolTip(_fileName, _filePath ?? string.Empty);
        PerformLayout();
        Invalidate();
    }

    private static string? GetDroppedPath(DragEventArgs e) => e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0 ? files[0] : null;

    private static bool IsSupportedDocument(string path) => Path.GetExtension(path).Equals(".hwp", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".hwpx", StringComparison.OrdinalIgnoreCase);

    private static string FormatFileSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes:N0} B",
        < 1024 * 1024 => $"{bytes / 1024d:N1} KB",
        _ => $"{bytes / 1024d / 1024d:N1} MB",
    };
}
