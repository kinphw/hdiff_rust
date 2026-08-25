using System.Drawing.Drawing2D;
using System.ComponentModel;
using Hdiff.Core.Documents;

namespace Hdiff.UI;

internal enum DocumentSourceKind
{
    File,
    DirectText,
}

internal sealed record DocumentSource(DocumentSourceKind Kind, string? FilePath, string? Text)
{
    public static DocumentSource FromFile(string path) => new(DocumentSourceKind.File, Path.GetFullPath(path), null);
    public static DocumentSource FromText(string text) => new(DocumentSourceKind.DirectText, null, text);
}

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
    private readonly Button _directInputButton = new();
    private readonly Button _clearButton = new();
    private DocumentSource? _source;
    private bool _dragging;
    private HdiffThemePalette _theme = HdiffThemes.Light;

    public DocumentDropCard(string caption, Color accentColor)
    {
        _caption = caption;
        _accentColor = accentColor;
        // The card draws its own border at the client edge, so growing it must
        // repaint all of it. Without this the border stays where the old edge
        // was and the card is left with a stray line inside it.
        SetStyle(ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
        AllowDrop = true;
        BackColor = Color.White;
        Cursor = Cursors.Default;
        Height = 96;
        MinimumSize = new Size(350, 96);
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

        _directInputButton.AutoSize = false;
        _directInputButton.Text = "직접 입력…";
        _directInputButton.Click += (_, _) => EditDirectText();

        _clearButton.AutoSize = false;
        // Native Button text includes asymmetric glyph padding. The compact
        // close control paints a geometric X at its exact center instead.
        _clearButton.Text = string.Empty;
        _clearButton.Font = new Font("Segoe UI", 12f, FontStyle.Regular);
        _clearButton.Padding = Padding.Empty;
        _clearButton.TextAlign = ContentAlignment.MiddleCenter;
        _clearButton.Paint += PaintClearGlyph;
        _clearButton.Visible = false;
        _clearButton.Click += (_, _) => SetFile(null);

        Controls.AddRange(new Control[] { _badge, _captionLabel, _fileName, _metadata, _details, _directInputButton, _browseButton, _clearButton });
        foreach (var control in Controls.OfType<Control>().Append(this))
        {
            control.AllowDrop = true;
            control.DragEnter += OnDragEnter;
            control.DragLeave += OnDragLeave;
            control.DragDrop += OnDragDrop;
        }

        UpdateVisualState();
    }

    public event EventHandler? SourceChanged;
    public event EventHandler<CancelEventArgs>? SourceChanging;

    public DocumentSource? Source => _source;
    public bool HasSource => _source is not null;
    public string Caption => _caption;

    public void ApplyTheme(HdiffThemePalette theme)
    {
        _theme = theme;
        _badge.BackColor = theme.BadgeBack;
        _captionLabel.ForeColor = theme.MutedText;
        _fileName.ForeColor = theme.Text;
        _metadata.ForeColor = theme.MutedText;
        _details.ForeColor = theme.MutedText;
        ApplyButtonTheme(_browseButton);
        ApplyButtonTheme(_directInputButton);
        ApplyButtonTheme(_clearButton);
        UpdateVisualState();
    }

    public void SetFile(string? path)
    {
        SetSource(path is null ? null : DocumentSource.FromFile(path));
    }

    public void SetSource(DocumentSource? source)
    {
        if (source is { Kind: DocumentSourceKind.File } &&
            (source.FilePath is null || !File.Exists(source.FilePath) || !IsSupportedDocument(source.FilePath)))
            throw new ArgumentException(".hwp, .hwpx, .docx, .pdf, .xlsx, .xls, .xlsm, .xlsb, .txt 또는 .md 파일만 첨부할 수 있습니다.", nameof(source));
        if (source is { Kind: DocumentSourceKind.DirectText, Text: null })
            throw new ArgumentException("직접 입력 텍스트가 없습니다.", nameof(source));
        if (Equals(_source, source)) return;
        var changing = new CancelEventArgs();
        SourceChanging?.Invoke(this, changing);
        if (changing.Cancel) return;

        _source = source;
        UpdateVisualState();
        SourceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetParsedDetails(ParsedDocument document)
    {
        var characterCount = document.Blocks.Sum(block => block.Kind == DocumentBlockKind.Table && block.Rows is not null
            ? block.Text.Length + block.Rows.Sum(row => row.Sum(cell => cell.Length))
            : block.Text.Length);
        var rowCount = document.Blocks.Sum(block => block.Kind == DocumentBlockKind.Table && block.Rows is not null
            ? block.Rows.Count
            : 1);
        _details.Text = $"본문 {characterCount:N0}자 · {rowCount:N0}행 · {document.Reader}";
        _toolTip.SetToolTip(_details, _details.Text);
    }

    public void SetParsingState()
    {
        if (_source is null) return;
        _details.Text = "문서 구조와 본문을 읽는 중…";
        _toolTip.SetToolTip(_details, _details.Text);
    }

    public void SetParseFailure(string error)
    {
        if (_source is null) return;
        var firstLine = error.Replace('\r', ' ').Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        _details.Text = string.IsNullOrWhiteSpace(firstLine)
            ? "읽기 확인 실패 — 비교 시 다시 시도합니다"
            : $"읽기 실패 — {firstLine}";
        _toolTip.SetToolTip(_details, error);
    }

    public void ClearParsedDetails()
    {
        if (_source is null) return;
        if (_source.Kind == DocumentSourceKind.DirectText)
        {
            _details.Text = "붙여넣은 원문은 파일로 저장하지 않고 앱 메모리에서만 비교합니다.";
            _toolTip.SetToolTip(_details, _details.Text);
        }
        else
        {
            _details.Text = Path.GetDirectoryName(_source.FilePath!) ?? string.Empty;
            _toolTip.SetToolTip(_details, _source.FilePath);
        }
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var buttonWidth = 82;
        var right = ClientSize.Width - Padding.Right;
        _browseButton.SetBounds(right - buttonWidth, 35, buttonWidth, 28);
        _directInputButton.SetBounds(_browseButton.Left - buttonWidth - 6, 35, buttonWidth, 28);
        _clearButton.SetBounds(_directInputButton.Left - 31, 35, 26, 28);

        _badge.SetBounds(Padding.Left, 17, 42, 28);
        var textLeft = _badge.Right + 10;
        var textRight = _clearButton.Visible ? _clearButton.Left - 8 : _directInputButton.Left - 8;
        var textWidth = Math.Max(80, textRight - textLeft);
        _captionLabel.SetBounds(textLeft, 12, textWidth, 17);
        _fileName.SetBounds(textLeft, 30, textWidth, 22);
        _metadata.SetBounds(textLeft, 53, textWidth, 17);
        _details.SetBounds(Padding.Left, 75, Math.Max(80, ClientSize.Width - Padding.Horizontal), 17);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var borderColor = _dragging ? _accentColor : (_source is null ? _theme.Border : _theme.AttachedBorder);
        using var pen = new Pen(borderColor, _dragging ? 2f : 1f) { DashStyle = _source is null && !_dragging ? DashStyle.Dash : DashStyle.Solid };
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
        using var dialog = new OpenFileDialog
        {
            Filter = "지원 문서 (*.hwp;*.hwpx;*.docx;*.pdf;*.xlsx;*.xls;*.xlsm;*.xlsb;*.txt;*.md)|*.hwp;*.hwpx;*.docx;*.pdf;*.xlsx;*.xls;*.xlsm;*.xlsb;*.txt;*.md|한글 문서 (*.hwp;*.hwpx)|*.hwp;*.hwpx|Word 문서 (*.docx)|*.docx|PDF 문서 (*.pdf)|*.pdf|Excel 통합문서 (*.xlsx;*.xls;*.xlsm;*.xlsb)|*.xlsx;*.xls;*.xlsm;*.xlsb|텍스트 및 Markdown (*.txt;*.md)|*.txt;*.md",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) SetFile(dialog.FileName);
    }

    private void EditDirectText()
    {
        var initialText = _source is { Kind: DocumentSourceKind.DirectText } ? _source.Text ?? string.Empty : string.Empty;
        using var dialog = new DirectTextInputDialog(_caption, initialText, _theme);
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
            SetSource(DocumentSource.FromText(dialog.InputText));
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
        var hasSource = _source is not null;
        var isDirectText = _source?.Kind == DocumentSourceKind.DirectText;
        var filePath = _source?.FilePath;
        BackColor = _dragging ? _theme.DragSurfaceBack : (hasSource ? _theme.AttachedSurfaceBack : _theme.SurfaceBack);
        _captionLabel.Text = _dragging ? "이 위치에 놓으면 첨부됩니다" : _caption;
        _badge.Text = isDirectText ? "TEXT" : (filePath is null ? "HWP" : Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant());
        _fileName.Text = _dragging
            ? "HWP, Word, PDF, Excel, TXT 또는 Markdown 파일"
            : isDirectText
                ? "직접 입력 텍스트"
                : filePath is not null ? Path.GetFileName(filePath) : "파일을 끌어 놓거나 직접 입력하세요";
        _metadata.Text = isDirectText
            ? $"직접 입력 · {_source!.Text!.Length:N0}자 · {CountLines(_source.Text):N0}줄"
            : filePath is not null
                ? $"{Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant()} · {FormatFileSize(new FileInfo(filePath).Length)}"
                : "파일 선택 또는 [직접 입력…]을 이용하세요";
        _clearButton.Visible = hasSource;
        _browseButton.Text = filePath is not null ? "바꾸기…" : "파일 선택…";
        _directInputButton.Text = isDirectText ? "내용 편집…" : "직접 입력…";
        if (!hasSource) _details.Text = "원본은 수정하지 않고 읽기 전용으로 비교합니다.";
        ClearParsedDetails();
        _toolTip.SetToolTip(_fileName, filePath ?? (isDirectText ? "직접 입력 텍스트" : string.Empty));
        PerformLayout();
        Invalidate();
    }

    private static string? GetDroppedPath(DragEventArgs e) => e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0 ? files[0] : null;

    private static bool IsSupportedDocument(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".hwp" or ".hwpx" or ".docx" or ".pdf" or ".xlsx" or ".xls" or ".xlsm" or ".xlsb" or ".txt" or ".md";

    private static int CountLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Count(character => character == '\n') + 1;

    private static string FormatFileSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes:N0} B",
        < 1024 * 1024 => $"{bytes / 1024d:N1} KB",
        _ => $"{bytes / 1024d / 1024d:N1} MB",
    };

    private void ApplyButtonTheme(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = _theme.ButtonBorder;
        button.FlatAppearance.MouseOverBackColor = _theme.HeaderBack;
        button.BackColor = _theme.ButtonBack;
        button.ForeColor = _theme.ButtonText;
    }

    private static void PaintClearGlyph(object? sender, PaintEventArgs e)
    {
        if (sender is not Button button) return;
        var centerX = (button.ClientSize.Width - 1) / 2f;
        var centerY = (button.ClientSize.Height - 1) / 2f;
        var halfLength = Math.Max(4f, button.ClientSize.Height * 0.18f);
        var previousSmoothing = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            using var pen = new Pen(button.ForeColor, 1.25f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            e.Graphics.DrawLine(pen, centerX - halfLength, centerY - halfLength, centerX + halfLength, centerY + halfLength);
            e.Graphics.DrawLine(pen, centerX + halfLength, centerY - halfLength, centerX - halfLength, centerY + halfLength);
        }
        finally
        {
            e.Graphics.SmoothingMode = previousSmoothing;
        }
    }
}
