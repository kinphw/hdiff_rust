using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Hdiff.Core.Diff;
using Hdiff.Core.Documents;
using Hdiff.Core.Export;
using Hdiff.Core.Hwp5;
using Hdiff.Core.Review;

var fixtureDir = Path.Combine(FindRepositoryRoot(), "artifacts", "generated-fixtures");
Directory.CreateDirectory(fixtureDir);

var beforeHwp = Path.Combine(fixtureDir, "before-synthetic-hwp5.hwp");
var afterHwp = Path.Combine(fixtureDir, "after-synthetic-hwp5.hwp");
Hwp5FixtureWriter.Write(beforeHwp,
    "제1조 목적",
    "평가기간은 5년으로 한다.",
    "이 지침은 2026년 1월 1일부터 시행한다.");
Hwp5FixtureWriter.Write(afterHwp,
    "제1조 목적",
    "평가기간은 7년으로 한다.",
    "이 지침은 2026년 1월 1일부터 시행한다.",
    "부칙: 시행 전 경과조치를 적용한다.");

var reader = new DocumentReader();
var before = reader.Read(beforeHwp);
var after = reader.Read(afterHwp);

Expect(before.Reader == "HWP5 직접 파서", "생성한 .hwp가 HWP5 직접 파서로 열려야 합니다.");
Expect(before.Blocks.Select(x => x.Text).SequenceEqual(new[]
{
    "제1조 목적",
    "평가기간은 5년으로 한다.",
    "이 지침은 2026년 1월 1일부터 시행한다.",
}), "생성한 HWP5의 문단이 원문과 같아야 합니다.");
Expect(after.Blocks.Count == 4, "변경 후 HWP5의 추가 문단을 읽어야 합니다.");

var spacedHwp = Path.Combine(fixtureDir, "spaced-synthetic-hwp5.hwp");
Hwp5FixtureWriter.Write(spacedHwp, "제1조 목적", "", "제2조 적용");
var parsedSpacedHwp = reader.Read(spacedHwp);
Expect(parsedSpacedHwp.Blocks.Select(x => x.Text).SequenceEqual(new[] { "제1조 목적", "", "제2조 적용" }), "HWP5의 빈 문단은 표시 옵션을 위해 보존해야 합니다.");

var controlledHwp = Path.Combine(fixtureDir, "inline-controls-synthetic-hwp5.hwp");
Hwp5FixtureWriter.WriteParaTextPayloads(
    controlledHwp,
    ParaTextPayload(
        Utf16("표 앞"),
        ExtendedControl(0x0B, "표오염문자노출"),
        Utf16("표 뒤")),
    ParaTextPayload(
        Utf16("탭 앞"),
        ExtendedControl(0x09, "탭오염문자노출"),
        Utf16("탭 뒤")),
    ParaTextPayload(
        Utf16("문자제어 앞"),
        CharControl(0x18),
        Utf16("문자제어 뒤")),
    ParaTextPayload(
        Utf16("잘린 제어 앞"),
        TruncatedExtendedControl(0x0B)));
var parsedControlledHwp = reader.Read(controlledHwp);
Expect(parsedControlledHwp.Blocks.Select(x => x.Text).SequenceEqual(new[]
{
    "표 앞표 뒤",
    "탭 앞\t탭 뒤",
    "문자제어 앞문자제어 뒤",
    "잘린 제어 앞",
}), "HWP5 8-WCHAR 인라인/확장 제어 payload가 본문으로 누출되지 않아야 합니다.");
Expect(parsedControlledHwp.Warnings.Any(warning => warning.Contains("0x0B", StringComparison.Ordinal)),
    "잘린 HWP5 8-WCHAR 제어 payload는 파서 경고로 보고해야 합니다.");

var memoHwp = Path.Combine(fixtureDir, "memo-synthetic-hwp5.hwp");
Hwp5FixtureWriter.WriteWithMemo(memoHwp, "메모 앞 본문", "검토자 메모", "메모 뒤 본문");
var memoExcludedHwp = reader.Read(memoHwp);
Expect(memoExcludedHwp.Blocks.Select(block => block.Text).SequenceEqual(new[] { "메모 앞 본문", "메모 뒤 본문" }),
    "HWP5 메모 하위 문단은 기본적으로 본문에서 제외해야 합니다.");
var memoIncludedHwp = reader.Read(memoHwp, includeMemos: true);
Expect(memoIncludedHwp.Blocks.Select(block => block.Text).SequenceEqual(new[] { "메모 앞 본문", "검토자 메모", "메모 뒤 본문" }),
    "Include Memos가 켜지면 HWP5 메모 하위 문단을 포함해야 합니다.");

var diff = new DocumentDiffer().Compare(before, after);
Expect(diff.Summary.HasChanges, "전/후 HWP5 차이를 감지해야 합니다.");
Expect(diff.ToGitLog().Contains("평가기간은 5년으로 한다.", StringComparison.Ordinal), "Git 로그에 변경 전 문장이 있어야 합니다.");
Expect(diff.ToGitLog().Contains("평가기간은 7년으로 한다.", StringComparison.Ordinal), "Git 로그에 변경 후 문장이 있어야 합니다.");
var changedRow = diff.Rows.Single(x => x.Kind == DiffChangeKind.Modified);
Expect(changedRow.OldFragments.Any(x => x is { Kind: InlineDiffFragmentKind.Removed, Text: "5" }), "변경 전 숫자만 문자 단위 삭제로 표시해야 합니다.");
Expect(changedRow.NewFragments.Any(x => x is { Kind: InlineDiffFragmentKind.Added, Text: "7" }), "변경 후 숫자만 문자 단위 추가로 표시해야 합니다.");

var blankBefore = new ParsedDocument("blank-before", new[]
{
    new DocumentBlock(DocumentBlockKind.Paragraph, "제1조 목적"),
    new DocumentBlock(DocumentBlockKind.Paragraph, ""),
    new DocumentBlock(DocumentBlockKind.Paragraph, "제2조 적용"),
}, "test", Array.Empty<string>());
var blankAfter = new ParsedDocument("blank-after", new[]
{
    new DocumentBlock(DocumentBlockKind.Paragraph, "제1조 목적"),
    new DocumentBlock(DocumentBlockKind.Paragraph, ""),
    new DocumentBlock(DocumentBlockKind.Paragraph, ""),
    new DocumentBlock(DocumentBlockKind.Paragraph, "제2조 적용"),
}, "test", Array.Empty<string>());
var blankIgnoredDiff = new DocumentDiffer().Compare(blankBefore, blankAfter, ignoreBlankLines: true);
Expect(!blankIgnoredDiff.Summary.HasChanges, "빈 문단 무시가 켜지면 연속 엔터만의 차이는 변경으로 세지 않아야 합니다.");
var blankShownDiff = new DocumentDiffer().Compare(blankBefore, blankAfter, ignoreBlankLines: false);
Expect(blankShownDiff.Summary.Inserted == 1, "빈 문단 무시를 끄면 추가된 엔터 한 줄을 표시해야 합니다.");

var semanticBefore = new ParsedDocument("semantic-before", new[]
{
    new DocumentBlock(DocumentBlockKind.Paragraph, "위원회는 관련 기준을 신속하게 정비한다."),
}, "test", Array.Empty<string>());
var semanticAfter = new ParsedDocument("semantic-after", new[]
{
    new DocumentBlock(DocumentBlockKind.Paragraph, "위원회는 관련 기준을 체계적으로 정비한다."),
}, "test", Array.Empty<string>());
var dmpApplied = GoogleDiffMatchPatchInlineDiffer.TryCreateFragments(
    semanticBefore.Blocks[0].Text,
    semanticAfter.Blocks[0].Text,
    out var dmpOldFragments,
    out var dmpNewFragments);
Expect(dmpApplied, "Google DMP semantic cleanup이 성공해야 합니다.");
Expect(string.Concat(dmpOldFragments.Select(x => x.Text)) == semanticBefore.Blocks[0].Text, "DMP 직접 결과가 변경 전 문단을 정확히 복원해야 합니다.");
Expect(string.Concat(dmpNewFragments.Select(x => x.Text)) == semanticAfter.Blocks[0].Text, "DMP 직접 결과가 변경 후 문단을 정확히 복원해야 합니다.");
var semanticRow = new DocumentDiffer().Compare(semanticBefore, semanticAfter).Rows.Single(x => x.Kind == DiffChangeKind.Modified);
Expect(string.Concat(semanticRow.OldFragments.Select(x => x.Text)) == semanticBefore.Blocks[0].Text, "DMP 정돈 후에도 변경 전 문단이 정확히 복원되어야 합니다.");
Expect(string.Concat(semanticRow.NewFragments.Select(x => x.Text)) == semanticAfter.Blocks[0].Text, "DMP 정돈 후에도 변경 후 문단이 정확히 복원되어야 합니다.");
Expect(semanticRow.OldFragments.Any(x => x.Kind == InlineDiffFragmentKind.Removed), "DMP 정돈 결과에 삭제 강조가 있어야 합니다.");
Expect(semanticRow.NewFragments.Any(x => x.Kind == InlineDiffFragmentKind.Added), "DMP 정돈 결과에 추가 강조가 있어야 합니다.");

