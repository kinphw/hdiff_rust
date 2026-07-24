using System.Drawing;
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
    private readonly Label _fontSizeLabel = new() { Text = "글자 크기", AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(12, 7, 2, 3) };
    private readonly ComboBox _fontSize = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 104 };
    private readonly CheckBox _comFallback = new() { Text = "직접 파서 실패 시 한글 COM 폴백", Checked = true, AutoSize = true };
    private readonly CheckBox _ignoreWhitespace = new() { Text = "공백만 다른 변경 무시", Checked = true, AutoSize = true };
    private readonly CheckBox _ignoreBlankLines = new() { Text = "빈 개행(엔터) 무시", Checked = true, AutoSize = true };
    private readonly CheckBox _googleDmpCleanup = new() { Text = "Google DMP 강조 정돈", Checked = true, AutoSize = true };
    private readonly CheckBox _wrapLongLines = new() { Text = "긴 줄 자동 줄바꿈", Checked = true, AutoSize = true };
    private readonly Label _summary = new() { AutoSize = true, Text = "전/후 HWP 또는 HWPX를 놓고 [비교]를 누르세요." };
    private readonly SideBySideDiffView _diffView = new() { Dock = DockStyle.Fill, WrapLongLines = true };
    private readonly ToolTip _toolTip = new();
    private readonly Icon? _applicationIcon = LoadApplicationIcon();
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

    public MainForm()
    {
        Text = "Hdiff — HWP/HWPX 변경 비교";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(980, 640);
        Size = new Size(1280, 820);
        Font = new Font("Segoe UI", 9f);
        if (_applicationIcon is not null) Icon = _applicationIcon;

        ConfigureFontSizePicker();
        _toolTip.SetToolTip(_ignoreWhitespace, "띄어쓰기·탭·줄 끝 공백만 다른 경우에는 변경으로 표시하지 않습니다.");
        _toolTip.SetToolTip(_ignoreBlankLines, "내용 없는 문단은 비교 행과 변경 요약에서 제외합니다. 체크를 풀면 원래 빈 문단도 표시합니다.");
        _toolTip.SetToolTip(_googleDmpCleanup, "수정으로 짝지어진 문단 내부의 강조 범위를 Google Diff Match Patch semantic cleanup으로 읽기 좋게 정돈합니다.");
        _toolTip.SetToolTip(_wrapLongLines, "VS Code의 Alt+Z처럼 긴 문단을 다음 표시 줄로 이어 보여 줍니다.");
        _oldFile.FileChanged += (_, _) => HandleFileChanged(_oldFile, oldSide: true);
        _newFile.FileChanged += (_, _) => HandleFileChanged(_newFile, oldSide: false);

        var sources = new TableLayoutPanel { Dock = DockStyle.Top, Height = 124, Padding = new Padding(12, 12, 12, 8), ColumnCount = 2, RowCount = 1 };
        sources.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        sources.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        sources.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        sources.Controls.Add(_oldFile, 0, 0);
        sources.Controls.Add(_newFile, 1, 0);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12, 0, 12, 8) };
        _compareButton.Click += async (_, _) => await CompareAsync();
        _swapButton.Click += (_, _) => SwapFiles();
        _wrapLongLines.CheckedChanged += (_, _) => _diffView.WrapLongLines = _wrapLongLines.Checked;
        _ignoreBlankLines.CheckedChanged += (_, _) => ClearPreviousComparison();
        _googleDmpCleanup.CheckedChanged += (_, _) => ClearPreviousComparison();
        _comFallback.CheckedChanged += (_, _) => RefreshPreviewsForParserSetting();
        actions.Controls.AddRange(new Control[] { _compareButton, _swapButton, _fontSizeLabel, _fontSize, _wrapLongLines, _ignoreWhitespace, _ignoreBlankLines, _googleDmpCleanup, _comFallback });

        var summaryPanel = new Panel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(14, 4, 12, 4) };
        summaryPanel.Controls.Add(_summary);

        Controls.Add(_diffView);
        Controls.Add(summaryPanel);
        Controls.Add(actions);
        Controls.Add(sources);
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
        _summary.Text = "파서 워커에서 전/후 문서를 읽는 중…";
        _diffView.Clear();

        try
        {
            var oldDoc = await GetDocumentForComparisonAsync(_oldFile, oldSide: true);
            var newDoc = await GetDocumentForComparisonAsync(_newFile, oldSide: false);
            var diff = new DocumentDiffer().Compare(oldDoc, newDoc, _ignoreWhitespace.Checked, _googleDmpCleanup.Checked, _ignoreBlankLines.Checked);
            _oldFile.SetParsedDetails(oldDoc);
            _newFile.SetParsedDetails(newDoc);
            _diffView.SetDiff(diff);
            _summary.Text = $"수정 {diff.Summary.Modified}, 추가 {diff.Summary.Inserted}, 삭제 {diff.Summary.Deleted}   |   전 {FormatDocumentStats(oldDoc)} → 후 {FormatDocumentStats(newDoc)}";
        }
        catch (Exception ex)
        {
            _summary.Text = "비교하지 못했습니다.";
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

    private void SwapFiles()
    {
        var oldPath = _oldFile.FilePath;
        _oldFile.SetFile(_newFile.FilePath);
        _newFile.SetFile(oldPath);
    }

    private void ClearPreviousComparison()
    {
        _diffView.Clear();
        _summary.Text = "파일이 바뀌었습니다. [비교]를 눌러 새 결과를 만드세요.";
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
        var useComFallback = _comFallback.Checked;
        if (path is null) return null;

        card.SetParsingState();
        try
        {
            var document = await Task.Run(() => new HwpWorkerClient().Read(path, useComFallback));
            if (!IsCurrentAttachment(card, path, useComFallback)) return null;

            if (oldSide) _oldPreview = document;
            else _newPreview = document;
            card.SetParsedDetails(document);
            return document;
        }
        catch (Exception ex)
        {
            if (IsCurrentAttachment(card, path, useComFallback)) card.SetParseFailure(ex.Message);
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

        var document = await Task.Run(() => new HwpWorkerClient().Read(path, _comFallback.Checked));
        if (oldSide) _oldPreview = document;
        else _newPreview = document;
        card.SetParsedDetails(document);
        return document;
    }

    private bool IsCurrentAttachment(DocumentDropCard card, string path, bool useComFallback) =>
        string.Equals(card.FilePath, path, StringComparison.OrdinalIgnoreCase) && _comFallback.Checked == useComFallback;

    private static bool IsPreviewForPath(ParsedDocument? document, string path) =>
        document is not null && string.Equals(document.SourcePath, path, StringComparison.OrdinalIgnoreCase);

    private void RefreshPreviewsForParserSetting()
    {
        if (_oldFile.FilePath is not null) HandleFileChanged(_oldFile, oldSide: true);
        if (_newFile.FilePath is not null) HandleFileChanged(_newFile, oldSide: false);
    }

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
}
