using System.Drawing;
using System.Drawing.Drawing2D;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Hdiff.Core.Diff;
using Hdiff.Core.Documents;
using Hdiff.Core.Export;
using Hdiff.Core.Review;
using Hdiff.UI.Worker;

namespace Hdiff.UI;

internal sealed class MainForm : Form
{
    private readonly DocumentDropCard _oldFile = new("변경 전", Color.FromArgb(40, 105, 173)) { Dock = DockStyle.Fill };
    private readonly DocumentDropCard _newFile = new("변경 후", Color.FromArgb(28, 132, 89)) { Dock = DockStyle.Fill };
    private readonly Button _compareButton = new() { Text = "비교", AutoSize = true };
    private readonly Button _swapButton = new() { Text = "전/후 바꿈", AutoSize = true };
    private readonly Button _exportButton = new() { Text = "HTML 추출", AutoSize = true, Enabled = false };
    private readonly Button _memoButton = new() { Text = "검토 메모", AutoSize = true, Enabled = false };
    private readonly Button _settingsButton = new() { AutoSize = false, Size = new Size(30, 26) };
    private readonly Button _aboutButton = new() { Text = "?", AutoSize = false, Size = new Size(28, 26), Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
    private readonly ComboBox _themePicker = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 112 };
    private readonly ComboBox _fontSize = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 104 };
    private readonly CheckBox _ignoreWhitespace = new() { Text = "Ignore Whitespace Changes", Checked = true, AutoSize = true };
    private readonly CheckBox _ignoreBlankLines = new() { Text = "Ignore Blank Lines", Checked = true, AutoSize = true };
    private readonly CheckBox _wrapLongLines = new() { Text = "Word Wrap", Checked = true, AutoSize = true };
    private readonly CheckBox _rowSeparators = new() { Text = "Row Separators", Checked = false, AutoSize = true };
    private readonly CheckBox _textSelection = new() { Text = "Text Selection", Checked = true, AutoSize = true };
    private readonly CheckBox _includeMemos = new() { Text = "Include Memos", Checked = false, AutoSize = true };
    private readonly Label _summary = new() { AutoSize = true, Text = "전/후 문서를 놓거나 직접 입력하면 자동으로 비교합니다." };
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
    private readonly DiffMemoListPanel _memoPanel = new();
    private readonly DiffMemoStore _memoStore = new();
    private readonly ToolTip _toolTip = new();
    private readonly Icon? _applicationIcon = LoadApplicationIcon();
    private readonly TableLayoutPanel _sources;
    private readonly TableLayoutPanel _actions;
    private readonly Panel _summaryPanel;
    private ParsedDocument? _oldPreview;
    private ParsedDocument? _newPreview;
    private DocumentSource? _oldPreviewSource;
    private DocumentSource? _newPreviewSource;
    private Task<ParsedDocument?>? _oldPreviewTask;
    private Task<ParsedDocument?>? _newPreviewTask;
    private DocumentSource? _oldPreviewTaskSource;
    private DocumentSource? _newPreviewTaskSource;
    private CancellationTokenSource? _automaticCompareCancellation;
    private int _comparisonRevision;
    private int _comparisonRunId;
    private DocumentDiff? _currentDiff;
    private (DocumentSource? Old, DocumentSource? New)? _memoSourcePair;
    private string _memoAuthor = Environment.UserName;
    private bool _memosDirty;

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
        Text = "Hdiff";
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
        _toolTip.SetToolTip(_textSelection, "비교 본문을 마우스로 선택하고 Ctrl+C로 복사할 수 있습니다.");
        _toolTip.SetToolTip(_exportButton, "현재 비교 화면을 오프라인 공유용 단일 HTML 파일로 저장합니다. 검토 메모도 함께 담깁니다.");
        _toolTip.SetToolTip(_memoButton, "검토 메모 목록을 열고 닫습니다. 비교 행에서 마우스 오른쪽 클릭 또는 Ctrl+M으로 메모를 답니다.");
        _toolTip.SetToolTip(_settingsButton, "설정");
        _toolTip.SetToolTip(_aboutButton, "Hdiff 정보");
        _settingsButton.Paint += PaintSettingsGlyph;
        _oldFile.SourceChanged += (_, _) => HandleSourceChanged(_oldFile, oldSide: true);
        _newFile.SourceChanged += (_, _) => HandleSourceChanged(_newFile, oldSide: false);
        _oldFile.SourceChanging += ConfirmSourceChangeWithMemos;
        _newFile.SourceChanging += ConfirmSourceChangeWithMemos;

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
        _compareButton.Click += async (_, _) =>
        {
            CancelScheduledAutomaticCompare();
            await CompareAsync();
        };
        _swapButton.Click += (_, _) => SwapFiles();
        _exportButton.Click += async (_, _) => await ExportHtmlAsync();
        _memoButton.Click += (_, _) => ShowMemoPanel(!_memoPanel.Visible);
        _settingsButton.Click += (_, _) => ShowSettings();
        _aboutButton.Click += (_, _) => ShowAbout();
        ConfigureMemoSurfaces();
        primaryActions.Controls.AddRange(new Control[] { _compareButton, _swapButton, _exportButton, _memoButton });
        utilityActions.Controls.AddRange(new Control[] { _settingsButton, _aboutButton });
        _actions.Controls.Add(primaryActions, 0, 0);
        _actions.Controls.Add(utilityActions, 1, 0);

        _summaryPanel = new Panel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(14, 4, 12, 4) };
        _summaryChips.Controls.AddRange(new Control[] { _modifiedChip, _insertedChip, _deletedChip, _summaryDetail });
        _summaryPanel.Controls.Add(_summary);
        _summaryPanel.Controls.Add(_summaryChips);

        // Added before the top-docked strips so the filling comparison view is
        // laid out last and keeps whatever the memo pane leaves.
        Controls.Add(_diffView);
        Controls.Add(_memoPanel);
        Controls.Add(_summaryPanel);
        Controls.Add(_actions);
        Controls.Add(_sources);
        ApplyTheme((DiffThemeOption)_themePicker.SelectedItem!, persist: false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CancelScheduledAutomaticCompare();
            _toolTip.Dispose();
            _applicationIcon?.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_memosDirty && MessageBox.Show(this, "HTML로 추출하지 않은 검토 메모 또는 회신이 있습니다. 저장하지 않고 Hdiff를 닫을까요?",
                "저장 안 된 검토 내용", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
        { e.Cancel = true; return; }
        base.OnFormClosing(e);
    }

    private async Task CompareAsync()
    {
        var oldSource = _oldFile.Source;
        var newSource = _newFile.Source;
        if (!ValidateSource(oldSource, "변경 전") || !ValidateSource(newSource, "변경 후")) return;
        var revision = _comparisonRevision;
        var runId = ++_comparisonRunId;
        _compareButton.Enabled = false;
        _currentDiff = null;
        _exportButton.Enabled = false;
        SetSummaryMessage("파서 워커에서 전/후 문서를 읽는 중…");
        _diffView.Clear();

        try
        {
            var oldDoc = await GetDocumentForComparisonAsync(_oldFile, oldSide: true, oldSource!);
            if (!IsCurrentComparison(runId, revision, oldSource!, newSource!)) return;
            var newDoc = await GetDocumentForComparisonAsync(_newFile, oldSide: false, newSource!);
            if (!IsCurrentComparison(runId, revision, oldSource!, newSource!)) return;
            var diff = new DocumentDiffer().Compare(
                oldDoc,
                newDoc,
                ignoreWhitespace: _ignoreWhitespace.Checked,
                useGoogleDmpSemanticCleanup: true,
                ignoreBlankLines: _ignoreBlankLines.Checked);
            if (!IsCurrentComparison(runId, revision, oldSource!, newSource!)) return;
            _oldFile.SetParsedDetails(oldDoc);
            _newFile.SetParsedDetails(newDoc);
            _diffView.SetDiff(diff);
            _currentDiff = diff;
            _exportButton.Enabled = true;
            AdoptMemosFor(diff, oldSource!, newSource!);
            ShowDiffSummary(diff, oldDoc, newDoc);
        }
        catch (Exception ex)
        {
            if (!IsCurrentComparison(runId, revision, oldSource!, newSource!)) return;
            SetSummaryMessage("비교하지 못했습니다.");
            MessageBox.Show(this, ex.Message, "Hdiff", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            if (_comparisonRunId == runId) _compareButton.Enabled = true;
        }
    }

    private void ConfigureMemoSurfaces()
    {
        _diffView.MemoAddRequested += (_, target) => AddMemo(target);
        _diffView.MemoOpenRequested += (_, target) => OpenMemosForCell(target);
        _memoStore.Changed += (_, _) => RefreshMemoSurfaces();
        _memoPanel.MemoActivated += (_, id) => NavigateToMemo(id);
        _memoPanel.MemoSubmitted += (_, item) => SaveMemo(item);
        _memoPanel.DeleteRequested += (_, id) => DeleteMemo(id);
        _memoPanel.ReplySubmitted += (_, item) => AddReply(item);
        _memoPanel.ReplyDeleteRequested += (_, key) => DeleteReply(key);
        _memoPanel.CloseRequested += (_, _) => ShowMemoPanel(false);
        _memoPanel.Author = _memoAuthor;
        RefreshMemoSurfaces();
    }

    private void MarkMemosDirty() { _memosDirty = true; _memoPanel.SetDirty(true); }

    private void ConfirmSourceChangeWithMemos(object? sender, CancelEventArgs e)
    {
        if (!_memosDirty) return;
        if (MessageBox.Show(this, "HTML로 추출하지 않은 검토 내용이 있습니다. 문서를 바꾸면 사라집니다. 계속할까요?",
                "저장 안 된 검토 내용", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
        { e.Cancel = true; return; }
        _memosDirty = false; _memoPanel.SetDirty(false);
    }

    /// <summary>
    /// Keeps memos across a re-comparison of the same pair, because comparison
    /// options are toggled mid-review. A different document pair starts a new
    /// review, so its memos would be meaningless.
    /// </summary>
    private void AdoptMemosFor(DocumentDiff diff, DocumentSource oldSource, DocumentSource newSource)
    {
        var pair = (Old: (DocumentSource?)oldSource, New: (DocumentSource?)newSource);
        if (_memoSourcePair is null || _memoSourcePair.Value != pair)
        {
            _memoSourcePair = pair;
            _memoStore.Clear();
        }
        else
        {
            _memoStore.Reanchor(diff);
        }
        RefreshMemoSurfaces();
    }

    private void RefreshMemoSurfaces()
    {
        _diffView.SetMemoCounts(_currentDiff is null
            ? new Dictionary<int, (int, int)>()
            : _memoStore.CountByRowSide());
        _memoPanel.SetMemos(BuildMemoRows());
        _memoPanel.SetDirty(_memosDirty);
        _memoButton.Text = _memoStore.Count == 0 ? "검토 메모" : $"검토 메모 {_memoStore.Count}";
        _memoButton.Enabled = _currentDiff is not null;
    }

    private IReadOnlyList<DiffMemoRow> BuildMemoRows()
    {
        if (_currentDiff is null) return Array.Empty<DiffMemoRow>();
        return _memoStore.Memos
            .Select((memo, index) => new DiffMemoRow(
                memo.Id,
                index + 1,
                memo.Anchor.Kind,
                memo.Anchor.Side,
                memo.Anchor.RowIndex,
                memo.Anchor.IsOrphaned ? string.Empty : DescribeRowPosition(_currentDiff.Rows[memo.Anchor.RowIndex]),
                memo.Anchor.Quote,
                memo.Text,
                memo.Author,
                memo.LastEditedAt.ToString("yyyy-MM-dd HH:mm"),
                memo.Anchor.IsOrphaned,
                memo.Replies))
            .ToArray();
    }

    private void ShowMemoPanel(bool show)
    {
        _memoPanel.Visible = show;
        PerformLayout();
    }

    private void AddMemo(SideBySideDiffView.DiffMemoTarget target)
    {
        if (_currentDiff is null || target.RowIndex < 0 || target.RowIndex >= _currentDiff.Rows.Count) return;
        var row = _currentDiff.Rows[target.RowIndex];
        var side = DiffMemoAnchor.ResolveSide(row, target.Side);
        ShowMemoPanel(true);
        _memoPanel.Author = _memoAuthor;
        _memoPanel.BeginAdd(new DiffMemoDraftTarget(target.RowIndex,row.Kind,side,DescribeRowPosition(row),
            (side == DiffMemoSide.Old ? row.OldText : row.NewText) ?? string.Empty));
        _diffView.PinnedMemoTarget = new SideBySideDiffView.DiffMemoTarget(target.RowIndex,side);
    }

    private void SaveMemo(DiffMemoSubmission item)
    {
        if (_currentDiff is null) return;
        _memoAuthor=item.Author; _memoPanel.Author=_memoAuthor; string id;
        if(item.MemoId is { } memoId){if(!_memoStore.Update(memoId,item.Author,item.Text,DateTimeOffset.Now))return;id=memoId;}
        else { id=_memoStore.Add(_currentDiff,item.Target.RowIndex,item.Target.Side,item.Author,item.Text,DateTimeOffset.Now).Id; }
        MarkMemosDirty();
        _memoPanel.SelectMemo(id);
        NavigateToMemo(id);
    }

    private void DeleteMemo(string id, bool confirm = true)
    {
        if (_memoStore.Find(id) is null) return;
        if (confirm && MessageBox.Show(this, "선택한 검토 메모를 삭제할까요?", "Hdiff",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        _memoStore.Remove(id);
        MarkMemosDirty();
    }

    private void AddReply(DiffMemoReplySubmission item)
    {
        _memoAuthor=item.Author;_memoPanel.Author=_memoAuthor;
        _memoStore.AddReply(item.MemoId,item.Author,item.Text,DateTimeOffset.Now);MarkMemosDirty();
        _memoPanel.SelectMemo(item.MemoId);NavigateToMemo(item.MemoId);
    }

    private void DeleteReply(DiffMemoReplyKey key)
    {
        if(MessageBox.Show(this,"이 회신을 삭제할까요?","Hdiff",MessageBoxButtons.OKCancel,MessageBoxIcon.Question)!=DialogResult.OK)return;
        if(!_memoStore.RemoveReply(key.MemoId,key.ReplyId))return;MarkMemosDirty();_memoPanel.SelectMemo(key.MemoId);
    }

    private void OpenMemosForCell(SideBySideDiffView.DiffMemoTarget target)
    {
        var memos = _memoStore.ForRow(target.RowIndex)
            .Where(memo => memo.Anchor.Side == target.Side)
            .ToArray();
        if (memos.Length == 0) return;
        ShowMemoPanel(true);
        _memoPanel.SelectMemo(memos[0].Id);
        _diffView.PinnedMemoTarget = target;
    }

    private void NavigateToMemo(string id)
    {
        if (_memoStore.Find(id) is not { } memo || memo.Anchor.IsOrphaned)
        {
            _diffView.PinnedMemoTarget = null;
            return;
        }
        // The pin stays while the memo stays selected, so the list row and the
        // paragraph on screen visibly belong together.
        _diffView.PinnedMemoTarget = new SideBySideDiffView.DiffMemoTarget(memo.Anchor.RowIndex,memo.Anchor.Side);
        _diffView.RevealDiffRow(memo.Anchor.RowIndex);
    }

    private static string DescribeRowPosition(DiffRow row) =>
        $"문단 {row.OldLine?.ToString() ?? "–"} → {row.NewLine?.ToString() ?? "–"}";

    private bool IsCurrentComparison(int runId, int revision, DocumentSource oldSource, DocumentSource newSource) =>
        _comparisonRunId == runId
        && _comparisonRevision == revision
        && Equals(_oldFile.Source, oldSource)
        && Equals(_newFile.Source, newSource);

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
        _textSelection.Checked = HdiffUserSettings.LoadTextSelectionEnabled();
        _includeMemos.Checked = HdiffUserSettings.LoadIncludeMemos();
        _diffView.WrapLongLines = _wrapLongLines.Checked;
        _diffView.TextSelectionEnabled = _textSelection.Checked;

        _wrapLongLines.CheckedChanged += (_, _) =>
        {
            _diffView.WrapLongLines = _wrapLongLines.Checked;
            HdiffUserSettings.SaveWrapLongLines(_wrapLongLines.Checked);
        };
        _ignoreWhitespace.CheckedChanged += (_, _) =>
        {
            HdiffUserSettings.SaveIgnoreWhitespaceChanges(_ignoreWhitespace.Checked);
            ClearPreviousComparison();
            ScheduleAutomaticCompare();
        };
        _ignoreBlankLines.CheckedChanged += (_, _) =>
        {
            HdiffUserSettings.SaveIgnoreBlankLines(_ignoreBlankLines.Checked);
            ClearPreviousComparison();
            ScheduleAutomaticCompare();
        };
        _textSelection.CheckedChanged += (_, _) =>
        {
            _diffView.TextSelectionEnabled = _textSelection.Checked;
            HdiffUserSettings.SaveTextSelectionEnabled(_textSelection.Checked);
        };
        _includeMemos.CheckedChanged += (_, _) =>
        {
            HdiffUserSettings.SaveIncludeMemos(_includeMemos.Checked);
            RestartDocumentPreviews();
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
        ApplyButtonTheme(_exportButton, theme);
        ApplyButtonTheme(_memoButton, theme);
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
        _memoPanel.ApplyTheme(theme);
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
        var oldSource = _oldFile.Source;
        _oldFile.SetSource(_newFile.Source);
        _newFile.SetSource(oldSource);
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
            _textSelection.Checked,
            _ignoreWhitespace.Checked,
            _ignoreBlankLines.Checked,
            _includeMemos.Checked,
            HdiffThemes.Get(currentTheme.Theme));

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _themePicker.SelectedItem = ThemeOptions[dialog.SelectedThemeIndex];
        _fontSize.SelectedItem = FontSizeOptions[dialog.SelectedFontSizeIndex];
        _wrapLongLines.Checked = dialog.WrapLongLines;
        _rowSeparators.Checked = dialog.ShowRowSeparators;
        _textSelection.Checked = dialog.TextSelectionEnabled;
        _ignoreWhitespace.Checked = dialog.IgnoreWhitespaceChanges;
        _ignoreBlankLines.Checked = dialog.IgnoreBlankLines;
        _includeMemos.Checked = dialog.IncludeMemos;
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
        var theme = ((DiffThemeOption)_themePicker.SelectedItem!).Theme;
        using var dialog = new HdiffAboutDialog(GetApplicationVersion(), HdiffThemes.Get(theme));
        dialog.ShowDialog(this);
    }

    private async Task ExportHtmlAsync()
    {
        var diff = _currentDiff;
        if (diff is null)
        {
            MessageBox.Show(this, "먼저 전/후 문서를 비교해 주세요.", "Hdiff", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = "Hdiff 비교 결과 HTML 저장",
            Filter = "HTML 파일 (*.html)|*.html",
            DefaultExt = "html",
            AddExtension = true,
            RestoreDirectory = true,
            FileName = CreateExportFileName(diff),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _exportButton.Enabled = false;
        try
        {
            var fontOption = (DiffFontSizeOption)_fontSize.SelectedItem!;
            var themeOption = (DiffThemeOption)_themePicker.SelectedItem!;
            var html = HtmlDiffExporter.Create(diff, new HtmlDiffExportOptions(
                FontSizePixels: (int)Math.Round(fontOption.Points * 96f / 72f),
                WrapLongLines: _wrapLongLines.Checked,
                ShowRowSeparators: _rowSeparators.Checked,
                Theme: themeOption.Theme == HdiffThemeKind.RustDark ? HtmlDiffTheme.RustDark : HtmlDiffTheme.Light,
                AppVersion: GetApplicationVersion(),
                GeneratedAt: DateTimeOffset.Now),
                _memoStore.Memos);
            await File.WriteAllTextAsync(dialog.FileName, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _memosDirty=false; _memoPanel.SetDirty(false);
            ShowHtmlExportCompleted(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"HTML을 저장하지 못했습니다.\n\n{ex.Message}", "Hdiff", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _exportButton.Enabled = _currentDiff is not null;
        }
    }

    private void ShowHtmlExportCompleted(string filePath)
    {
        var openButton = new TaskDialogButton("열기");
        var closeButton = new TaskDialogButton("종료");
        var page = new TaskDialogPage
        {
            Caption = "Hdiff",
            Heading = "비교 결과를 저장했습니다.",
            Text = filePath,
            Icon = TaskDialogIcon.Information,
            AllowCancel = true,
            SizeToContent = true,
            DefaultButton = openButton,
        };
        page.Buttons.Add(openButton);
        page.Buttons.Add(closeButton);

        if (TaskDialog.ShowDialog(this, page) != openButton) return;

        try
        {
            Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"저장된 HTML을 열지 못했습니다.\n\n{ex.Message}",
                "Hdiff",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private static string CreateExportFileName(DocumentDiff diff)
    {
        var oldName = Path.GetFileNameWithoutExtension(diff.OldDocument.SourcePath);
        var newName = Path.GetFileNameWithoutExtension(diff.NewDocument.SourcePath);
        var rawName = $"{oldName}_vs_{newName}";
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var safeName = new string(rawName.Select(character => invalidCharacters.Contains(character) ? '_' : character).ToArray()).Trim();
        if (safeName.Length > 120) safeName = safeName[..120].TrimEnd();
        return (string.IsNullOrWhiteSpace(safeName) ? "Hdiff_비교결과" : safeName) + ".html";
    }

    private static string GetApplicationVersion()
    {
        var version = typeof(MainForm).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Application.ProductVersion;
        return version.Split('+')[0];
    }

    private void ClearPreviousComparison()
    {
        _currentDiff = null;
        _exportButton.Enabled = false;
        _diffView.Clear();
        RefreshMemoSurfaces();
        SetSummaryMessage(_oldFile.HasSource && _newFile.HasSource
            ? "변경 내용을 자동으로 비교하는 중…"
            : "전/후 문서를 놓거나 직접 입력하면 자동으로 비교합니다.");
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
        var detail = $"전 {FormatDocumentStats(oldDocument)} → 후 {FormatDocumentStats(newDocument)}";
        var memoNotice = DescribeMemosInComparison(oldDocument, newDocument);
        _summaryDetail.Text = memoNotice is null ? detail : $"{detail}  ·  {memoNotice}";
        _summary.Visible = false;
        _summaryChips.Visible = true;
    }

    /// <summary>
    /// The COM fallback cannot separate memos from the body without editing
    /// the document, so it reports them instead. Surface that here, otherwise
    /// a comparison silently includes memo text the user asked to exclude.
    /// </summary>
    private static string? DescribeMemosInComparison(ParsedDocument oldDocument, ParsedDocument newDocument)
    {
        var oldMemos = HwpComFallbackReader.CountMemosReported(oldDocument.Warnings);
        var newMemos = HwpComFallbackReader.CountMemosReported(newDocument.Warnings);
        if (oldMemos == 0 && newMemos == 0) return null;
        return $"메모 제외 불가: 전 {oldMemos}건 · 후 {newMemos}건이 본문에 포함됨";
    }

    private void HandleSourceChanged(DocumentDropCard card, bool oldSide)
    {
        var source = card.Source;
        var revision = ++_comparisonRevision;
        if (oldSide)
        {
            _oldPreview = null;
            _oldPreviewSource = null;
            _oldPreviewTaskSource = source;
            _oldPreviewTask = StartPreviewAsync(card, oldSide: true, revision);
        }
        else
        {
            _newPreview = null;
            _newPreviewSource = null;
            _newPreviewTaskSource = source;
            _newPreviewTask = StartPreviewAsync(card, oldSide: false, revision);
        }
        ClearPreviousComparison();
        ScheduleAutomaticCompare();
    }

    private void RestartDocumentPreviews()
    {
        CancelScheduledAutomaticCompare();
        var revision = ++_comparisonRevision;
        var oldSource = _oldFile.Source;
        var newSource = _newFile.Source;

        _oldPreview = null;
        _oldPreviewSource = null;
        _oldPreviewTaskSource = oldSource;
        _oldPreviewTask = oldSource is null ? null : StartPreviewAsync(_oldFile, oldSide: true, revision);

        _newPreview = null;
        _newPreviewSource = null;
        _newPreviewTaskSource = newSource;
        _newPreviewTask = newSource is null ? null : StartPreviewAsync(_newFile, oldSide: false, revision);

        ClearPreviousComparison();
        ScheduleAutomaticCompare();
    }

    private void ScheduleAutomaticCompare()
    {
        CancelScheduledAutomaticCompare();
        if (!_oldFile.HasSource || !_newFile.HasSource) return;

        var cancellation = new CancellationTokenSource();
        _automaticCompareCancellation = cancellation;
        _ = RunScheduledAutomaticCompareAsync(cancellation);
    }

    private async Task RunScheduledAutomaticCompareAsync(CancellationTokenSource cancellation)
    {
        try
        {
            // File swaps and settings acceptance can raise several events in
            // succession. Wait briefly so only the final pair is compared.
            await Task.Delay(250, cancellation.Token);
            if (!cancellation.IsCancellationRequested) await CompareAsync();
        }
        catch (OperationCanceledException)
        {
            // A newer file pair or comparison option superseded this request.
        }
        finally
        {
            if (ReferenceEquals(_automaticCompareCancellation, cancellation))
                _automaticCompareCancellation = null;
            cancellation.Dispose();
        }
    }

    private void CancelScheduledAutomaticCompare()
    {
        var cancellation = _automaticCompareCancellation;
        _automaticCompareCancellation = null;
        if (cancellation is null) return;
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task<ParsedDocument?> StartPreviewAsync(DocumentDropCard card, bool oldSide, int revision)
    {
        var source = card.Source;
        if (source is null) return null;
        var includeMemos = _includeMemos.Checked;

        card.SetParsingState();
        try
        {
            var document = await ReadSourceAsync(card, source, includeMemos);
            if (_comparisonRevision != revision || !Equals(card.Source, source)) return null;

            if (oldSide)
            {
                _oldPreview = document;
                _oldPreviewSource = source;
            }
            else
            {
                _newPreview = document;
                _newPreviewSource = source;
            }
            card.SetParsedDetails(document);
            return document;
        }
        catch (Exception ex)
        {
            if (_comparisonRevision == revision && Equals(card.Source, source))
                card.SetParseFailure(ex.Message);
            return null;
        }
    }

    private async Task<ParsedDocument> GetDocumentForComparisonAsync(DocumentDropCard card, bool oldSide, DocumentSource source)
    {
        var preview = oldSide ? _oldPreview : _newPreview;
        var previewSource = oldSide ? _oldPreviewSource : _newPreviewSource;
        if (preview is not null && Equals(previewSource, source)) return preview;

        var previewTask = oldSide ? _oldPreviewTask : _newPreviewTask;
        var previewTaskSource = oldSide ? _oldPreviewTaskSource : _newPreviewTaskSource;
        if (previewTask is not null && Equals(previewTaskSource, source))
        {
            var previewResult = await previewTask;
            if (previewResult is not null) return previewResult;
        }

        var document = await ReadSourceAsync(card, source, _includeMemos.Checked);
        if (Equals(card.Source, source))
        {
            if (oldSide)
            {
                _oldPreview = document;
                _oldPreviewSource = source;
            }
            else
            {
                _newPreview = document;
                _newPreviewSource = source;
            }
            card.SetParsedDetails(document);
        }
        return document;
    }

    private static Task<ParsedDocument> ReadSourceAsync(
        DocumentDropCard card,
        DocumentSource source,
        bool includeMemos) =>
        source.Kind == DocumentSourceKind.DirectText
            ? Task.FromResult(new PlainTextReader().ReadText($"{card.Caption} 직접 입력.md", source.Text ?? string.Empty, "직접 입력"))
            : Task.Run(() => new HwpWorkerClient().Read(
                source.FilePath!,
                allowComFallback: true,
                includeMemos));

    private static Icon? LoadApplicationIcon()
    {
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("Hdiff.ApplicationIcon");
        if (stream is null) return null;
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }

    private bool ValidateSource(DocumentSource? source, string caption)
    {
        if (source is null)
        {
            MessageBox.Show(this, $"{caption} 파일을 선택하거나 텍스트를 직접 입력하세요.", "Hdiff", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        if (source.Kind == DocumentSourceKind.File && !File.Exists(source.FilePath))
        {
            MessageBox.Show(this, $"{caption} 파일을 찾을 수 없습니다.", "Hdiff", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