var dmpClassificationBefore = new ParsedDocument("dmp-classification-before", new[]
{
    new DocumentBlock(DocumentBlockKind.Paragraph, "가나다라마바사"),
}, "test", Array.Empty<string>());
var dmpClassificationAfter = new ParsedDocument("dmp-classification-after", new[]
{
    new DocumentBlock(DocumentBlockKind.Paragraph, "가차카다라차카사"),
}, "test", Array.Empty<string>());
var dmpClassificationOn = new DocumentDiffer().Compare(
    dmpClassificationBefore,
    dmpClassificationAfter,
    useGoogleDmpSemanticCleanup: true);
var dmpClassificationOff = new DocumentDiffer().Compare(
    dmpClassificationBefore,
    dmpClassificationAfter,
    useGoogleDmpSemanticCleanup: false);
Expect(dmpClassificationOn.Rows.Select(row => row.Kind).SequenceEqual(dmpClassificationOff.Rows.Select(row => row.Kind)),
    "인라인 강조 정돈 옵션은 DiffPlex의 행 분류를 바꾸면 안 됩니다.");
Expect(dmpClassificationOn.Summary == dmpClassificationOff.Summary,
    "인라인 강조 정돈 옵션은 변경 요약 통계를 바꾸면 안 됩니다.");

var replacementBefore = new ParsedDocument("replacement-before", new[]
{
    new DocumentBlock(DocumentBlockKind.Paragraph, "Ⅰ. 검토배경"),
    new DocumentBlock(DocumentBlockKind.Paragraph, "라이선스 관련"),
    new DocumentBlock(DocumentBlockKind.Paragraph, "부정결제 유형구분"),
}, "test", Array.Empty<string>());
var replacementAfter = new ParsedDocument("replacement-after", new[]
{
    new DocumentBlock(DocumentBlockKind.Paragraph, "Ⅰ. 검토배경"),
    new DocumentBlock(DocumentBlockKind.Paragraph, "해외 주요국은 비대면 전자결제의 확산과 신종 금융사기 급증을 고려하여 규율체계를 재편 중"),
    new DocumentBlock(DocumentBlockKind.Paragraph, "유럽연합은 지급서비스규정을 개정하고 지급결제 관련 책임체계를 강화함"),
}, "test", Array.Empty<string>());
var replacementDiff = new DocumentDiffer().Compare(replacementBefore, replacementAfter);
Expect(replacementDiff.Summary.Modified == 2, "DiffPlex가 대응시킨 전면 교체 문단은 수정 행으로 유지해야 합니다.");
Expect(replacementDiff.Summary.Deleted == 0 && replacementDiff.Summary.Inserted == 0,
    "앱이 DiffPlex의 수정 행을 삭제·추가 행으로 재분류하면 안 됩니다.");
var replacementRows = replacementDiff.Rows.Where(row => row.Kind != DiffChangeKind.Unchanged).ToArray();
Expect(replacementRows.Select(row => row.Kind).SequenceEqual(new[]
{
    DiffChangeKind.Modified,
    DiffChangeKind.Modified,
}), "전면 교체 구간도 DiffPlex가 만든 좌우 대응을 그대로 유지해야 합니다.");
Expect(replacementRows.All(row => row.OldText is not null && row.NewText is not null),
    "DiffPlex의 수정 행에는 변경 전후 문단을 모두 유지해야 합니다.");

var exportBeforeBlocks = Enumerable.Range(1, 80)
    .Select(index => new DocumentBlock(DocumentBlockKind.Paragraph,
        index == 24
            ? "<script>alert('문서 본문')</script> & 검토"
            : $"제{index}조 비교 결과를 제3자에게 공유하기 위한 긴 시험 문단입니다. 행 번호 {index}."))
    .ToArray();
var exportAfterBlocks = exportBeforeBlocks
    .Select((block, index) => index == 23
        ? block with { Text = "<script>alert('변경 본문')</script> & 최종 검토" }
        : index % 17 == 0
            ? block with { Text = block.Text.Replace("시험", "공유용", StringComparison.Ordinal) }
            : block)
    .Append(new DocumentBlock(DocumentBlockKind.Paragraph, "HTML 공유 결과에 새로 추가된 마지막 문단입니다."))
    .ToArray();
var exportBefore = new ParsedDocument("변경 전 <검토>.hwp", exportBeforeBlocks, "test", Array.Empty<string>());
var exportAfter = new ParsedDocument("변경 후 & 최종.hwp", exportAfterBlocks, "test", Array.Empty<string>());
var exportDiff = new DocumentDiffer().Compare(exportBefore, exportAfter);
var exportedHtml = HtmlDiffExporter.Create(exportDiff, new HtmlDiffExportOptions(
    FontSizePixels: 14,
    WrapLongLines: true,
    ShowRowSeparators: false,
    Theme: HtmlDiffTheme.Light,
    AppVersion: "0.1.0-test",
    GeneratedAt: new DateTimeOffset(2026, 7, 29, 9, 30, 0, TimeSpan.FromHours(9))));
Expect(exportedHtml.StartsWith("<!doctype html>", StringComparison.Ordinal), "HTML 내보내기는 표준 문서 형식으로 시작해야 합니다.");
Expect(exportedHtml.Contains("id=\"diff-scroll\"", StringComparison.Ordinal)
    && !exportedHtml.Contains("id=\"old-scroll\"", StringComparison.Ordinal)
    && !exportedHtml.Contains("id=\"new-scroll\"", StringComparison.Ordinal),
    "공유 HTML은 좌우가 갈라질 수 없는 단일 공유 스크롤을 사용해야 합니다.");
Expect(exportedHtml.Contains("class=\"diff-pair\"", StringComparison.Ordinal)
    && !exportedHtml.Contains("function mirror(source, target)", StringComparison.Ordinal)
    && !exportedHtml.Contains("function equalizeRows()", StringComparison.Ordinal),
    "공유 HTML은 CSS 행 페어로 좌우 높이를 맞추고 전수 높이 측정을 하지 않아야 합니다.");
Expect(exportedHtml.Contains("scrollbar-width:none", StringComparison.Ordinal)
    && exportedHtml.Contains("pointermove", StringComparison.Ordinal)
    && exportedHtml.Contains("overview-canvas", StringComparison.Ordinal)
    && exportedHtml.Contains("overview-signal-canvas", StringComparison.Ordinal)
    && exportedHtml.Contains("Math.max(scale, lineHeight)", StringComparison.Ordinal)
    && !exportedHtml.Contains("Math.max(2 * scale, lineHeight)", StringComparison.Ordinal)
    && !exportedHtml.Contains("<button class=\"overview-signal", StringComparison.Ordinal)
    && !exportedHtml.Contains("overview-line", StringComparison.Ordinal),
    "공유 HTML은 행별 DOM 없이 Canvas로 그리는 드래그 가능한 변경 미니맵을 사용하고, 작은 변경을 2px로 부풀리지 않아야 합니다.");
Expect(!exportedHtml.Contains("source-strip", StringComparison.Ordinal)
    && !exportedHtml.Contains("id=\"theme-picker\"", StringComparison.Ordinal),
    "공유 HTML 상단에는 중복 파일 카드나 과도한 보기 설정을 넣지 않아야 합니다.");
Expect(exportedHtml.Contains("&lt;script&gt;alert", StringComparison.Ordinal)
    && !exportedHtml.Contains("<script>alert('문서 본문')</script>", StringComparison.Ordinal),
    "문서 본문은 실행 가능한 HTML이 되지 않도록 이스케이프해야 합니다.");
Expect(!exportedHtml.Contains("<link ", StringComparison.OrdinalIgnoreCase)
    && !exportedHtml.Contains(" src=", StringComparison.OrdinalIgnoreCase),
    "공유 HTML은 외부 스타일·스크립트 파일에 의존하지 않아야 합니다.");
Expect(!exportedHtml.Contains("</span>\r\n</div></div>", StringComparison.Ordinal)
    && !exportedHtml.Contains("</span>\n</div></div>", StringComparison.Ordinal)
    && !exportedHtml.Contains("<div class=\"line-text\">\r\n", StringComparison.Ordinal)
    && !exportedHtml.Contains("<div class=\"line-text\">\n", StringComparison.Ordinal),
    "공유 HTML의 pre-wrap 본문에 생성기용 개행이 들어가 빈 표시 줄을 만들면 안 됩니다.");
Expect(!exportedHtml.Contains("class=\"memo-flag\"", StringComparison.Ordinal)
    && !exportedHtml.Contains("\" class=\"wrap  with-memos\"", StringComparison.Ordinal)
    && exportedHtml.Contains("id=\"memo-empty\"", StringComparison.Ordinal)
    && exportedHtml.Contains("id=\"memo-add\"", StringComparison.Ordinal),
    "검토 메모 없이 추출해도 받는 사람이 메모를 새로 달 수 있어야 하고, 패널은 접힌 채여야 합니다.");
