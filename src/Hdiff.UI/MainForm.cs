using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using Hdiff.Core.Diff;
using Hdiff.Core.Documents;
using Hdiff.UI.Worker;

namespace Hdiff.UI;

internal sealed class MainForm : Form
{
    private readonly DocumentDropCard _oldFile = new("변경 전", Color.FromArgb(40, 105, 173)) { Dock = DockStyle.Fill };
    private readonly DocumentDropCard _newFile = new("변경 후", Color.FromArgb(28, 132, 89)) { Dock = DockStyle.Fill };
    private readonly Button _compareButton = new() { Text = "비교", AutoSize = true };
    private readonly Button _swapButton = new() { Text = "전/후 바꿈", AutoSize = true };
    private readonly Button _settingsButton = new() { AutoSize = false, Size = new Size(30, 26) };
    private readonly Button _aboutButton = new() { Text = "?", AutoSize = false, Size = new Size(28, 26), Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
    private readonly ComboBox _themePicker = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 112 };
    private readonly ComboBox _fontSize = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 104 };
    private readonly CheckBox _ignoreWhitespace = new() { Text = "Ignore Whitespace Changes", Checked = true, AutoSize = true };
    private readonly CheckBox _ignoreBlankLines = new() { Text = "Ignore Blank Lines", Checked = true, AutoSize = true };
    private readonly CheckBox _wrapLongLines = new() { Text = "Word Wrap", Checked = true, AutoSize = true };
    private readonly CheckBox _rowSeparators = new() { Text = "Row Separators", Checked = false, AutoSize = true };
    private readonly Label _summary = new() { AutoSize = true, Text = "전/후 HWP 또는 HWPX를 놓고 [비교]를 누르세요." };
    private readonly Label _modifiedChip = CreateSummaryChip();
    private readonly Label _insertedChip = CreateSummaryChip();
    private readonly Label _deletedChip = CreateSummaryChip();
    private readonly Label _summaryDetail = new() { AutoSize = true, Font = new Font("Segoe UI", 8.5f), Margin = new Padding(2, 4, 0, 0) };
    private readonly FlowLayoutPanel _summaryChips = new()
    {
        AutoSize = true,
        FlowDirection = FlowDirection.LeftToRight,
        Location = new Point(14, 3),
        Visible = false,
        WrapContents = false,
    };
    private readonly SideBySideDiffView _diffView = new() { Dock = DockStyle.Fill, WrapLongLines = true };
    private readonly ToolTip _toolTip = new();
    private readonly Icon? _applicationIcon = LoadApplicationIcon();
    private readonly TableLayoutPanel _sources;
    private readonly TableLayoutPanel _actions;
    private readonly Panel _summaryPanel;
    private ParsedDocument? _oldPreview;
    private ParsedDocument? _newPreview;
    private Task<ParsedDocument?>? _oldPreviewTask;
    private Task<ParsedDocument?>? _newPreviewTask;

    private static readonly DiffFontSizeOption[] FontSizeOptions =
    {
        new("small", "작게 (12px)", 9f),
        new("medium", "보통 (14px)", 10.5f),
        new("large", "크게 (16px)", 12f),
    };

    private static readonly DiffThemeOption[] ThemeOptions =
    {
        new("light", "화이트", HdiffThemeKind.Light),
        new("rust-dark", "블랙 (Rust)", HdiffThemeKind.RustDark),
    };

    public MainForm()
    {
        Text = "Hdiff — HWP/HWPX 변경 비교";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(980, 640);
        Size = new Size(1280, 820);
        Font = new Font("Segoe UI", 9f);
        if (_applicationIcon is not null) Icon = _applicationIcon;

        ConfigureFontSizePicker();
        ConfigureThemePicker();
        ConfigureComparisonOptions();
        ConfigureRowSeparators();
        _toolTip.SetToolTip(_ignoreWhitespace, "띄어쓰기·탭·줄 끝 공백만 다른 경우에는 변경으로 표시하지 않습니다.");
        _toolTip.SetToolTip(_ignoreBlankLines, "내용 없는 문단은 비교 행과 변경 요약에서 제외합니다. 체크를 풀면 원래 빈 문단도 표시합니다.");
        _toolTip.SetToolTip(_wrapLongLines, "VS Code의 Alt+Z처럼 긴 문단을 다음 표시 줄로 이어 보여 줍니다.");
        _toolTip.SetToolTip(_rowSeparators, "각 비교 행 아래에 옅은 구분선을 표시합니다. 기본값은 해제입니다.");
        _toolTip.SetToolTip(_settingsButton, "설정");
        _toolTip.SetToolTip(_aboutButton, "Hdiff 정보");
        _settingsButton.Paint += PaintSettingsGlyph;
        _oldFile.FileChanged += (_, _) => HandleFileChanged(_oldFile, oldSide: true);
        _newFile.FileChanged += (_, _) => HandleFileChanged(_newFile, oldSide: false);

        _sources = new TableLayoutPanel { Dock = DockStyle.Top, Height = 112, Padding = new Padding(12, 8, 12, 8), ColumnCount = 2, RowCount = 1 };
        _sources.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _sources.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _sources.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _sources.Controls.Add(_oldFile, 0, 0);
        _sources.Controls.Add(_newFile, 1, 0);

        var primaryActions = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };
        var utilityActions = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };
        _actions = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 42,
            Padding = new Padding(12, 0, 12, 8),
            ColumnCount = 2,
            RowCount = 1,
        };
        _actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _compareButton.Click += async (_, _) => await CompareAsync();
        _swapButton.Click += (_, _) => SwapFiles();
        _settingsButton.Click += (_, _) => ShowSettings();
        _aboutButton.Click += (_, _) => ShowAbout();
        primaryActions.Controls.AddRange(new Control[] { _compareButton, _swapButton });
        utilityActions.Controls.AddRange(new Control[] { _settingsButton, _aboutButton });
        _actions.Controls.Add(primaryActions, 0, 0);
        _actions.Controls.Add(utilityActions, 1, 0);

        _summaryPanel = new Panel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(14, 4, 12, 4) };
        _summaryChips.Controls.AddRange(new Control[] { _modifiedChip, _insertedChip, _deletedChip, _summaryDetail });
        _summaryPanel.Controls.Add(_summary);
        _summaryPanel.Controls.Add(_summaryChips);

        Controls.Add(_diffView);
        Controls.Add(_summaryPanel);
        Controls.Add(_actions);
        Controls.Add(_sources);
        ApplyTheme((DiffThemeOption)_themePicker.SelectedItem!, persist: false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
            _applicationIcon?.Dispose();
        }
        base.Dispose(disposing);
    }

    private async Task CompareAsync()
    {
        if (!ValidatePath(_oldFile.FilePath, "변경 전") || !ValidatePath(_newFile.FilePath, "변경 후")) return;
        _compareButton.Enabled = false;
        SetSummaryMessage("파서 워커에서 전/후 문서를 읽는 중…");
        _diffView.Clear();

        try
        {
            var oldDoc = await GetDocumentForComparisonAsync(_oldFile, oldSide: true);
            var newDoc = await GetDocumentForComparisonAsync(_newFile, oldSide: false);
            var diff = new DocumentDiffer().Compare(
                oldDoc,
                newDoc,
                ignoreWhitespace: _ignoreWhitespace.Checked,
                useGoogleDmpSemanticCleanup: true,
                ignoreBlankLines: _ignoreBlankLines.Checked);
            _oldFile.SetParsedDetails(oldDoc);
            _newFile.SetParsedDetails(newDoc);
            _diffView.SetDiff(diff);
            ShowDiffSummary(diff, oldDoc, newDoc);
        }
        catch (Exception ex)
        {
            SetSummaryMessage("비교하지 못했습니다.");
            MessageBox.Show(this, ex.Message, "Hdiff", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _compareButton.Enabled = true;
        }
    }

    private static string FormatDocumentStats(ParsedDocument document) => $"{document.Blocks.Sum(block => block.Text.Length):N0}자 · {document.Blocks.Count:N0}문단";

    private void ConfigureFontSizePicker()
    {
        _fontSize.DisplayMember = nameof(DiffFontSizeOption.Label);
        _fontSize.Items.AddRange(FontSizeOptions);

        var savedKey = HdiffUserSettings.LoadDiffFontSizeKey();
        var initial = FontSizeOptions.FirstOrDefault(option => option.Key == savedKey)
            ?? FontSizeOptions.Single(option => option.Key == "medium");
        _fontSize.SelectedItem = initial;
        ApplyFontSize(initial, persist: false);
        _fontSize.SelectedIndexChanged += (_, _) =>
        {
            if (_fontSize.SelectedItem is DiffFontSizeOption selected) ApplyFontSize(selected, persist: true);
        };
    }

    private void ApplyFontSize(DiffFontSizeOption option, bool persist)
    {
        _diffView.DocumentFontSizePoints = option.Points;
        if (persist) HdiffUserSettings.SaveDiffFontSizeKey(option.Key);
    }

    private void ConfigureRowSeparators()
    {
        _rowSeparators.Checked = HdiffUserSettings.LoadShowRowSeparators();
        _diffView.ShowRowSeparators = _rowSeparators.Checked;
        _rowSeparators.CheckedChanged += (_, _) =>
        {
            _diffView.ShowRowSeparators = _rowSeparators.Checked;
            HdiffUserSettings.SaveShowRowSeparators(_rowSeparators.Checked);
        };
    }

    private void ConfigureComparisonOptions()
    {
        _wrapLongLines.Checked = HdiffUserSettings.LoadWrapLongLines();
        _ignoreWhitespace.Checked = HdiffUserSettings.LoadIgnoreWhitespaceChanges();
        _ignoreBlankLines.Checked = HdiffUserSettings.LoadIgnoreBlankLines();
        _diffView.WrapLongLines = _wrapLongLines.Checked;

        _wrapLongLines.CheckedChanged += (_, _) =>
        {
            _diffView.WrapLongLines = _wrapLongLines.Checked;
            HdiffUserSettings.SaveWrapLongLines(_wrapLongLines.Checked);
        };
        _ignoreWhitespace.CheckedChanged += (_, _) =>
        {
            HdiffUserSettings.SaveIgnoreWhitespaceChanges(_ignoreWhitespace.Checked);
            ClearPreviousComparison();
        };
        _ignoreBlankLines.CheckedChanged += (_, _) =>
        {
            HdiffUserSettings.SaveIgnoreBlankLines(_ignoreBlankLines.Checked);
            ClearPreviousComparison();
        };
    }

    private void ConfigureThemePicker()
    {
        _themePicker.DisplayMember = nameof(DiffThemeOption.Label);
        _themePicker.Items.AddRange(ThemeOptions);
        var savedKey = HdiffUserSettings.LoadThemeKey();
        _themePicker.SelectedItem = ThemeOptions.FirstOrDefault(option => option.Key == savedKey)
            ?? ThemeOptions.Single(option => option.Key == "light");
        _themePicker.SelectedIndexChanged += (_, _) =>
        {
            if (_themePicker.SelectedItem is DiffThemeOption selected) ApplyTheme(selected, persist: true);
        };
    }

    private void ApplyTheme(DiffThemeOption option, bool persist)
    {
        var theme = HdiffThemes.Get(option.Theme);
        BackColor = theme.AppBack;
        ForeColor = theme.Text;
        _sources.BackColor = theme.AppBack;
        _actions.BackColor = theme.AppBack;
        _summaryPanel.BackColor = theme.SurfaceBack;
        _summary.ForeColor = theme.Text;
        ApplyPrimaryButtonTheme(_compareButton, theme);
        ApplyButtonTheme(_swapButton, theme);
        ApplyButtonTheme(_settingsButton, theme);
        ApplyButtonTheme(_aboutButton, theme);
        _summaryChips.BackColor = theme.SurfaceBack;
        _summary.ForeColor = theme.Text;
        _summaryDetail.ForeColor = theme.MutedText;
        ApplySummaryChipTheme(_modifiedChip, theme.HeaderBack, theme.Text);
        ApplySummaryChipTheme(_insertedChip, theme.InsertedLineBack, theme.AddedText);
        ApplySummaryChipTheme(_deletedChip, theme.DeletedLineBack, theme.RemovedText);
        _oldFile.ApplyTheme(theme);
        _newFile.ApplyTheme(theme);
        _diffView.ApplyTheme(theme);
        if (persist) HdiffUserSettings.SaveThemeKey(option.Key);
    }

    private static void ApplyButtonTheme(Button button, HdiffThemePalette theme)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = theme.ButtonBorder;
        button.FlatAppearance.MouseOverBackColor = theme.HeaderBack;
        button.BackColor = theme.ButtonBack;
        button.ForeColor = theme.ButtonText;
    }

    private static void ApplyPrimaryButtonTheme(Button button, HdiffThemePalette theme)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = theme.PrimaryActionBack;
        button.FlatAppearance.MouseOverBackColor = theme.PrimaryActionHover;
        button.BackColor = theme.PrimaryActionBack;
        button.ForeColor = theme.PrimaryActionText;
        button.Padding = new Padding(10, 1, 10, 1);
    }

    private static Label CreateSummaryChip() => new()
    {
        AutoSize = true,
        BorderStyle = BorderStyle.FixedSingle,
        Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
        Margin = new Padding(0, 1, 5, 1),
        Padding = new Padding(6, 2, 6, 2),
    };

    private static void ApplySummaryChipTheme(Label chip, Color backColor, Color textColor)
    {
        chip.BackColor = backColor;
        chip.ForeColor = textColor;
    }

    private void SwapFiles()
    {
        var oldPath = _oldFile.FilePath;
        _oldFile.SetFile(_newFile.FilePath);
        _newFile.SetFile(oldPath);
    }

    private void ShowSettings()
    {
        var currentTheme = (DiffThemeOption)_themePicker.SelectedItem!;
        var currentFontSize = (DiffFontSizeOption)_fontSize.SelectedItem!;
        using var dialog = new HdiffSettingsDialog(
            ThemeOptions.Select(option => option.Label).ToArray(),
            Array.IndexOf(ThemeOptions, currentTheme),
            FontSizeOptions.Select(option => option.Label).ToArray(),
            Array.IndexOf(FontSizeOptions, currentFontSize),
            _wrapLongLines.Checked,
            _rowSeparators.Checked,
            _ignoreWhitespace.Checked,
            _ignoreBlankLines.Checked,
            HdiffThemes.Get(currentTheme.Theme));

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _themePicker.SelectedItem = ThemeOptions[dialog.SelectedThemeIndex];
        _fontSize.SelectedItem = FontSizeOptions[dialog.SelectedFontSizeIndex];
        _wrapLongLines.Checked = dialog.WrapLongLines;
        _rowSeparators.Checked = dialog.ShowRowSeparators;
        _ignoreWhitespace.Checked = dialog.IgnoreWhitespaceChanges;
        _ignoreBlankLines.Checked = dialog.IgnoreBlankLines;
    }

    private static void PaintSettingsGlyph(object? sender, PaintEventArgs e)
    {
        if (sender is not Button button) return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var center = new PointF(button.ClientSize.Width / 2f, button.ClientSize.Height / 2f);
        using var pen = new Pen(button.Enabled ? button.ForeColor : SystemColors.GrayText, 1.45f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        for (var index = 0; index < 8; index++)
        {
            var angle = index * MathF.PI / 4f;
            var inner = new PointF(center.X + MathF.Cos(angle) * 6.2f, center.Y + MathF.Sin(angle) * 6.2f);
            var outer = new PointF(center.X + MathF.Cos(angle) * 9f, center.Y + MathF.Sin(angle) * 9f);
            e.Graphics.DrawLine(pen, inner, outer);
        }

        e.Graphics.DrawEllipse(pen, center.X - 6.3f, center.Y - 6.3f, 12.6f, 12.6f);
        e.Graphics.DrawEllipse(pen, center.X - 2.2f, center.Y - 2.2f, 4.4f, 4.4f);
    }

    private void ShowAbout()
    {
        var version = typeof(MainForm).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Application.ProductVersion;
        version = version.Split('+')[0];
        MessageBox.Show(
            this,
            $"Hdiff\n\n제작자: kinphw\nwww.github.com/hdiff\n버전: v{version}",
            "About Hdiff",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void ClearPreviousComparison()
    {
        _diffView.Clear();
        SetSummaryMessage("파일이 바뀌었습니다. [비교]를 눌러 새 결과를 만드세요.");
    }

    private void SetSummaryMessage(string message)
    {
        _summary.Text = message;
        _summary.Visible = true;
        _summaryChips.Visible = false;
    }

    private void ShowDiffSummary(DocumentDiff diff, ParsedDocument oldDocument, ParsedDocument newDocument)
    {
        _modifiedChip.Text = $"수정 {diff.Summary.Modified}";
        _insertedChip.Text = $"추가 {diff.Summary.Inserted}";
        _deletedChip.Text = $"삭제 {diff.Summary.Deleted}";
        _summaryDetail.Text = $"전 {FormatDocumentStats(oldDocument)} → 후 {FormatDocumentStats(newDocument)}";
        _summary.Visible = false;
        _summaryChips.Visible = true;
    }

    private void HandleFileChanged(DocumentDropCard card, bool oldSide)
    {
        if (oldSide)
        {
            _oldPreview = null;
            _oldPreviewTask = StartPreviewAsync(card, oldSide: true);
        }
        else
        {
            _newPreview = null;
            _newPreviewTask = StartPreviewAsync(card, oldSide: false);
        }
        ClearPreviousComparison();
    }

    private async Task<ParsedDocument?> StartPreviewAsync(DocumentDropCard card, bool oldSide)
    {
        var path = card.FilePath;
        if (path is null) return null;

        card.SetParsingState();
        try
        {
            var document = await Task.Run(() => new HwpWorkerClient().Read(path, allowComFallback: true));
            if (!IsCurrentAttachment(card, path)) return null;

            if (oldSide) _oldPreview = document;
            else _newPreview = document;
            card.SetParsedDetails(document);
            return document;
        }
        catch (Exception ex)
        {
            if (IsCurrentAttachment(card, path)) card.SetParseFailure(ex.Message);
            return null;
        }
    }

    private async Task<ParsedDocument> GetDocumentForComparisonAsync(DocumentDropCard card, bool oldSide)
    {
        var path = card.FilePath!;
        var preview = oldSide ? _oldPreview : _newPreview;
        if (IsPreviewForPath(preview, path)) return preview!;

        var previewTask = oldSide ? _oldPreviewTask : _newPreviewTask;
        if (previewTask is not null)
        {
            var previewResult = await previewTask;
            if (IsPreviewForPath(previewResult, path)) return previewResult!;
        }

        var document = await Task.Run(() => new HwpWorkerClient().Read(path, allowComFallback: true));
        if (oldSide) _oldPreview = document;
        else _newPreview = document;
        card.SetParsedDetails(document);
        return document;
    }

    private static bool IsCurrentAttachment(DocumentDropCard card, string path) =>
        string.Equals(card.FilePath, path, StringComparison.OrdinalIgnoreCase);

    private static bool IsPreviewForPath(ParsedDocument? document, string path) =>
        document is not null && string.Equals(document.SourcePath, path, StringComparison.OrdinalIgnoreCase);

    private static Icon? LoadApplicationIcon()
    {
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("Hdiff.ApplicationIcon");
        if (stream is null) return null;
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }

    private bool ValidatePath(string? path, string caption)
    {
        if (path is null || !File.Exists(path))
        {
            MessageBox.Show(this, $"{caption} 파일을 선택하세요.", "Hdiff", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        return true;
    }

    private sealed record DiffFontSizeOption(string Key, string Label, float Points)
    {
        public override string ToString() => Label;
    }

    private sealed record DiffThemeOption(string Key, string Label, HdiffThemeKind Theme)
    {
        public override string ToString() => Label;
    }
}
