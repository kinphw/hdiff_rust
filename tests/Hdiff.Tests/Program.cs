using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Hdiff.Core.Diff;
using Hdiff.Core.Documents;
using Hdiff.Core.Hwp5;

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
File.WriteAllText(Path.Combine(fixtureDir, "before-after.diff.txt"), diff.ToGitLog(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

var hwpx = Path.Combine(fixtureDir, "sample.hwpx");
WriteHwpx(hwpx, "제2조 적용", "HWPX도 한글 없이 읽는다.");
var parsedHwpx = reader.Read(hwpx);
Expect(parsedHwpx.Reader == "HWPX 직접 파서", "생성한 HWPX를 직접 읽어야 합니다.");
Expect(parsedHwpx.Blocks.Select(x => x.Text).SequenceEqual(new[] { "제2조 적용", "HWPX도 한글 없이 읽는다." }), "HWPX 문단이 원문과 같아야 합니다.");

var spacedHwpx = Path.Combine(fixtureDir, "spaced-sample.hwpx");
WriteHwpx(spacedHwpx, "제2조 적용", "", "제3조 시행");
var parsedSpacedHwpx = reader.Read(spacedHwpx);
Expect(parsedSpacedHwpx.Blocks.Select(x => x.Text).SequenceEqual(new[] { "제2조 적용", "", "제3조 시행" }), "HWPX의 빈 문단은 표시 옵션을 위해 보존해야 합니다.");

Console.WriteLine("PASS: direct HWP5/HWPX reader and diff tests");
Console.WriteLine($"Generated HWP5 fixtures: {beforeHwp}");
Console.WriteLine($"Diff log: {Path.Combine(fixtureDir, "before-after.diff.txt")}");

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
}

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