var exportedHtmlPath = Path.Combine(fixtureDir, "standalone-diff-preview.html");
File.WriteAllText(exportedHtmlPath, exportedHtml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
File.WriteAllText(Path.Combine(fixtureDir, "before-after.diff.txt"), diff.ToGitLog(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

// 검토 메모: 비교 행에 붙이고, 재비교 후에도 문단을 따라가며, 공유 HTML에 함께 담깁니다.
var memoStore = new DiffMemoStore();
var memoTime = new DateTimeOffset(2026, 7, 29, 10, 0, 0, TimeSpan.FromHours(9));
var modifiedRowIndex = exportDiff.Rows.Select((row, index) => (row, index)).First(x => x.row.Kind == DiffChangeKind.Modified).index;
var insertedRowIndex = exportDiff.Rows.Select((row, index) => (row, index)).First(x => x.row.Kind == DiffChangeKind.Inserted).index;
memoStore.Add(exportDiff, modifiedRowIndex, DiffMemoSide.New, "김검토", "표현이 바뀐 이유를 <확인>해 주세요.", memoTime);
memoStore.Add(exportDiff, modifiedRowIndex, DiffMemoSide.Old, "박작성", "삭제된 옛 표현에 대한 의견입니다.", memoTime.AddMinutes(3));
memoStore.Add(exportDiff, insertedRowIndex, DiffMemoSide.New, "김검토", "새 문단은 부칙으로 옮기는 편이 좋겠습니다.", memoTime.AddMinutes(5));
Expect(memoStore.Count == 3 && memoStore.ForRow(modifiedRowIndex).Count == 2,
    "한 비교 행에 여러 개의 검토 메모를 달 수 있어야 합니다.");
Expect(memoStore.CountByRowSide()[modifiedRowIndex] == (1, 1),
    "같은 행이라도 변경 전/후 어느 쪽에 단 메모인지 나누어 세어야 합니다.");
Expect(memoStore.ForRow(insertedRowIndex).Single().Anchor.Side == DiffMemoSide.New,
    "한쪽에만 있는 행의 메모는 빈 칸이 아니라 글이 있는 쪽에 붙어야 합니다.");
var oldSideMemo = memoStore.ForRow(modifiedRowIndex).Single(memo => memo.Anchor.Side == DiffMemoSide.Old);
Expect(oldSideMemo.Anchor.Quote == exportDiff.Rows[modifiedRowIndex].OldText,
    "변경 전에 단 메모는 변경 전 문단을 인용해야 합니다.");
var appReply=memoStore.AddReply(oldSideMemo.Id,"이회신","본체에서 추가한 회신입니다.",memoTime.AddMinutes(7));
Expect(memoStore.Find(oldSideMemo.Id)!.Replies.Single()==appReply,"본체 회신은 코어 데이터로 보존되어야 합니다.");
Expect(memoStore.RemoveReply(oldSideMemo.Id,appReply.Id),"회신을 삭제할 수 있어야 합니다.");
memoStore.AddReply(oldSideMemo.Id,"이회신","본체에서 추가한 회신입니다.",memoTime.AddMinutes(7));

static DocumentBlock MemoBlock(string text) => new(DocumentBlockKind.Paragraph, text);
var anchorBefore = new ParsedDocument("anchor-before", new[] { MemoBlock("가 조항"), MemoBlock("나 조항"), MemoBlock("다 조항") }, "test", Array.Empty<string>());
var anchorAfter = new ParsedDocument("anchor-after", new[] { MemoBlock("가 조항"), MemoBlock("나 조항"), MemoBlock("다 조항 개정") }, "test", Array.Empty<string>());
var anchorDiff = new DocumentDiffer().Compare(anchorBefore, anchorAfter);
var anchorStore = new DiffMemoStore();
var anchorRowIndex = anchorDiff.Rows.Select((row, index) => (row, index)).First(x => x.row.NewText == "다 조항 개정").index;
var anchorMemo = anchorStore.Add(anchorDiff, anchorRowIndex, DiffMemoSide.New, "김검토", "개정 근거 확인", memoTime);

var shiftedAfter = new ParsedDocument("anchor-after-2", new[] { MemoBlock("머리말"), MemoBlock("가 조항"), MemoBlock("나 조항"), MemoBlock("다 조항 개정") }, "test", Array.Empty<string>());
var shiftedDiff = new DocumentDiffer().Compare(anchorBefore, shiftedAfter);
anchorStore.Reanchor(shiftedDiff);
Expect(shiftedDiff.Rows[anchorStore.Find(anchorMemo.Id)!.Anchor.RowIndex].NewText == "다 조항 개정",
    "앞에 문단이 늘어 행 번호가 밀려도 검토 메모는 같은 문단을 따라가야 합니다.");

var replacedAfter = new ParsedDocument("anchor-after-3", new[] { MemoBlock("가 조항"), MemoBlock("나 조항") }, "test", Array.Empty<string>());
anchorStore.Reanchor(new DocumentDiffer().Compare(anchorBefore, replacedAfter));
Expect(anchorStore.Find(anchorMemo.Id)!.Anchor.IsOrphaned,
    "메모가 붙어 있던 문단이 사라지면 엉뚱한 행에 붙이지 말고 위치 없음으로 남겨야 합니다.");
Expect(anchorStore.Find(anchorMemo.Id)!.Text == "개정 근거 확인",
    "위치를 잃은 검토 메모도 내용은 보존해야 합니다.");

var exportedHtmlWithMemos = HtmlDiffExporter.Create(
    exportDiff,
    new HtmlDiffExportOptions(
        FontSizePixels: 14,
        WrapLongLines: true,
        ShowRowSeparators: false,
        Theme: HtmlDiffTheme.Light,
        AppVersion: "0.1.0-test",
        GeneratedAt: new DateTimeOffset(2026, 7, 29, 9, 30, 0, TimeSpan.FromHours(9))),
    memoStore.Memos);
Expect(exportedHtmlWithMemos.Contains("id=\"memo-panel\"", StringComparison.Ordinal)
    && exportedHtmlWithMemos.Contains("검토 메모 3", StringComparison.Ordinal)
    && exportedHtmlWithMemos.Contains("class=\"memo-flag\"", StringComparison.Ordinal),
    "검토 메모가 있으면 공유 HTML에 메모 패널과 행 표식을 함께 담아야 합니다.");
Expect(exportedHtmlWithMemos.Contains("&lt;확인&gt;", StringComparison.Ordinal)
    && !exportedHtmlWithMemos.Contains("<확인>", StringComparison.Ordinal),
    "메모 본문도 실행 가능한 HTML이 되지 않도록 이스케이프해야 합니다.");
Expect(exportedHtmlWithMemos.Contains("calc((100vw - var(--memo-w))/2 + 2px)", StringComparison.Ordinal)
    && !exportedHtmlWithMemos.Contains("left:calc(50vw + 2px)", StringComparison.Ordinal),
    "메모 패널이 차지한 폭만큼 좌우 열 기준을 줄여야 고정 줄번호가 어긋나지 않습니다.");
Expect(exportedHtmlWithMemos.Contains("class=\"memo-reply-add\"", StringComparison.Ordinal)
    && exportedHtmlWithMemos.Contains("id=\"memo-save\"", StringComparison.Ordinal)
    && exportedHtmlWithMemos.Contains("root.outerHTML", StringComparison.Ordinal)
    && exportedHtmlWithMemos.Contains("data-reply-round=\"0\"", StringComparison.Ordinal),
    "받는 사람이 회신을 쓰고 그 상태 그대로 다시 저장할 수 있어야 합니다.");
Expect(exportedHtmlWithMemos.Contains("data-reply-id=",StringComparison.Ordinal)
    && exportedHtmlWithMemos.Contains("본체에서 추가한 회신입니다.",StringComparison.Ordinal),
    "본체에서 작성한 회신도 공유 HTML에 포함되어야 합니다.");
Expect(exportedHtmlWithMemos.Contains("id=\"memo-add\"", StringComparison.Ordinal)
    && exportedHtmlWithMemos.Contains("function createMemo(", StringComparison.Ordinal)
    && exportedHtmlWithMemos.Contains("function renumber(", StringComparison.Ordinal),
    "받는 사람이 아무 비교 행에나 새 검토 메모를 달 수 있어야 합니다.");
Expect(exportedHtmlWithMemos.Contains("data-side=\"old\"", StringComparison.Ordinal)
    && exportedHtmlWithMemos.Contains("data-side=\"new\"", StringComparison.Ordinal)
    && exportedHtmlWithMemos.Contains("<span class=\"memo-side old\">전</span>", StringComparison.Ordinal),
    "공유 HTML도 메모가 변경 전·후 어느 쪽 이야기인지 구분해 보여 줘야 합니다.");
Expect(exportedHtmlWithMemos.Contains("id=\"memo-link-path\"", StringComparison.Ordinal)
    && exportedHtmlWithMemos.Contains("function drawLink(", StringComparison.Ordinal),
    "선택한 메모는 본문의 표식과 선으로 이어져야 합니다.");
Expect(exportedHtmlWithMemos.Contains("id=\"memo-unsaved\"", StringComparison.Ordinal)
    && exportedHtmlWithMemos.Contains("beforeunload", StringComparison.Ordinal),
    "메모와 회신은 한 번에 모아 저장하므로, 저장하지 않고 닫으면 경고해야 합니다.");
Expect(exportedHtmlWithMemos.Contains("window.showSaveFilePicker", StringComparison.Ordinal)
    && exportedHtmlWithMemos.Contains("saveHandle.createWritable()", StringComparison.Ordinal)
    && exportedHtmlWithMemos.Contains("id=\"memo-relocate\"", StringComparison.Ordinal)
    && exportedHtmlWithMemos.Contains("link.download = name;", StringComparison.Ordinal),
    "저장 위치를 고르고 같은 파일에 다시 저장할 수 있어야 하며, 지원하지 않는 브라우저에서는 내려받기로 돌아가야 합니다.");
// 내려받기는 결과를 알려 주지 않고, 브라우저가 저장 위치를 되묻는 중일 수도 있습니다.
Expect(exportedHtmlWithMemos.Contains("내려받는 중 · ${name}", StringComparison.Ordinal)
    && exportedHtmlWithMemos.Contains("저장됨 · ${saveHandle.name}", StringComparison.Ordinal),
    "실제로 디스크에 쓴 경우에만 저장됐다고 말해야 합니다.");
// 저장한 파일을 다시 열면 스크립트는 그때의 DOM만 보고 동작을 다시 붙여야 합니다.
// 만든 순간에만 붙이는 처리가 하나라도 있으면 그 버튼은 재열람 시 죽습니다.
foreach (var loadTimeBinding in new[]
{
    "for (const flag of document.querySelectorAll('.memo-flag')) bindFlag(flag);",
    "for (const button of document.querySelectorAll('.reply-delete')) bindReplyDelete(button);",
    "for (const button of document.querySelectorAll('.memo-reply-add')) bindReplyAdd(button);",
    "for (const button of document.querySelectorAll('.memo-card-remove')) bindCardRemove(button);",
    "for (const card of cards()) bindCard(card);",
})
{
    Expect(exportedHtmlWithMemos.Contains(loadTimeBinding, StringComparison.Ordinal),
        $"회신 파일을 다시 열었을 때도 동작하도록 로드 시점에 다시 연결해야 합니다: {loadTimeBinding}");
}
Expect(!exportedHtmlWithMemos.Contains("<link ", StringComparison.OrdinalIgnoreCase)
    && !exportedHtmlWithMemos.Contains(" src=", StringComparison.OrdinalIgnoreCase),
    "메모가 담긴 공유 HTML도 외부 파일에 의존하지 않아야 합니다.");
var memoPanelSource=File.ReadAllText(Path.Combine(FindRepositoryRoot(),"src","Hdiff.UI","DiffMemoListPanel.cs"));
var mainFormSource=File.ReadAllText(Path.Combine(FindRepositoryRoot(),"src","Hdiff.UI","MainForm.cs"));
var diffViewSource=File.ReadAllText(Path.Combine(FindRepositoryRoot(),"src","Hdiff.UI","SideBySideDiffView.cs"));
Expect(memoPanelSource.Contains("Dock = DockStyle.Right",StringComparison.Ordinal)
    && memoPanelSource.Contains("PanelWidth = 320",StringComparison.Ordinal)
    && memoPanelSource.Contains("CreateCard",StringComparison.Ordinal)
    && !memoPanelSource.Contains("new ListView",StringComparison.Ordinal),
    "본체 메모는 오른쪽 320px 카드 패널이어야 합니다.");
Expect(mainFormSource.Contains("_memoPanel.BeginAdd",StringComparison.Ordinal)
    && !mainFormSource.Contains("new DiffMemoEditorDialog",StringComparison.Ordinal)
    && mainFormSource.Contains("OnFormClosing",StringComparison.Ordinal),
    "메모 작성은 인라인이어야 하며 미저장 종료 경고가 있어야 합니다.");
Expect(diffViewSource.Contains("DrawMemoAdd",StringComparison.Ordinal)&&diffViewSource.Contains("DrawMemoLink",StringComparison.Ordinal),
    "행 호버 추가 버튼과 선택 메모 연결선이 있어야 합니다.");
var exportedMemoHtmlPath = Path.Combine(fixtureDir, "standalone-diff-preview-memos.html");
File.WriteAllText(exportedMemoHtmlPath, exportedHtmlWithMemos, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

var hwpx = Path.Combine(fixtureDir, "sample.hwpx");
WriteHwpx(hwpx, "제2조 적용", "HWPX도 한글 없이 읽는다.");
var parsedHwpx = reader.Read(hwpx);
Expect(parsedHwpx.Reader == "HWPX 직접 파서", "생성한 HWPX를 직접 읽어야 합니다.");
Expect(parsedHwpx.Blocks.Select(x => x.Text).SequenceEqual(new[] { "제2조 적용", "HWPX도 한글 없이 읽는다." }), "HWPX 문단이 원문과 같아야 합니다.");

var spacedHwpx = Path.Combine(fixtureDir, "spaced-sample.hwpx");
WriteHwpx(spacedHwpx, "제2조 적용", "", "제3조 시행");
var parsedSpacedHwpx = reader.Read(spacedHwpx);
Expect(parsedSpacedHwpx.Blocks.Select(x => x.Text).SequenceEqual(new[] { "제2조 적용", "", "제3조 시행" }), "HWPX의 빈 문단은 표시 옵션을 위해 보존해야 합니다.");

var memoHwpx = Path.Combine(fixtureDir, "memo-sample.hwpx");
WriteMemoHwpx(memoHwpx, "메모 앞 본문", "검토자 메모", "메모 뒤 본문");
var memoExcludedHwpx = reader.Read(memoHwpx);
Expect(memoExcludedHwpx.Blocks.Select(block => block.Text).SequenceEqual(new[] { "메모 앞 본문메모 뒤 본문" }),
    "HWPX fieldBegin type=MEMO의 하위 텍스트는 기본적으로 본문에서 제외해야 합니다.");
var memoIncludedHwpx = reader.Read(memoHwpx, includeMemos: true);
Expect(memoIncludedHwpx.Blocks.Select(block => block.Text).SequenceEqual(new[] { "메모 앞 본문검토자 메모메모 뒤 본문" }),
    "Include Memos가 켜지면 HWPX 메모 텍스트를 포함해야 합니다.");

var textPath = Path.Combine(fixtureDir, "sample.txt");
File.WriteAllText(textPath, "제1조 목적\r\n\r\n텍스트 파일도 읽는다.", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
var parsedText = reader.Read(textPath);
Expect(parsedText.Reader == "텍스트 직접 파서", "UTF-8 TXT 파일은 텍스트 직접 파서로 열어야 합니다.");
Expect(parsedText.Blocks.Select(x => x.Text).SequenceEqual(new[] { "제1조 목적", "", "텍스트 파일도 읽는다." }),
    "TXT 줄과 빈 줄을 비교 문단으로 보존해야 합니다.");

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
var cp949Path = Path.Combine(fixtureDir, "sample-cp949.txt");
File.WriteAllBytes(cp949Path, Encoding.GetEncoding(949).GetBytes("한글 레거시 텍스트\r\n두 번째 줄"));
var parsedCp949 = reader.Read(cp949Path);
Expect(parsedCp949.Reader.Contains("CP949", StringComparison.Ordinal), "CP949 TXT 파일의 감지 인코딩을 표시해야 합니다.");
Expect(parsedCp949.Blocks.Select(x => x.Text).SequenceEqual(new[] { "한글 레거시 텍스트", "두 번째 줄" }),
    "폐쇄망의 레거시 CP949 한글 TXT 파일도 읽어야 합니다.");

var markdownPath = Path.Combine(fixtureDir, "sample.md");
File.WriteAllText(markdownPath, "# 검토 결과\n\n- 변경 사항", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
var parsedMarkdown = reader.Read(markdownPath);
Expect(parsedMarkdown.Reader == "Markdown 직접 파서", "UTF-8 BOM Markdown 파일은 Markdown 직접 파서로 열어야 합니다.");
Expect(parsedMarkdown.Blocks.Select(x => x.Text).SequenceEqual(new[] { "# 검토 결과", "", "- 변경 사항" }),
    "Markdown 문법과 빈 줄을 원문 그대로 보존해야 합니다.");

var directInput = new PlainTextReader().ReadText(
    "변경 후 직접 입력.md",
    "# AI 검토\r\n\r\n본문 **강조**",
    "직접 입력");
Expect(directInput.Reader == "직접 입력", "붙여넣은 텍스트는 직접 입력 소스로 표시해야 합니다.");
Expect(directInput.Blocks.Select(x => x.Text).SequenceEqual(new[] { "# AI 검토", "", "본문 **강조**" }),
    "직접 입력의 Markdown 문법과 줄 구조를 그대로 비교해야 합니다.");
var mixedSourceDiff = new DocumentDiffer().Compare(parsedText, directInput);
Expect(mixedSourceDiff.Summary.HasChanges, "TXT 파일과 직접 입력 텍스트를 서로 비교할 수 있어야 합니다.");

Console.WriteLine("PASS: HWP5/HWPX/TXT/Markdown/direct-input reader and diff tests");
Console.WriteLine($"Generated HWP5 fixtures: {beforeHwp}");
Console.WriteLine($"Diff log: {Path.Combine(fixtureDir, "before-after.diff.txt")}");
Console.WriteLine($"Standalone HTML preview: {exportedHtmlPath}");
Console.WriteLine($"Standalone HTML preview (memos): {exportedMemoHtmlPath}");

if (args.Contains("--with-com", StringComparer.OrdinalIgnoreCase) && OperatingSystem.IsWindows())
{
    var hancomHwp = Path.Combine(fixtureDir, "hancom-generated.hwp");
    WriteHancomHwp(hancomHwp, "한글 COM으로 생성한 HWP5 문단입니다.", "직접 파서가 이 문단을 읽어야 합니다.");
    var actual = reader.Read(hancomHwp);
    Console.WriteLine("Hancom HWP5 blocks: " + string.Join(" | ", actual.Blocks.Select(x => $"[{x.Text}]")));
    Expect(actual.Blocks.Select(x => x.Text).SequenceEqual(new[]
    {
        "한글 COM으로 생성한 HWP5 문단입니다.",
        "직접 파서가 이 문단을 읽어야 합니다.",
    }), "한글 COM 생성 HWP5 문단을 직접 파서로 정확히 복원해야 합니다.");
    Console.WriteLine($"PASS: Hancom-generated HWP5 direct-reader test ({hancomHwp})");

    var hancomMemoHwp = Path.Combine(fixtureDir, "hancom-generated-memo.hwp");
    WriteHancomMemoHwp(hancomMemoHwp, "메모 앞 본문", "실제 한글 메모 내용", "메모 뒤 본문");
    var actualMemoExcluded = reader.Read(hancomMemoHwp);
    var actualMemoIncluded = reader.Read(hancomMemoHwp, includeMemos: true);
    var excludedText = string.Join("\n", actualMemoExcluded.Blocks.Select(block => block.Text));
    var includedText = string.Join("\n", actualMemoIncluded.Blocks.Select(block => block.Text));
    Console.WriteLine("Hancom memo excluded blocks: " + string.Join(" | ", actualMemoExcluded.Blocks.Select(block => $"[{block.Text}]")));
    Console.WriteLine("Hancom memo included blocks: " + string.Join(" | ", actualMemoIncluded.Blocks.Select(block => $"[{block.Text}]")));
    Expect(!excludedText.Contains("실제 한글 메모 내용", StringComparison.Ordinal),
        "실제 한글이 저장한 HWP 메모는 기본 본문에서 제외해야 합니다.");
    Expect(includedText.Contains("실제 한글 메모 내용", StringComparison.Ordinal),
        "Include Memos가 켜지면 실제 한글이 저장한 HWP 메모를 읽어야 합니다.");
    Expect(excludedText.Contains("메모 앞", StringComparison.Ordinal)
        && excludedText.Contains("메모 뒤", StringComparison.Ordinal),
        "HWP 메모를 제외해도 앞뒤 본문은 보존해야 합니다.");
    Console.WriteLine($"PASS: Hancom-generated HWP5 memo exclusion test ({hancomMemoHwp})");

    // The COM fallback lives in Hdiff.UI, which this project does not
    // reference. Drive it the way the app does instead: through the worker
    // process, which is also the only place COM is allowed to run.
    var workerExecutable = FindWorkerExecutable(FindRepositoryRoot());
    if (workerExecutable is null)
    {
        Console.WriteLine("SKIP: Hdiff.exe가 없어 COM 폴백 메모 테스트를 건너뜁니다. src/Hdiff.UI를 먼저 빌드하세요.");
    }
    else
    {
        // The COM path cannot separate memos without editing the document, so
        // it reports them instead of dropping them. Excluding them would need
        // DeleteCtrl (sets the modified flag) or a per-memo list scan (raised
        // a COMException on every measured run) — both risk a DLP/DRM popup.
        var (comDefault, defaultWarnings) = ReadThroughComFallback(workerExecutable, hancomMemoHwp, includeMemos: false);
        var (comIncluded, includedWarnings) = ReadThroughComFallback(workerExecutable, hancomMemoHwp, includeMemos: true);
        Console.WriteLine("COM fallback (memos excluded requested): " + comDefault.Replace("\n", " | "));
        Console.WriteLine("COM fallback (memos included): " + comIncluded.Replace("\n", " | "));
        Expect(comDefault.Contains("메모 앞", StringComparison.Ordinal)
            && comDefault.Contains("메모 뒤", StringComparison.Ordinal),
            "COM 폴백은 본문을 그대로 읽어야 합니다.");
        // MainForm reads the count back out of this warning with a regex
        // anchored at the start, so the prefix is part of the contract.
        Expect(defaultWarnings.Any(warning => warning.StartsWith("이 문서에는 메모 1건", StringComparison.Ordinal)),
            "메모 제외를 적용하지 못했으면 COM 폴백이 메모 건수를 경고해야 합니다.");
        Expect(!includedWarnings.Any(warning => warning.StartsWith("이 문서에는 메모", StringComparison.Ordinal)),
            "Include Memos가 켜져 있으면 메모 경고를 내지 않아야 합니다.");
        Expect(comIncluded.Contains("실제 한글 메모 내용", StringComparison.Ordinal),
            "Include Memos가 켜지면 COM 폴백이 메모를 포함해야 합니다.");
        Console.WriteLine($"PASS: COM fallback memo reporting test ({workerExecutable})");

        var hancomTableHwp = Path.Combine(fixtureDir, "hancom-generated-table.hwp");
        WriteHancomTableHwp(hancomTableHwp);
        var (comTableText, _) = ReadThroughComFallback(workerExecutable, hancomTableHwp, includeMemos: true);
        var comTableLines = comTableText.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Console.WriteLine("COM fallback table rows: " + string.Join(" | ", comTableLines.Select(line => $"[{line}]")));
        Expect(comTableLines.SequenceEqual(new[]
        {
            "표 앞 문단",
            "R1C1-A R1C1-B R1C2 R1C3",
            "R2C1 R2C2 R2C3",
            "표 뒤 문단",
        }), "COM 폴백은 같은 표 행의 셀을 공백으로 합치고 행 사이만 개행해야 합니다.");
        Console.WriteLine($"PASS: COM fallback table row grouping test ({hancomTableHwp})");

        var hancomNestedTableHwp = Path.Combine(fixtureDir, "hancom-generated-nested-table.hwp");
        WriteHancomNestedTableHwp(hancomNestedTableHwp);
        var (comNestedTableText, _) = ReadThroughComFallback(workerExecutable, hancomNestedTableHwp, includeMemos: true);
        var comNestedTableLines = comNestedTableText.Split(
            '\n',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Console.WriteLine("COM fallback nested-table rows: "
            + string.Join(" | ", comNestedTableLines.Select(line => $"[{line}]")));
        Expect(comNestedTableLines.SequenceEqual(new[]
        {
            "중첩 표 앞 문단",
            "2026.7월 개편 내용",
            "내부1 내부2 내부3",
            "값1 값2 값3",
            "후속 설명",
            "2027.1월 차기 내용",
            "중첩 표 뒤 문단",
        }), "COM 폴백은 중첩 표 전후에서 부모 행을 끊고 내부 표를 독립된 행들로 출력해야 합니다.");
        Console.WriteLine($"PASS: COM fallback nested-table block test ({hancomNestedTableHwp})");

        var pdfFixture = Path.Combine(fixtureDir, "pdf-text-fixture.pdf");
        var pdfChangedFixture = Path.Combine(fixtureDir, "pdf-text-fixture-changed.pdf");
        WritePdfFixture(pdfFixture, changed: false);
        WritePdfFixture(pdfChangedFixture, changed: true);
        var pdfDocument = ReadThroughWorker(workerExecutable, pdfFixture);
        Expect(pdfDocument.Reader == "PDF PdfPig 직접 파서",
            "PDF 파일은 격리 워커의 PdfPig reader를 사용해야 합니다.");
        Expect(pdfDocument.Blocks.Select(block => block.Text).SequenceEqual(new[]
        {
            "PDF heading",
            "Table row A B C",
            "Value 100",
        }), "PDF 단어 좌표를 위에서 아래, 같은 줄은 왼쪽에서 오른쪽 순서로 복원해야 합니다.");
        var pdfChangedDocument = ReadThroughWorker(workerExecutable, pdfChangedFixture);
        var pdfDiff = new DocumentDiffer().Compare(pdfDocument, pdfChangedDocument);
        Expect(pdfDiff.Summary.Modified == 1
            && pdfDiff.Summary.Inserted == 0
            && pdfDiff.Summary.Deleted == 0,
            "PDF 한 줄의 값이 달라지면 해당 비교 행 하나만 수정으로 표시해야 합니다.");
        Console.WriteLine("PDF rows: " + string.Join(" | ", pdfDocument.ComparisonLines()));
        Console.WriteLine($"PASS: PDF PdfPig reading-order test ({pdfFixture})");

        if (Type.GetTypeFromProgID("Word.Application") is null)
        {
            Console.WriteLine("SKIP: Word COM이 등록되어 있지 않아 Word 워커 테스트를 건너뜁니다.");
        }
        else
        {
            var wordFixture = Path.Combine(fixtureDir, "word-com-fixture.docx");
            var wordChangedFixture = Path.Combine(fixtureDir, "word-com-fixture-changed.docx");
            WriteWordFixture(wordFixture, changed: false);
            WriteWordFixture(wordChangedFixture, changed: true);

            var wordDocument = ReadThroughWorker(workerExecutable, wordFixture);
            Expect(wordDocument.Reader == "Word COM 읽기 전용",
                "DOCX 파일은 읽기 전용 Word COM reader를 사용해야 합니다.");
            Expect(wordDocument.Blocks.Count == 3
                && wordDocument.Blocks[0].Kind == DocumentBlockKind.Paragraph
                && wordDocument.Blocks[0].Text == "표 앞 문단"
                && wordDocument.Blocks[1].Kind == DocumentBlockKind.Table
                && wordDocument.Blocks[2].Kind == DocumentBlockKind.Paragraph
                && wordDocument.Blocks[2].Text == "표 뒤 문단",
                "Word 본문과 표의 실제 문서 순서를 보존해야 합니다.");
            var wordRows = wordDocument.Blocks[1].Rows
                ?? throw new InvalidOperationException("Word 표의 행이 없습니다.");
            Expect(wordRows.Count == 2
                && wordRows[0].SequenceEqual(new[] { "R1C1-A R1C1-B", "R1C2", "R1C3" })
                && wordRows[1].SequenceEqual(new[] { "R2C1", "R2C2", "R2C3" }),
                "Word 표는 셀 내부 개행을 공백으로 잇고 같은 행의 셀 경계를 보존해야 합니다.");

            var wordChangedDocument = ReadThroughWorker(workerExecutable, wordChangedFixture);
            var wordDiff = new DocumentDiffer().Compare(wordDocument, wordChangedDocument);
            Expect(wordDiff.Summary.Modified == 1
                && wordDiff.Summary.Inserted == 0
                && wordDiff.Summary.Deleted == 0,
                "Word 표 셀 값 하나가 달라지면 해당 표 행 하나만 수정으로 표시해야 합니다.");
            Console.WriteLine("Word COM rows: " + string.Join(" | ", wordDocument.ComparisonLines()));
            Console.WriteLine($"PASS: Word COM paragraph/table structure test ({wordFixture})");
        }

        if (Type.GetTypeFromProgID("Excel.Application") is null)
        {
            Console.WriteLine("SKIP: Excel COM이 등록되어 있지 않아 Excel 워커 테스트를 건너뜁니다.");
        }
        else
        {
            var excelFixture = Path.Combine(fixtureDir, "excel-multi-sheet-fixture.xlsx");
            WriteExcelFixture(excelFixture);
            var excelDocument = ReadThroughWorker(workerExecutable, excelFixture);
            Expect(excelDocument.Reader == "Excel COM 읽기 전용", "Excel 파일은 읽기 전용 COM reader를 사용해야 합니다.");
            Expect(excelDocument.Blocks.Count == 5, "두 시트와 빈 행으로 나뉜 세 표를 각각 구조화해야 합니다.");
            Expect(excelDocument.Blocks[0].Text == "[시트] 요약", "첫 시트 헤더를 비교 행으로 보존해야 합니다.");
            Expect(excelDocument.Blocks[1].Text == "[표 영역]" && excelDocument.Blocks[2].Text == "[표 영역]",
                "빈 행으로 분리된 표 사이에 번호 없는 안정적인 비교 경계를 둬야 합니다.");
            var firstTableRows = excelDocument.Blocks[1].Rows
                ?? throw new InvalidOperationException("첫 Excel 표의 행이 없습니다.");
            var secondTableRows = excelDocument.Blocks[2].Rows
                ?? throw new InvalidOperationException("두 번째 Excel 표의 행이 없습니다.");
            var hiddenTableRows = excelDocument.Blocks[4].Rows
                ?? throw new InvalidOperationException("숨김 Excel 시트의 행이 없습니다.");
            Expect(firstTableRows.Count == 2
                && firstTableRows[0].SequenceEqual(new[] { "항목", "금액", "", "비고" })
                && firstTableRows[1].SequenceEqual(new[] { "A", "100", "", "정상" }),
                "같은 Excel 행의 셀 경계와 중간 빈 셀을 보존해야 합니다.");
            Expect(secondTableRows.Count == 2
                && secondTableRows[1].SequenceEqual(new[] { "합계", "100" }),
                "완전히 빈 행은 표 블록을 나누고 수식 셀도 사용자가 보는 계산값으로 읽어야 합니다.");
            Expect(excelDocument.Blocks[3].Text == "[시트] 숨김자료 [숨김]"
                && hiddenTableRows.Count == 2,
                "다중 시트와 숨김 상태도 비교 구조에 포함해야 합니다.");
            var excelChangedFixture = Path.Combine(fixtureDir, "excel-multi-sheet-fixture-changed.xlsx");
            WriteExcelFixture(excelChangedFixture, changed: true);
            var excelChangedDocument = ReadThroughWorker(workerExecutable, excelChangedFixture);
            var excelDiff = new DocumentDiffer().Compare(excelDocument, excelChangedDocument);
            Expect(excelDiff.Summary.Modified == 2
                && excelDiff.Summary.Inserted == 0
                && excelDiff.Summary.Deleted == 0,
                "Excel 입력값과 그 결과값이 달라졌으면 값이 바뀐 두 행을 수정으로 표시해야 합니다.");
            Expect(excelDiff.Rows.All(row =>
                    !ContainsStructuralLabel(row.OldText) && !ContainsStructuralLabel(row.NewText)
                    && row.OldFragments.All(fragment => !ContainsStructuralLabel(fragment.Text))
                    && row.NewFragments.All(fragment => !ContainsStructuralLabel(fragment.Text))),
                "Excel 구조 표식은 비교 정렬에만 사용하고 표시 문자열과 인라인 강조에서는 숨겨야 합니다.");
            Expect(excelDiff.Rows.Any(row => row.Presentation == DiffRowPresentationKind.SectionHeader
                    && row.OldText == "요약" && row.NewText == "요약")
                && excelDiff.Rows.Any(row => row.Presentation == DiffRowPresentationKind.Spacer
                    && row.OldText == string.Empty && row.NewText == string.Empty)
                && excelDiff.Rows.Any(row => row.Presentation == DiffRowPresentationKind.TableRow
                    && row.OldText == "항목 | 금액 |  | 비고"),
                "시트명은 헤더로, 표 영역은 여백으로, 표 행은 접두어 없는 셀 값으로 표시해야 합니다.");
            Console.WriteLine("Excel COM rows: " + string.Join(" | ", excelDocument.ComparisonLines()));
            Console.WriteLine($"PASS: Excel COM multi-sheet/table structure test ({excelFixture})");
        }
    }
}

static bool ContainsStructuralLabel(string? text) => text is not null
    && (text.Contains("[시트]", StringComparison.Ordinal)
        || text.Contains("[표 영역]", StringComparison.Ordinal)
        || text.Contains("[표]", StringComparison.Ordinal));

static void WriteHwpx(string path, params string[] paragraphs)
{
    if (File.Exists(path)) File.Delete(path);
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    var entry = archive.CreateEntry("Contents/section0.xml");
    using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
    writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?><hp:sec xmlns:hp=\"urn:hancom:office:hwpx\">");
    foreach (var paragraph in paragraphs)
        writer.Write($"<hp:p><hp:run><hp:t>{System.Security.SecurityElement.Escape(paragraph)}</hp:t></hp:run></hp:p>");
    writer.Write("</hp:sec>");
}

static void WriteMemoHwpx(string path, string bodyBefore, string memoText, string bodyAfter)
{
    if (File.Exists(path)) File.Delete(path);
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    var entry = archive.CreateEntry("Contents/section0.xml");
    using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
    writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?><hp:sec xmlns:hp=\"urn:hancom:office:hwpx\">");
    writer.Write("<hp:p><hp:run><hp:t>");
    writer.Write(System.Security.SecurityElement.Escape(bodyBefore));
    writer.Write("</hp:t><hp:ctrl><hp:fieldBegin type=\"MEMO\"><hp:subList><hp:p styleIDRef=\"16\"><hp:run><hp:t>");
    writer.Write(System.Security.SecurityElement.Escape(memoText));
    writer.Write("</hp:t></hp:run></hp:p></hp:subList></hp:fieldBegin></hp:ctrl><hp:t>");
    writer.Write(System.Security.SecurityElement.Escape(bodyAfter));
    writer.Write("</hp:t></hp:run></hp:p></hp:sec>");
}

static void WriteExcelFixture(string path, bool changed = false)
{
    if (File.Exists(path)) File.Delete(path);
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    WriteZipText(archive, "[Content_Types].xml", """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
          <Default Extension="xml" ContentType="application/xml"/>
          <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
          <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
          <Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
        </Types>
        """);
    WriteZipText(archive, "_rels/.rels", """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
        </Relationships>
        """);
    WriteZipText(archive, "xl/workbook.xml", """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
          <sheets>
            <sheet name="요약" sheetId="1" r:id="rId1"/>
            <sheet name="숨김자료" sheetId="2" state="hidden" r:id="rId2"/>
          </sheets>
        </workbook>
        """);
    WriteZipText(archive, "xl/_rels/workbook.xml.rels", """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
          <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/>
        </Relationships>
        """);
    var changedValue = changed ? 125 : 100;
    WriteZipText(archive, "xl/worksheets/sheet1.xml", $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
          <row r="1"><c r="A1" t="inlineStr"><is><t>항목</t></is></c><c r="B1" t="inlineStr"><is><t>금액</t></is></c><c r="D1" t="inlineStr"><is><t>비고</t></is></c></row>
          <row r="2"><c r="A2" t="inlineStr"><is><t>A</t></is></c><c r="B2"><v>{changedValue}</v></c><c r="D2" t="inlineStr"><is><t>정상</t></is></c></row>
          <row r="4"><c r="A4" t="inlineStr"><is><t>구분</t></is></c><c r="B4" t="inlineStr"><is><t>값</t></is></c></row>
          <row r="5"><c r="A5" t="inlineStr"><is><t>합계</t></is></c><c r="B5"><f>SUM(B2)</f><v>{changedValue}</v></c></row>
        </sheetData></worksheet>
        """);
    WriteZipText(archive, "xl/worksheets/sheet2.xml", """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
          <row r="1"><c r="A1" t="inlineStr"><is><t>내부</t></is></c><c r="B1" t="inlineStr"><is><t>수치</t></is></c></row>
          <row r="2"><c r="A2" t="inlineStr"><is><t>X</t></is></c><c r="B2"><v>7</v></c></row>
        </sheetData></worksheet>
        """);
}

static void WritePdfFixture(string path, bool changed)
{
    if (File.Exists(path)) File.Delete(path);
    var value = changed ? "Value 125" : "Value 100";
    var content = $"BT\n/F1 12 Tf\n72 740 Td\n(PDF heading) Tj\n0 -24 Td\n(Table row A B C) Tj\n0 -24 Td\n({value}) Tj\nET\n";
    var objects = new[]
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
        $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
    };

    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
    writer.Write(Encoding.ASCII.GetBytes("%PDF-1.4\n%Hdiff\n"));
    var offsets = new List<long> { 0 };
    for (var index = 0; index < objects.Length; index++)
    {
        offsets.Add(stream.Position);
        writer.Write(Encoding.ASCII.GetBytes($"{index + 1} 0 obj\n{objects[index]}\nendobj\n"));
    }

    var xrefOffset = stream.Position;
    writer.Write(Encoding.ASCII.GetBytes($"xref\n0 {objects.Length + 1}\n"));
    writer.Write(Encoding.ASCII.GetBytes("0000000000 65535 f \n"));
    foreach (var offset in offsets.Skip(1))
        writer.Write(Encoding.ASCII.GetBytes($"{offset:0000000000} 00000 n \n"));
    writer.Write(Encoding.ASCII.GetBytes(
        $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n"));
}

[SupportedOSPlatform("windows")]
static void WriteWordFixture(string path, bool changed)
{
    if (File.Exists(path)) File.Delete(path);
    var type = Type.GetTypeFromProgID("Word.Application")
        ?? throw new InvalidOperationException("Word COM이 등록되어 있지 않습니다.");
    dynamic? word = null;
    object? documentObject = null;
    object? selectionObject = null;
    object? tableObject = null;
    try
    {
        word = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Word COM 생성 실패");
        word.Visible = false;
        word.DisplayAlerts = 0;
        word.AutomationSecurity = 3;
        documentObject = word.Documents.Add();
        dynamic document = documentObject;
        selectionObject = word.Selection;
        dynamic selection = selectionObject;
        selection.TypeText("표 앞 문단");
        selection.TypeParagraph();
        tableObject = document.Tables.Add(selection.Range, 2, 3);
        dynamic table = tableObject;
        SetWordCell(table, 1, 1, "R1C1-A\vR1C1-B");
        SetWordCell(table, 1, 2, "R1C2");
        SetWordCell(table, 1, 3, "R1C3");
        SetWordCell(table, 2, 1, "R2C1");
        SetWordCell(table, 2, 2, changed ? "R2C2 변경" : "R2C2");
        SetWordCell(table, 2, 3, "R2C3");
        selection.SetRange(table.Range.End, table.Range.End);
        selection.TypeText("표 뒤 문단");
        document.SaveAs2(Path.GetFullPath(path), 16);
    }
    finally
    {
        if (documentObject is not null)
        {
            try { ((dynamic)documentObject).Close(0); } catch { }
        }
        if (word is not null)
        {
            try { word.Quit(0); } catch { }
        }
        ReleaseTestComObject(tableObject);
        ReleaseTestComObject(selectionObject);
        ReleaseTestComObject(documentObject);
        ReleaseTestComObject(word);
    }
}

static void SetWordCell(dynamic table, int row, int column, string text)
{
    object? cellObject = null;
    object? rangeObject = null;
    try
    {
        cellObject = table.Cell(row, column);
        dynamic cell = cellObject;
        rangeObject = cell.Range;
        ((dynamic)rangeObject).Text = text;
    }
    finally
    {
        ReleaseTestComObject(rangeObject);
        ReleaseTestComObject(cellObject);
    }
}

static void ReleaseTestComObject(object? value)
{
    if (value is null || !Marshal.IsComObject(value)) return;
    try { Marshal.FinalReleaseComObject(value); } catch { }
}

static void WriteZipText(ZipArchive archive, string path, string text)
{
    var entry = archive.CreateEntry(path);
    using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
    writer.Write(text);
}

static byte[] ParaTextPayload(params byte[][] parts)
{
    var length = parts.Sum(part => part.Length);
    var result = new byte[length];
    var offset = 0;
    foreach (var part in parts)
    {
        Buffer.BlockCopy(part, 0, result, offset, part.Length);
        offset += part.Length;
    }
    return result;
}

static byte[] Utf16(string text) => Encoding.Unicode.GetBytes(text);

static byte[] ExtendedControl(ushort code, string sevenPayloadCharacters)
{
    if (sevenPayloadCharacters.Length != 7)
        throw new ArgumentException("8-WCHAR 제어문자 테스트 payload는 후속 문자가 정확히 7개여야 합니다.", nameof(sevenPayloadCharacters));

    var result = new byte[8 * sizeof(char)];
    BitConverter.GetBytes(code).CopyTo(result, 0);
    Encoding.Unicode.GetBytes(sevenPayloadCharacters).CopyTo(result, sizeof(char));
    return result;
}

static byte[] CharControl(ushort code) => BitConverter.GetBytes(code);

static byte[] TruncatedExtendedControl(ushort code)
{
    var result = new byte[2 * sizeof(char)];
    BitConverter.GetBytes(code).CopyTo(result, 0);
    Encoding.Unicode.GetBytes("누").CopyTo(result, sizeof(char));
    return result;
}

[SupportedOSPlatform("windows")]
static void WriteHancomHwp(string path, params string[] paragraphs)
{
    var type = Type.GetTypeFromProgID("HWPFrame.HwpObject")
        ?? throw new InvalidOperationException("한글 COM이 등록되어 있지 않습니다.");
    dynamic? hwp = null;
    try
    {
        hwp = Activator.CreateInstance(type) ?? throw new InvalidOperationException("한글 COM 생성 실패");
        try { hwp.RegisterModule("FilePathCheckDLL", "FilePathCheckerModule"); } catch { }
        try { hwp.XHwpWindows.Item(0).Visible = false; } catch { }
        hwp.Run("FileNew");
        foreach (var paragraph in paragraphs)
        {
            var action = hwp.CreateAction("InsertText");
            var set = action.CreateSet();
            action.GetDefault(set);
            set.SetItem("Text", paragraph);
            action.Execute(set);
            hwp.HAction.Run("BreakPara");
        }
        var saved = hwp.SaveAs(Path.GetFullPath(path), "HWP", "");
        if (saved is bool ok && !ok) throw new InvalidOperationException("한글 COM SaveAs(HWP)가 false를 반환했습니다.");
        if (!File.Exists(path)) throw new InvalidOperationException("한글 COM이 HWP 파일을 만들지 않았습니다.");
    }
    finally
    {
        if (hwp is not null)
        {
            try { hwp.XHwpDocuments.Item(0).SetModified(false); } catch { }
            try { hwp.Run("FileClose"); } catch { }
            try { hwp.Quit(); } catch { }
            try { Marshal.FinalReleaseComObject(hwp); } catch { }
        }
    }
}

[SupportedOSPlatform("windows")]
static void WriteHancomMemoHwp(string path, string bodyBefore, string memoText, string bodyAfter)
{
    var type = Type.GetTypeFromProgID("HWPFrame.HwpObject")
        ?? throw new InvalidOperationException("한글 COM이 등록되어 있지 않습니다.");
    dynamic? hwp = null;
    try
    {
        hwp = Activator.CreateInstance(type) ?? throw new InvalidOperationException("한글 COM 생성 실패");
        try { hwp.RegisterModule("FilePathCheckDLL", "FilePathCheckerModule"); } catch { }
        try { hwp.XHwpWindows.Item(0).Visible = false; } catch { }
        hwp.Run("FileNew");
        InsertHancomText(hwp, bodyBefore);
        if (!(bool)hwp.HAction.Run("InsertFieldMemo"))
            throw new InvalidOperationException("한글이 InsertFieldMemo 액션을 실행하지 못했습니다.");
        InsertHancomText(hwp, memoText);
        if (!(bool)hwp.HAction.Run("CloseEx"))
            throw new InvalidOperationException("한글 메모 하위 목록에서 본문으로 돌아오지 못했습니다.");
        hwp.HAction.Run("BreakPara");
        InsertHancomText(hwp, bodyAfter);
        var saved = hwp.SaveAs(Path.GetFullPath(path), "HWP", "");
        if (saved is bool ok && !ok) throw new InvalidOperationException("한글 COM SaveAs(HWP)가 false를 반환했습니다.");
        if (!File.Exists(path)) throw new InvalidOperationException("한글 COM이 메모 HWP 파일을 만들지 않았습니다.");
    }
    finally
    {
        if (hwp is not null)
        {
            try { hwp.XHwpDocuments.Item(0).SetModified(false); } catch { }
            try { hwp.Run("FileClose"); } catch { }
            try { hwp.Quit(); } catch { }
            try { Marshal.FinalReleaseComObject(hwp); } catch { }
        }
    }
}

[SupportedOSPlatform("windows")]
static void WriteHancomTableHwp(string path)
{
    var type = Type.GetTypeFromProgID("HWPFrame.HwpObject")
        ?? throw new InvalidOperationException("한글 COM이 등록되어 있지 않습니다.");
    dynamic? hwp = null;
    try
    {
        hwp = Activator.CreateInstance(type) ?? throw new InvalidOperationException("한글 COM 생성 실패");
        try { hwp.RegisterModule("FilePathCheckDLL", "FilePathCheckerModule"); } catch { }
        try { hwp.XHwpWindows.Item(0).Visible = false; } catch { }
        hwp.Run("FileNew");
        InsertHancomText(hwp, "표 앞 문단");
        hwp.HAction.Run("BreakPara");

        var tableAction = hwp.CreateAction("TableCreate");
        var tableSet = tableAction.CreateSet();
        tableAction.GetDefault(tableSet);
        tableSet.SetItem("Rows", 2);
        tableSet.SetItem("Cols", 3);
        if (!(bool)tableAction.Execute(tableSet))
            throw new InvalidOperationException("한글이 2행 3열 시험 표를 만들지 못했습니다.");

        var labels = new[] { "R1C1-A", "R1C2", "R1C3", "R2C1", "R2C2", "R2C3" };
        for (var index = 0; index < labels.Length; index++)
        {
            InsertHancomText(hwp, labels[index]);
            if (index == 0)
            {
                hwp.HAction.Run("BreakPara");
                InsertHancomText(hwp, "R1C1-B");
            }
            if (index < labels.Length - 1) hwp.HAction.Run("TableRightCell");
        }

        hwp.HAction.Run("CloseEx");
        hwp.HAction.Run("BreakPara");
        InsertHancomText(hwp, "표 뒤 문단");
        var saved = hwp.SaveAs(Path.GetFullPath(path), "HWP", "");
        if (saved is bool ok && !ok) throw new InvalidOperationException("한글 COM SaveAs(HWP)가 false를 반환했습니다.");
        if (!File.Exists(path)) throw new InvalidOperationException("한글 COM이 표 시험 HWP 파일을 만들지 않았습니다.");
    }
    finally
    {
        if (hwp is not null)
        {
            try { hwp.XHwpDocuments.Item(0).SetModified(false); } catch { }
            try { hwp.Run("FileClose"); } catch { }
            try { hwp.Quit(); } catch { }
            try { Marshal.FinalReleaseComObject(hwp); } catch { }
        }
    }
}

[SupportedOSPlatform("windows")]
static void WriteHancomNestedTableHwp(string path)
{
    var type = Type.GetTypeFromProgID("HWPFrame.HwpObject")
        ?? throw new InvalidOperationException("한글 COM이 등록되어 있지 않습니다.");
    dynamic? hwp = null;
    try
    {
        hwp = Activator.CreateInstance(type) ?? throw new InvalidOperationException("한글 COM 생성 실패");
        try { hwp.RegisterModule("FilePathCheckDLL", "FilePathCheckerModule"); } catch { }
        try { hwp.XHwpWindows.Item(0).Visible = false; } catch { }
        hwp.Run("FileNew");
        InsertHancomText(hwp, "중첩 표 앞 문단");
        hwp.HAction.Run("BreakPara");

        CreateHancomTable(hwp, rows: 2, columns: 2, "바깥 2행 2열 시험 표");
        InsertHancomText(hwp, "2026.7월");
        hwp.HAction.Run("TableRightCell");
        InsertHancomText(hwp, "개편 내용");
        hwp.HAction.Run("BreakPara");

        CreateHancomTable(hwp, rows: 2, columns: 3, "안쪽 2행 3열 시험 표");
        var innerLabels = new[] { "내부1", "내부2", "내부3", "값1", "값2", "값3" };
        for (var index = 0; index < innerLabels.Length; index++)
        {
            InsertHancomText(hwp, innerLabels[index]);
            if (index < innerLabels.Length - 1) hwp.HAction.Run("TableRightCell");
        }

        hwp.HAction.Run("CloseEx");
        hwp.HAction.Run("BreakPara");
        InsertHancomText(hwp, "후속 설명");
        hwp.HAction.Run("TableRightCell");
        InsertHancomText(hwp, "2027.1월");
        hwp.HAction.Run("TableRightCell");
        InsertHancomText(hwp, "차기 내용");

        hwp.HAction.Run("CloseEx");
        hwp.HAction.Run("BreakPara");
        InsertHancomText(hwp, "중첩 표 뒤 문단");
        var saved = hwp.SaveAs(Path.GetFullPath(path), "HWP", "");
        if (saved is bool ok && !ok) throw new InvalidOperationException("한글 COM SaveAs(HWP)가 false를 반환했습니다.");
        if (!File.Exists(path)) throw new InvalidOperationException("한글 COM이 중첩 표 시험 HWP 파일을 만들지 않았습니다.");
    }
    finally
    {
        if (hwp is not null)
        {
            try { hwp.XHwpDocuments.Item(0).SetModified(false); } catch { }
            try { hwp.Run("FileClose"); } catch { }
            try { hwp.Quit(); } catch { }
            try { Marshal.FinalReleaseComObject(hwp); } catch { }
        }
    }
}

[SupportedOSPlatform("windows")]
static void CreateHancomTable(dynamic hwp, int rows, int columns, string description)
{
    var action = hwp.CreateAction("TableCreate");
    var set = action.CreateSet();
    action.GetDefault(set);
    set.SetItem("Rows", rows);
    set.SetItem("Cols", columns);
    if (!(bool)action.Execute(set))
        throw new InvalidOperationException($"한글이 {description}를 만들지 못했습니다.");
}

[SupportedOSPlatform("windows")]
static void InsertHancomText(dynamic hwp, string text)
{
    var action = hwp.CreateAction("InsertText");
    var set = action.CreateSet();
    action.GetDefault(set);
    set.SetItem("Text", text);
    action.Execute(set);
}

static string? FindWorkerExecutable(string repositoryRoot)
{
    // The target framework moniker changes between networks, so search the
    // build output instead of hard-coding a path.
    var binDirectory = Path.Combine(repositoryRoot, "src", "Hdiff.UI", "bin");
    if (!Directory.Exists(binDirectory)) return null;
    return Directory.EnumerateFiles(binDirectory, "Hdiff.exe", SearchOption.AllDirectories)
        .OrderByDescending(File.GetLastWriteTimeUtc)
        .FirstOrDefault();
}

[SupportedOSPlatform("windows")]
static (string Text, List<string> Warnings) ReadThroughComFallback(string workerExecutable, string documentPath, bool includeMemos)
{
    var request = JsonSerializer.Serialize(new
    {
        Path = Path.GetFullPath(documentPath),
        AllowComFallback = true,
        IncludeMemos = includeMemos,
        ForceComFallback = true,
    });

    using var process = Process.Start(new ProcessStartInfo(workerExecutable, "--worker")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        StandardOutputEncoding = new UTF8Encoding(false),
    }) ?? throw new InvalidOperationException("Hdiff 워커 프로세스를 시작하지 못했습니다.");

    using (var writer = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)))
        writer.WriteLine(request);

    var response = process.StandardOutput.ReadLine();
    process.WaitForExit(180_000);
    if (string.IsNullOrWhiteSpace(response))
        throw new InvalidOperationException("Hdiff 워커가 응답하지 않았습니다.");

    using var payload = JsonDocument.Parse(response);
    var root = payload.RootElement;
    if (!root.GetProperty("Ok").GetBoolean())
        throw new InvalidOperationException("COM 폴백 실패: " + root.GetProperty("Error").GetString());

    var warnings = new List<string>();
    foreach (var warning in root.GetProperty("Document").GetProperty("Warnings").EnumerateArray())
    {
        warnings.Add(warning.GetString() ?? string.Empty);
        Console.WriteLine($"  COM fallback warning: {warning.GetString()}");
    }

    var text = string.Join("\n", root.GetProperty("Document").GetProperty("Blocks")
        .EnumerateArray()
        .Select(block => block.GetProperty("Text").GetString()));
    return (text, warnings);
}

static ParsedDocument ReadThroughWorker(string workerExecutable, string documentPath)
{
    var request = JsonSerializer.Serialize(new
    {
        Path = Path.GetFullPath(documentPath),
        AllowComFallback = true,
        IncludeMemos = false,
        ForceComFallback = false,
    });

    using var process = Process.Start(new ProcessStartInfo(workerExecutable, "--worker")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        StandardOutputEncoding = new UTF8Encoding(false),
    }) ?? throw new InvalidOperationException("Hdiff 워커 프로세스를 시작하지 못했습니다.");

    using (var writer = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)))
        writer.WriteLine(request);

    var responseTask = process.StandardOutput.ReadLineAsync();
    if (!responseTask.Wait(300_000))
    {
        try { process.Kill(entireProcessTree: true); } catch { }
        throw new TimeoutException("문서 워커 회귀 테스트가 300초 안에 응답하지 않았습니다.");
    }
    var response = responseTask.GetAwaiter().GetResult();
    process.WaitForExit(10_000);
    if (string.IsNullOrWhiteSpace(response)) throw new InvalidOperationException("Hdiff 워커가 응답하지 않았습니다.");

    using var payload = JsonDocument.Parse(response);
    var root = payload.RootElement;
    if (!root.GetProperty("Ok").GetBoolean())
        throw new InvalidOperationException("문서 워커 실패: " + root.GetProperty("Error").GetString());
    return JsonSerializer.Deserialize<ParsedDocument>(root.GetProperty("Document").GetRawText())
        ?? throw new InvalidOperationException("문서 워커의 ParsedDocument를 해석하지 못했습니다.");
}

static string FindRepositoryRoot()
{
    var current = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (current is not null)
    {
        if (File.Exists(Path.Combine(current.FullName, "Hdiff.sln"))) return current.FullName;
        current = current.Parent;
    }
    throw new InvalidOperationException("Hdiff.sln을 찾지 못했습니다.");
}

static void Expect(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
