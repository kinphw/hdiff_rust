using System.Globalization;
using System.Net;
using System.Text;
using Hdiff.Core.Diff;
using Hdiff.Core.Review;

namespace Hdiff.Core.Export;

public enum HtmlDiffTheme
{
    Light,
    RustDark,
}

public sealed record HtmlDiffExportOptions(
    int FontSizePixels,
    bool WrapLongLines,
    bool ShowRowSeparators,
    HtmlDiffTheme Theme,
    string AppVersion,
    DateTimeOffset GeneratedAt);

/// <summary>
/// Creates a self-contained, offline HTML snapshot of a completed comparison.
/// It deliberately has no external scripts, fonts, stylesheets, or network calls.
/// </summary>
public static class HtmlDiffExporter
{
    /// <param name="memos">
    /// Review memos of the comparison. They are written into the same file so a
    /// reviewer can share the result and the notes as one attachment.
    /// </param>
    public static string Create(DocumentDiff diff, HtmlDiffExportOptions options, IReadOnlyList<DiffMemo>? memos = null)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(options);
        if (options.FontSizePixels is < 8 or > 32)
            throw new ArgumentOutOfRangeException(nameof(options), "Font size must be between 8px and 32px.");

        var notes = NumberMemos(memos, diff.Rows.Count);
        var notesByRow = notes.Where(note => !note.Memo.Anchor.IsOrphaned)
            .GroupBy(note => note.Memo.Anchor.RowIndex)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<NumberedMemo>)group.ToArray());
        var oldName = FileName(diff.OldDocument.SourcePath);
        var newName = FileName(diff.NewDocument.SourcePath);
        var title = $"Hdiff — {oldName} ↔ {newName}";
        var initialTheme = options.Theme == HtmlDiffTheme.RustDark ? "dark" : "light";
        var wrapClass = options.WrapLongLines ? "wrap" : "nowrap";
        var separatorClass = options.ShowRowSeparators ? "separators" : string.Empty;
        // Memos are the reason the file was shared, so the panel starts open.
        var memoClass = notes.Count > 0 ? "with-memos" : string.Empty;
        var builder = new StringBuilder(Math.Max(32_768,
            diff.Rows.Sum(row => (row.OldText?.Length ?? 0) + (row.NewText?.Length ?? 0)) * 2));

        builder.Append("<!doctype html><html lang=\"ko\" data-export-name=\"")
            .Append(Encode(ExportBaseName(oldName, newName)))
            .Append("\" data-reply-round=\"0\" data-theme=\"")
            .Append(initialTheme)
            .Append("\" class=\"")
            .Append(wrapClass).Append(' ').Append(separatorClass).Append(' ').Append(memoClass)
            .Append("\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; img-src data:; connect-src 'none'\">")
            .Append("<title>").Append(Encode(title)).AppendLine("</title>");
        builder.AppendLine(Styles);
        builder.Append("</head><body style=\"--document-font-size:")
            .Append(options.FontSizePixels.ToString(CultureInfo.InvariantCulture))
            .AppendLine("px\"><main class=\"app-shell\">");

        AppendTopBar(builder, diff, options, notes.Count);

        builder.AppendLine("<div class=\"workspace\">");
        AppendDiffStage(builder, diff, oldName, newName, notesByRow);
        AppendMemoPanel(builder, diff, notes);
        builder.AppendLine("</div></main>");
        builder.AppendLine(Script);
        builder.AppendLine("</body></html>");
        return builder.ToString();
    }

    /// <summary>
    /// Gives every memo the reading-order number shown both on the row flag and
    /// on the panel card. Memos whose paragraph is gone keep a number so the
    /// reader can still see that a note exists.
    /// </summary>
    private static IReadOnlyList<NumberedMemo> NumberMemos(IReadOnlyList<DiffMemo>? memos, int rowCount)
    {
        if (memos is null || memos.Count == 0) return Array.Empty<NumberedMemo>();
        return memos
            .OrderBy(memo => memo.Anchor.IsOrphaned || memo.Anchor.RowIndex >= rowCount ? int.MaxValue : memo.Anchor.RowIndex)
            .ThenBy(memo => memo.CreatedAt)
            .Select((memo, index) => new NumberedMemo(
                index + 1,
                memo.Anchor.RowIndex >= rowCount ? memo with { Anchor = memo.Anchor with { RowIndex = DiffMemoAnchor.OrphanedRowIndex } } : memo))
            .ToArray();
    }

    private static void AppendTopBar(StringBuilder builder, DocumentDiff diff, HtmlDiffExportOptions options, int memoCount)
    {
        builder.AppendLine("<header class=\"top-bar\">")
            .AppendLine("<div class=\"brand\"><span class=\"brand-mark\">HD</span><strong>Hdiff 비교 결과</strong></div>")
            .AppendLine("<div class=\"compact-summary\">")
            .Append("<span class=\"summary-chip modified\">수정 ").Append(diff.Summary.Modified).AppendLine("</span>")
            .Append("<span class=\"summary-chip inserted\">추가 ").Append(diff.Summary.Inserted).AppendLine("</span>")
            .Append("<span class=\"summary-chip deleted\">삭제 ").Append(diff.Summary.Deleted).AppendLine("</span>");
        // Always offered: a reader with no memos to read may still have one to write.
        builder.Append("<button type=\"button\" class=\"summary-chip memo-toggle\" id=\"memo-toggle\" aria-pressed=\"")
            .Append(memoCount > 0 ? "true" : "false").Append("\">검토 메모")
            .Append(memoCount > 0 ? " " + memoCount.ToString(CultureInfo.InvariantCulture) : string.Empty)
            .AppendLine("</button>");
        builder.AppendLine("</div>")
            .Append("<div class=\"export-meta\"><span>Hdiff v").Append(Encode(options.AppVersion))
            .Append(" · ").Append(Encode(options.GeneratedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)))
            .AppendLine("</span></div></header>");
    }

    private static void AppendDiffStage(
        StringBuilder builder,
        DocumentDiff diff,
        string oldName,
        string newName,
        IReadOnlyDictionary<int, IReadOnlyList<NumberedMemo>> notesByRow)
    {
        builder.AppendLine("<section class=\"diff-stage\" aria-label=\"문서 비교 결과\">");
        AppendPaneHeader(builder, oldSide: true, oldName);
        builder.AppendLine("<div class=\"center-divider\" aria-hidden=\"true\"></div>");
        AppendPaneHeader(builder, oldSide: false, newName);
        builder.AppendLine("<div class=\"shared-scroll\" id=\"diff-scroll\" tabindex=\"0\">")
            .AppendLine("<div class=\"diff-pairs\" id=\"diff-pairs\">");

        for (var rowIndex = 0; rowIndex < diff.Rows.Count; rowIndex++)
            AppendPairRow(builder, diff.Rows[rowIndex], rowIndex, notesByRow.GetValueOrDefault(rowIndex));

        builder.AppendLine("</div></div>")
            .AppendLine("<aside class=\"overview old-overview\" data-side=\"old\" aria-label=\"변경 전 위치 지도\">")
            .AppendLine("<canvas class=\"overview-canvas\" aria-hidden=\"true\"></canvas>")
            .AppendLine("<canvas class=\"overview-signal-canvas\" aria-hidden=\"true\"></canvas>")
            .AppendLine("<div class=\"overview-viewport\"></div></aside>")
            .AppendLine("<aside class=\"overview new-overview\" data-side=\"new\" aria-label=\"변경 후 위치 지도\">")
            .AppendLine("<canvas class=\"overview-canvas\" aria-hidden=\"true\"></canvas>")
            .AppendLine("<canvas class=\"overview-signal-canvas\" aria-hidden=\"true\"></canvas>")
            .AppendLine("<div class=\"overview-viewport\"></div></aside>")
            .AppendLine("<button type=\"button\" class=\"memo-add\" id=\"memo-add\" title=\"이 행에 검토 메모 추가\" aria-label=\"이 행에 검토 메모 추가\" hidden>+</button>")
            .AppendLine("</section>");
    }

    private static void AppendPaneHeader(StringBuilder builder, bool oldSide, string fileName)
    {
        var side = oldSide ? "old" : "new";
        var caption = oldSide ? "변경 전" : "변경 후";
        builder.Append("<header class=\"pane-header ").Append(side).Append("-header\">")
            .Append(caption).Append(" <span>· ").Append(Encode(fileName)).AppendLine("</span></header>");
    }

    private static void AppendPairRow(StringBuilder builder, DiffRow row, int rowIndex, IReadOnlyList<NumberedMemo>? notes)
    {
        var presentation = row.Presentation switch
        {
            DiffRowPresentationKind.SectionHeader => " section-header",
            DiffRowPresentationKind.Spacer => " structural-spacer",
            DiffRowPresentationKind.TableRow => " table-row",
            _ => string.Empty,
        };
        // A memo is written on one column and stays there: 변경 전 문장에 대한
        // 지적과 변경 후 문장에 대한 지적은 서로 다른 이야기이기 때문.
        var oldNotes = notes?.Where(note => note.Memo.Anchor.Side == DiffMemoSide.Old).ToArray();
        var newNotes = notes?.Where(note => note.Memo.Anchor.Side == DiffMemoSide.New).ToArray();
        builder.Append("<div class=\"diff-pair").Append(presentation);
        if (notes is { Count: > 0 }) builder.Append(" has-memo");
        builder.Append("\" data-row=\"").Append(rowIndex).AppendLine("\">");
        AppendRow(builder, row, oldSide: true, oldNotes);
        builder.AppendLine("<div class=\"pair-divider\" aria-hidden=\"true\"></div>");
        AppendRow(builder, row, oldSide: false, newNotes);
        builder.AppendLine("</div>");
    }

    private static void AppendRow(StringBuilder builder, DiffRow row, bool oldSide, IReadOnlyList<NumberedMemo>? notes)
    {
        var side = oldSide ? "old" : "new";
        var text = oldSide ? row.OldText : row.NewText;
        var line = oldSide ? row.OldLine : row.NewLine;
        var fragments = oldSide ? row.OldFragments : row.NewFragments;
        var imaginary = text is null;
        var kind = row.Kind.ToString().ToLowerInvariant();
        var marker = GetMarker(row.Kind, oldSide, imaginary);

        builder.Append("<div class=\"diff-row ").Append(side).Append("-side kind-").Append(kind);
        if (imaginary) builder.Append(" imaginary");
        builder.AppendLine("\">")
            .Append("<div class=\"gutter\"><span class=\"marker\">").Append(marker)
            .Append("</span><span class=\"line-number\">").Append(line?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)
            .Append("</span></div><div class=\"line-text\">");

        if (fragments.Count == 0 && text is not null)
        {
            builder.Append("<span>").Append(Encode(DisplayText(text))).Append("</span>");
        }
        else
        {
            foreach (var fragment in fragments)
            {
                var cssClass = fragment.Kind switch
                {
                    InlineDiffFragmentKind.Removed => "removed",
                    InlineDiffFragmentKind.Added => "added",
                    _ => "unchanged",
                };
                builder.Append("<span class=\"fragment ").Append(cssClass).Append("\">")
                    .Append(Encode(DisplayText(fragment.Text))).Append("</span>");
            }
        }

        if (notes is not null)
        {
            foreach (var note in notes)
            {
                builder.Append("<button type=\"button\" class=\"memo-flag\" data-memo=\"memo-")
                    .Append(note.Number).Append("\" title=\"")
                    .Append(Encode(FlagTooltip(note))).Append("\">")
                    .Append(note.Number).Append("</button>");
            }
        }

        builder.AppendLine("</div></div>");
    }

    private static string FlagTooltip(NumberedMemo note) =>
        $"검토 메모 {note.Number} · {note.Memo.Author} · {Shorten(note.Memo.Text, 60)}";

    private static void AppendMemoPanel(StringBuilder builder, DocumentDiff diff, IReadOnlyList<NumberedMemo> notes)
    {
        builder.AppendLine("<aside class=\"memo-panel\" id=\"memo-panel\" aria-label=\"검토 메모\">")
            .AppendLine("<header class=\"memo-panel-head\">")
            .Append("<div class=\"memo-panel-title\"><strong>검토 메모</strong><span class=\"memo-panel-count\" id=\"memo-count\">")
            .Append(notes.Count)
            .AppendLine("</span><span class=\"memo-unsaved\" id=\"memo-unsaved\" hidden>저장 안 됨</span><button type=\"button\" class=\"memo-panel-close\" id=\"memo-close\" title=\"메모 접기\" aria-label=\"메모 접기\">×</button></div>")
            .AppendLine("<div class=\"memo-panel-tools\">")
            .AppendLine("<input id=\"reviewer-name\" type=\"text\" value=\"\" maxlength=\"40\" placeholder=\"내 이름 (메모·회신 작성자)\" aria-label=\"메모와 회신 작성자 이름\">")
            .AppendLine("<button type=\"button\" id=\"memo-save\">회신 저장</button></div>")
            .AppendLine("<p class=\"memo-panel-hint\">메모와 회신은 여러 개를 먼저 다 쓴 뒤 <b>회신 저장</b>을 한 번만 누르면 됩니다. 그때 저장 위치를 고르면 그 파일에 담기고, 그 파일을 그대로 회신하면 됩니다. 새 메모는 비교 행 오른쪽의 <b>+</b> 로 답니다.</p>")
            .AppendLine("<p class=\"memo-saved\" id=\"memo-saved\" hidden><span id=\"memo-saved-text\"></span><button type=\"button\" class=\"memo-relocate\" id=\"memo-relocate\" hidden>위치 변경</button></p>")
            .AppendLine("</header>")
            .AppendLine("<div class=\"memo-list\" id=\"memo-list\">")
            .Append("<p class=\"memo-empty\" id=\"memo-empty\"")
            .Append(notes.Count > 0 ? " hidden" : string.Empty)
            .AppendLine(">아직 검토 메모가 없습니다. 비교 행 위에 마우스를 올리면 오른쪽에 나타나는 <b>+</b> 를 눌러 메모를 답니다.</p>");

        foreach (var note in notes)
        {
            var memo = note.Memo;
            var orphaned = memo.Anchor.IsOrphaned;
            var row = orphaned ? null : diff.Rows[memo.Anchor.RowIndex];
            builder.Append("<article class=\"memo-card").Append(orphaned ? " orphaned" : string.Empty)
                .Append("\" id=\"memo-").Append(note.Number).Append('"');
            if (!orphaned) builder.Append(" data-row=\"").Append(memo.Anchor.RowIndex).Append('"');
            var side = memo.Anchor.Side == DiffMemoSide.Old ? "old" : "new";
            builder.Append(" data-side=\"").Append(side).Append('"');
            builder.AppendLine(" tabindex=\"0\">")
                .Append("<header class=\"memo-card-head\"><span class=\"memo-number\">").Append(note.Number)
                .Append("</span><span class=\"memo-side ").Append(side).Append("\">")
                .Append(memo.Anchor.Side == DiffMemoSide.Old ? "전" : "후")
                .Append("</span><span class=\"memo-kind ").Append(KindClass(memo.Anchor.Kind)).Append("\">")
                .Append(KindLabel(memo.Anchor.Kind)).Append("</span><span class=\"memo-where\">")
                .Append(Encode(orphaned ? "위치 없음" : DescribePosition(row!)))
                .AppendLine("</span></header>")
                .Append("<blockquote class=\"memo-quote\">").Append(Encode(Shorten(memo.Anchor.Quote, 160)))
                .AppendLine("</blockquote>")
                .Append("<p class=\"memo-body\">").Append(EncodeMultiline(memo.Text))
                .AppendLine("</p>")
                .Append("<footer class=\"memo-card-foot\"><span>").Append(Encode(memo.Author))
                .Append("</span><span>").Append(Encode(memo.LastEditedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)))
                .Append(memo.UpdatedAt is null ? string.Empty : " (수정됨)")
                .AppendLine("</span></footer>")
                .AppendLine("<div class=\"memo-replies\">");
            AppendReplies(builder, memo.Replies);
            builder.AppendLine("</div>")
                .AppendLine("<div class=\"memo-card-tools\"><button type=\"button\" class=\"memo-reply-add\">회신</button></div>")
                .AppendLine("</article>");
        }

        builder.AppendLine("</div></aside>")
            // Drawn over both the comparison and the panel, so one memo can be
            // tied to its paragraph the way a 한글 memo balloon has a leader line.
            .AppendLine("<svg class=\"memo-links\" id=\"memo-links\" aria-hidden=\"true\"><path id=\"memo-link-path\" /></svg>");
    }

    private static void AppendReplies(StringBuilder builder, IReadOnlyList<DiffMemoReply> replies)
    {
        foreach (var reply in replies)
        {
            builder.Append("<div class=\"memo-reply\" data-reply-id=\"").Append(Encode(reply.Id))
                .Append("\" data-author-color=\"").Append(AuthorColorIndex(reply.Author)).AppendLine("\">")
                .Append("<div class=\"memo-reply-head\"><span class=\"reply-author\">").Append(Encode(reply.Author))
                .Append("</span><span>").Append(Encode(reply.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)))
                .AppendLine("</span></div><p class=\"reply-body\">")
                .Append(EncodeMultiline(reply.Text)).AppendLine("</p></div>");
        }
    }

    private static int AuthorColorIndex(string author)
    {
        var sum = 0;
        foreach (var rune in author.EnumerateRunes()) sum = (sum + rune.Value) % 4096;
        return sum % 8;
    }

    private static string DescribePosition(DiffRow row)
    {
        var oldPosition = row.OldLine?.ToString(CultureInfo.InvariantCulture) ?? "–";
        var newPosition = row.NewLine?.ToString(CultureInfo.InvariantCulture) ?? "–";
        return $"문단 {oldPosition} → {newPosition}";
    }

    private static string Shorten(string text, int maximumLength)
    {
        var single = DisplayText(text);
        return single.Length <= maximumLength ? single : single[..maximumLength] + "…";
    }

    private static string KindClass(DiffChangeKind kind) => kind switch
    {
        DiffChangeKind.Inserted => "inserted",
        DiffChangeKind.Deleted => "deleted",
        DiffChangeKind.Modified => "modified",
        _ => "unchanged",
    };

    private static string KindLabel(DiffChangeKind kind) => kind switch
    {
        DiffChangeKind.Inserted => "추가",
        DiffChangeKind.Deleted => "삭제",
        DiffChangeKind.Modified => "수정",
        _ => "동일",
    };

    private sealed record NumberedMemo(int Number, DiffMemo Memo);

    private static string GetMarker(DiffChangeKind kind, bool oldSide, bool imaginary)
    {
        if (imaginary) return string.Empty;
        return kind switch
        {
            DiffChangeKind.Deleted when oldSide => "−",
            DiffChangeKind.Inserted when !oldSide => "+",
            DiffChangeKind.Modified => "~",
            _ => string.Empty,
        };
    }

    private static string DisplayText(string text) => text
        .Replace("\r\n", "↵", StringComparison.Ordinal)
        .Replace("\n", "↵", StringComparison.Ordinal)
        .Replace("\r", "↵", StringComparison.Ordinal);

    /// <summary>
    /// The name the page suggests when a recipient saves their replies. Kept
    /// free of characters Windows refuses in a file name.
    /// </summary>
    private static string ExportBaseName(string oldName, string newName)
    {
        var raw = $"{Path.GetFileNameWithoutExtension(oldName)}_vs_{Path.GetFileNameWithoutExtension(newName)}";
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '"', '\'' }).ToArray();
        var safe = new string(raw.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        if (safe.Length > 100) safe = safe[..100].TrimEnd();
        return safe.Length == 0 ? "Hdiff_비교결과" : safe;
    }

    private static string FileName(string path)
    {
        var fileName = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(fileName) ? path : fileName;
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    /// <summary>Keeps the line breaks a reviewer typed into a memo.</summary>
    private static string EncodeMultiline(string value) => Encode(value)
        .Replace("\r\n", "<br>", StringComparison.Ordinal)
        .Replace("\n", "<br>", StringComparison.Ordinal)
        .Replace("\r", "<br>", StringComparison.Ordinal);

    private const string Styles = """
<style>
:root[data-theme="light"] {
  --app:#dce0e6;--surface:#fff;--header:#f5f7fa;--canvas:#fff;--text:#23272f;--muted:#5f6874;
  --border:#c2cad5;--gutter:#f8f9fa;--gutter-text:#69707a;--empty:#f8f9fa;
  --deleted:#fff2f2;--inserted:#effcf5;--removed-text:#9a1f23;--removed-inline:#ffc7ce;
  --added-text:#006832;--added-inline:#c6efce;--overview:#767e8c;--viewport:rgba(55,65,81,.18);
  --primary:#2067b2;--badge:#eef6ff;
  --memo-accent:#d97706;--memo-flag-text:#fff;--memo-card:#fffbeb;--memo-quote:#6b7280;
  --author-0:#b45309;--author-1:#1d4ed8;--author-2:#b91c1c;--author-3:#047857;
  --author-4:#6d28d9;--author-5:#be185d;--author-6:#0369a1;--author-7:#4d7c0f;
}
:root[data-theme="dark"] {
  --app:#0f172a;--surface:#1e293b;--header:#111827;--canvas:#0f172a;--text:#f8fafc;--muted:#94a3b8;
  --border:#334155;--gutter:#0f172a;--gutter-text:#64748b;--empty:#141e30;
  --deleted:#451b23;--inserted:#144232;--removed-text:#fca5a5;--removed-inline:#7f1d1d;
  --added-text:#86efac;--added-inline:#14532d;--overview:#64748b;--viewport:rgba(148,163,184,.28);
  --primary:#2563eb;--badge:#193152;
  --memo-accent:#f59e0b;--memo-flag-text:#1f2937;--memo-card:#26211a;--memo-quote:#9ca3af;
  --author-0:#fbbf24;--author-1:#93c5fd;--author-2:#fca5a5;--author-3:#6ee7b7;
  --author-4:#c4b5fd;--author-5:#f9a8d4;--author-6:#7dd3fc;--author-7:#bef264;
}
*{box-sizing:border-box}
html,body{height:100%;margin:0;overflow:hidden;background:var(--app);color:var(--text);font-family:"Malgun Gothic","맑은 고딕","Segoe UI",sans-serif}
button{font:inherit}
.app-shell{height:100vh;display:grid;grid-template-rows:40px minmax(0,1fr);background:var(--app)}
/* The memo panel takes width away from the stage, so every geometry rule below
   measures the stage as 100vw minus this value instead of the raw viewport. */
:root{--memo-w:0px}
:root.with-memos{--memo-w:320px}
.workspace{position:relative;min-height:0;display:grid;grid-template-columns:minmax(0,1fr) var(--memo-w)}
.top-bar{height:40px;padding:5px 10px;display:grid;grid-template-columns:auto 1fr auto;align-items:center;gap:14px;background:var(--surface);border-bottom:1px solid var(--border)}
.brand{display:flex;align-items:center;gap:8px;white-space:nowrap}.brand strong{font-size:13px}
.brand-mark{display:grid;place-items:center;width:27px;height:27px;border-radius:4px;background:var(--primary);color:#fff;font-size:10px;font-weight:800;letter-spacing:-.3px}
.compact-summary{display:flex;align-items:center;gap:4px}.summary-chip{padding:2px 7px;border:1px solid var(--border);font-size:10px;font-weight:700}.summary-chip.modified{background:var(--header)}.summary-chip.inserted{background:var(--inserted);color:var(--added-text)}.summary-chip.deleted{background:var(--deleted);color:var(--removed-text)}
.export-meta{color:var(--muted);font-size:10px;white-space:nowrap}
.diff-stage{position:relative;min-height:0;display:grid;grid-template-columns:minmax(0,1fr) 5px minmax(0,1fr);grid-template-rows:32px minmax(0,1fr);overflow:hidden;background:var(--canvas)}
.center-divider{z-index:4;grid-column:2;grid-row:1/3;background:linear-gradient(to right,var(--app) 0 2px,var(--border) 2px 3px,var(--app) 3px 5px);pointer-events:none}
.pane-header{z-index:2;min-width:0;padding:7px 52px 7px 10px;background:var(--header);border-bottom:1px solid var(--border);font-size:11px;font-weight:700;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.pane-header span{font-weight:400;color:var(--muted)}
.old-header{grid-column:1;grid-row:1}.new-header{grid-column:3;grid-row:1}
.shared-scroll{z-index:1;grid-column:1/4;grid-row:2;min-width:0;min-height:0;overflow:auto;overscroll-behavior:contain;background:var(--canvas);scrollbar-width:none}
.shared-scroll::-webkit-scrollbar{width:0;height:0;display:none}
.diff-pairs{min-width:100%;width:max-content;background:var(--canvas)}
.diff-pair{min-width:100%;display:grid;grid-template-columns:minmax(0,1fr) 5px minmax(0,1fr);align-items:stretch}
.diff-row{min-width:0;display:grid;grid-template-columns:48px minmax(0,1fr);align-items:stretch;padding-right:42px;background:var(--canvas);font-size:var(--document-font-size);line-height:1.55}
.old-side{grid-column:1;grid-row:1}.pair-divider{grid-column:2;grid-row:1;background:var(--app)}.new-side{grid-column:3;grid-row:1}
.wrap .diff-pairs,.wrap .diff-pair{width:100%}.nowrap .diff-pair{grid-template-columns:minmax(calc((100vw - var(--memo-w) - 5px)/2),max-content) 5px minmax(calc((100vw - var(--memo-w) - 5px)/2),max-content)}.nowrap .diff-row{grid-template-columns:48px max-content}
.separators .diff-row{border-bottom:1px solid color-mix(in srgb,var(--border) 45%,transparent)}
.old-side.kind-deleted,.old-side.kind-modified{background:var(--deleted)}.new-side.kind-inserted,.new-side.kind-modified{background:var(--inserted)}
.old-side.kind-inserted,.new-side.kind-deleted,.diff-row.imaginary{background:var(--empty)}
.gutter{position:sticky;left:0;z-index:1;min-height:27px;display:grid;grid-template-columns:15px minmax(0,1fr);align-items:start;padding-top:3px;background:var(--gutter);border-right:1px solid var(--border);color:var(--gutter-text);font-size:calc(var(--document-font-size) - 1px);user-select:none}
.new-side .gutter{left:calc((100vw - var(--memo-w))/2 + 2px)}
.marker{text-align:center;font-weight:700}.old-side .marker{color:var(--removed-text)}.new-side .marker{color:var(--added-text)}.kind-modified .marker{color:var(--muted)}.line-number{text-align:right;padding-right:4px;font-variant-numeric:tabular-nums}
.line-text{min-width:0;padding:3px 7px;white-space:pre;tab-size:4;overflow-wrap:normal;color:var(--text);cursor:text}.wrap .line-text{white-space:pre-wrap;overflow-wrap:anywhere;word-break:normal}
.fragment.removed{color:var(--removed-text);background:var(--removed-inline)}.fragment.added{color:var(--added-text);background:var(--added-inline)}
.section-header .diff-row:not(.kind-modified):not(.kind-inserted):not(.kind-deleted){background:var(--header)}.section-header .line-text{font-weight:700}.section-header .line-number,.structural-spacer .line-number{visibility:hidden}
.overview{z-index:5;grid-row:2;position:relative;justify-self:end;width:42px;min-height:0;overflow:hidden;background:var(--header);border-left:1px solid var(--border);cursor:pointer;touch-action:none;user-select:none}.old-overview{grid-column:1}.new-overview{grid-column:3}.overview-canvas,.overview-signal-canvas{position:absolute;inset:0;width:100%;height:100%;display:block}.overview-canvas{z-index:1}.overview-signal-canvas{z-index:3;pointer-events:none}.overview-viewport{position:absolute;z-index:2;left:0;right:0;min-height:16px;border:1px solid var(--overview);background:var(--viewport);pointer-events:none}
.memo-toggle{cursor:pointer;color:var(--memo-accent);background:var(--memo-card);border-color:var(--memo-accent)}
.memo-toggle[aria-pressed="false"]{background:var(--header);color:var(--muted);border-color:var(--border)}
.memo-flag{display:inline-block;vertical-align:baseline;margin:0 0 0 5px;padding:0 5px;min-width:17px;border:1px solid var(--memo-accent);border-radius:9px;background:var(--memo-accent);color:var(--memo-flag-text);font-size:10px;font-weight:700;line-height:15px;white-space:normal;cursor:pointer}
.memo-flag:hover,.memo-flag:focus-visible{outline:2px solid var(--memo-accent);outline-offset:1px}
.diff-pair.has-memo .diff-row:not(.imaginary){box-shadow:inset 3px 0 0 var(--memo-accent)}
.diff-pair.target-flash .diff-row{animation:memo-flash 1.2s ease-out}
@keyframes memo-flash{from{background:var(--memo-card)}to{background:transparent}}
.memo-panel{grid-column:2;grid-row:1;min-width:0;min-height:0;display:none;grid-template-rows:auto minmax(0,1fr);background:var(--surface);border-left:1px solid var(--border)}
.with-memos .memo-panel{display:grid}
.memo-panel-head{padding:6px 8px 7px 12px;background:var(--header);border-bottom:1px solid var(--border);font-size:11px}
.memo-panel-title{display:flex;align-items:center;gap:7px}
.memo-panel-count{padding:1px 6px;border:1px solid var(--memo-accent);border-radius:8px;color:var(--memo-accent);font-size:10px;font-weight:700}
.memo-panel-close{margin-left:auto;width:22px;height:22px;border:1px solid var(--border);background:var(--surface);color:var(--muted);font-size:13px;line-height:1;cursor:pointer}
.memo-links{position:absolute;inset:0;z-index:30;overflow:visible;pointer-events:none}
.memo-links path{fill:none;stroke:var(--memo-accent);stroke-width:1.5;stroke-dasharray:5 3}
.memo-side{padding:1px 5px;border:1px solid var(--border);font-weight:700}
.memo-side.old{background:var(--deleted);color:var(--removed-text)}
.memo-side.new{background:var(--inserted);color:var(--added-text)}
.memo-unsaved{padding:1px 6px;border:1px solid var(--removed-text);border-radius:8px;color:var(--removed-text);font-size:10px;font-weight:700}
.memo-empty{margin:2px;padding:10px;border:1px dashed var(--border);color:var(--muted);font-size:11px;line-height:1.6}
.memo-add{position:absolute;z-index:6;width:22px;height:22px;padding:0;border:1px solid var(--memo-accent);border-radius:11px;background:var(--surface);color:var(--memo-accent);font-size:15px;font-weight:700;line-height:1;cursor:pointer}
.memo-add:hover{background:var(--memo-accent);color:var(--memo-flag-text)}
.memo-card.added{border-left-color:var(--author-color,var(--memo-accent))}
.memo-card-remove{width:16px;height:16px;padding:0;border:0;background:none;color:var(--muted);font-size:12px;line-height:1;cursor:pointer}
.memo-draft{border-left-color:var(--primary)}
.memo-panel-tools{margin-top:6px;display:flex;gap:5px}
.memo-panel-tools input{min-width:0;flex:1;padding:3px 6px;border:1px solid var(--border);background:var(--canvas);color:var(--text);font:inherit;font-size:11px}
.memo-panel-tools button{padding:3px 9px;border:1px solid var(--primary);background:var(--primary);color:#fff;font-size:11px;font-weight:700;cursor:pointer}
.memo-panel-hint{margin:6px 0 0;color:var(--muted);font-size:10px;line-height:1.5}
.memo-saved{margin:5px 0 0;display:flex;align-items:center;gap:6px;color:var(--added-text);font-size:10px}
.memo-saved.pending{color:var(--muted)}
.memo-saved span{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.memo-relocate{flex:none;padding:1px 6px;border:1px solid var(--border);background:var(--surface);color:var(--muted);font-size:10px;cursor:pointer}
.memo-replies{display:flex;flex-direction:column;gap:6px}
.memo-replies:not(:empty){margin-top:8px}
.memo-reply{--author-color:var(--primary);padding:6px 8px;border-left:3px solid var(--author-color);background:var(--surface)}
.memo-reply[data-author-color="0"]{--author-color:var(--author-0)}.memo-reply[data-author-color="1"]{--author-color:var(--author-1)}
.memo-reply[data-author-color="2"]{--author-color:var(--author-2)}.memo-reply[data-author-color="3"]{--author-color:var(--author-3)}
.memo-reply[data-author-color="4"]{--author-color:var(--author-4)}.memo-reply[data-author-color="5"]{--author-color:var(--author-5)}
.memo-reply[data-author-color="6"]{--author-color:var(--author-6)}.memo-reply[data-author-color="7"]{--author-color:var(--author-7)}
.memo-reply-head{display:flex;align-items:center;gap:6px;font-size:10px;color:var(--muted)}
.reply-author{color:var(--author-color);font-weight:700}
.reply-delete{margin-left:auto;width:16px;height:16px;padding:0;border:0;background:none;color:var(--muted);font-size:12px;line-height:1;cursor:pointer}
.reply-body{margin:4px 0 0;color:var(--author-color);font-size:12px;line-height:1.6;white-space:pre-wrap;overflow-wrap:anywhere}
.memo-card-tools{margin-top:8px;display:flex;gap:6px}
.memo-reply-add{padding:2px 9px;border:1px solid var(--border);background:var(--surface);color:var(--text);font-size:11px;cursor:pointer}
.memo-reply-add:hover{border-color:var(--primary);color:var(--primary)}
.memo-reply-editor{margin-top:7px;display:flex;flex-direction:column;gap:5px}
.memo-reply-editor textarea{width:100%;padding:5px 6px;border:1px solid var(--primary);background:var(--canvas);color:var(--text);font:inherit;font-size:12px;line-height:1.5;resize:vertical}
.memo-reply-editor div{display:flex;gap:5px;justify-content:flex-end}
.memo-reply-editor button{padding:2px 9px;border:1px solid var(--border);background:var(--surface);color:var(--text);font-size:11px;cursor:pointer}
.memo-reply-editor .reply-save{border-color:var(--primary);background:var(--primary);color:#fff;font-weight:700}
.memo-list{min-height:0;overflow:auto;padding:8px;display:flex;flex-direction:column;gap:8px}
.memo-card{padding:8px 10px;background:var(--memo-card);border:1px solid var(--border);border-left:3px solid var(--memo-accent);cursor:pointer}
.memo-card.orphaned{border-left-color:var(--muted);opacity:.75;cursor:default}
.memo-card.selected,.memo-card:focus-visible{outline:2px solid var(--memo-accent);outline-offset:-1px}
.memo-card-head{display:flex;align-items:center;gap:6px;font-size:10px}
.memo-number{display:grid;place-items:center;min-width:17px;height:17px;padding:0 4px;border-radius:9px;background:var(--memo-accent);color:var(--memo-flag-text);font-weight:700}
.memo-kind{padding:1px 5px;border:1px solid var(--border);font-weight:700}
.memo-kind.inserted{background:var(--inserted);color:var(--added-text)}.memo-kind.deleted{background:var(--deleted);color:var(--removed-text)}.memo-kind.modified{background:var(--header);color:var(--text)}.memo-kind.unchanged{background:var(--header);color:var(--muted)}
.memo-where{margin-left:auto;color:var(--muted)}
.memo-quote{margin:6px 0 0;padding-left:7px;border-left:2px solid var(--border);color:var(--memo-quote);font-size:11px;line-height:1.5;overflow-wrap:anywhere}
.memo-body{margin:7px 0 0;font-size:12px;line-height:1.6;color:var(--text);overflow-wrap:anywhere}
.memo-card-foot{margin-top:7px;display:flex;justify-content:space-between;gap:8px;color:var(--muted);font-size:10px}
@media(max-width:700px){.export-meta{display:none}.top-bar{grid-template-columns:auto 1fr}.compact-summary{justify-content:flex-end}}
/* Below this width the two panes already fight for room, so the memo panel
   floats above the stage instead of taking a column from it. */
@media(max-width:900px){:root.with-memos{--memo-w:0px}.with-memos .memo-panel{position:absolute;top:0;right:0;bottom:0;width:min(320px,86vw);z-index:20;box-shadow:0 0 16px rgba(0,0,0,.28)}}
@media print{html,body{height:auto;overflow:visible}.app-shell{height:auto;display:block}.top-bar{position:static}.workspace{display:block}.diff-stage{display:grid;min-height:900px}.shared-scroll{overflow:visible}.overview{display:none}.diff-row{padding-right:0}.memo-toggle,.memo-panel-close,.memo-panel-tools,.memo-panel-hint,.memo-card-tools,.memo-reply-editor,.reply-delete,.memo-add,.memo-card-remove,.memo-unsaved,.memo-empty,.memo-links,.memo-saved{display:none}.with-memos .memo-panel{display:block;position:static;width:auto;border-left:0;border-top:1px solid var(--border);break-inside:avoid}.memo-list{display:block;overflow:visible}.memo-card{margin-bottom:8px;break-inside:avoid}}
</style>
""";

    private const string Script = """
<script>
(() => {
  'use strict';
  const scroll = document.getElementById('diff-scroll');
  const pairs = [...document.querySelectorAll('#diff-pairs > .diff-pair')];
  const overviews = [...document.querySelectorAll('.overview')];
  const viewports = overviews.map(overview => overview.querySelector('.overview-viewport'));
  let viewportFrame = 0;
  let overviewFrame = 0;

  const rows = pairs.map(pair => {
    const readSide = side => {
      const element = pair.querySelector(`.${side}-side`);
      let kind = 'unchanged';
      if (element.classList.contains('kind-modified')) kind = 'modified';
      else if (element.classList.contains('kind-deleted')) kind = 'deleted';
      else if (element.classList.contains('kind-inserted')) kind = 'inserted';
      // Only the document fragments count: a memo flag must not make the
      // minimap draw the paragraph longer than it is.
      const fragments = [...element.querySelectorAll('.line-text > span')];
      return {
        kind,
        imaginary: element.classList.contains('imaginary'),
        length: fragments.reduce((total, span) => total + span.textContent.length, 0),
      };
    };
    return {old: readSide('old'), new: readSide('new')};
  });

  function updateViewports() {
    viewportFrame = 0;
    const total = Math.max(1, scroll.scrollHeight);
    const height = Math.max(2, Math.min(100, (scroll.clientHeight / total) * 100));
    const top = Math.min(100 - height, (scroll.scrollTop / total) * 100);
    for (const viewport of viewports) {
      viewport.style.height = `${height}%`;
      viewport.style.top = `${top}%`;
    }
  }

  function scheduleViewportUpdate() {
    if (!viewportFrame) viewportFrame = requestAnimationFrame(updateViewports);
  }

  scroll.addEventListener('scroll', scheduleViewportUpdate, {passive:true});

  function navigateFromOverview(overview, clientY) {
    const rect = overview.getBoundingClientRect();
    const ratio = Math.max(0, Math.min(1, (clientY - rect.top) / Math.max(1, rect.height)));
    const centeredTop = (ratio * scroll.scrollHeight) - (scroll.clientHeight / 2);
    scroll.scrollTop = Math.max(0, Math.min(scroll.scrollHeight - scroll.clientHeight, centeredTop));
    scheduleViewportUpdate();
  }

  for (const overview of overviews) {
    overview.addEventListener('pointerdown', event => {
      if (event.button !== 0) return;
      event.preventDefault();
      overview.setPointerCapture(event.pointerId);
      navigateFromOverview(overview, event.clientY);
    });
    overview.addEventListener('pointermove', event => {
      if (overview.hasPointerCapture(event.pointerId)) navigateFromOverview(overview, event.clientY);
    });
    const endDrag = event => {
      if (overview.hasPointerCapture(event.pointerId)) overview.releasePointerCapture(event.pointerId);
    };
    overview.addEventListener('pointerup', endDrag);
    overview.addEventListener('pointercancel', endDrag);
  }

  function cssColor(name, fallback) {
    return getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;
  }

  function drawOverview(overview) {
    const canvas = overview.querySelector('.overview-canvas');
    const signalCanvas = overview.querySelector('.overview-signal-canvas');
    const rect = overview.getBoundingClientRect();
    const scale = Math.max(1, window.devicePixelRatio || 1);
    const width = Math.max(1, Math.round(rect.width * scale));
    const height = Math.max(1, Math.round(rect.height * scale));
    if (canvas.width !== width) canvas.width = width;
    if (canvas.height !== height) canvas.height = height;
    if (signalCanvas.width !== width) signalCanvas.width = width;
    if (signalCanvas.height !== height) signalCanvas.height = height;
    const context = canvas.getContext('2d', {alpha:true});
    const signalContext = signalCanvas.getContext('2d', {alpha:true});
    context.clearRect(0, 0, width, height);
    signalContext.clearRect(0, 0, width, height);

    const side = overview.dataset.side;
    const sideRows = rows.map(row => row[side]);
    let maxLength = 1;
    for (const row of sideRows) {
      if (!row.imaginary) maxLength = Math.max(maxLength, row.length);
    }

    const count = Math.max(1, sideRows.length);
    const changeRailWidth = 6 * scale;
    const availableWidth = Math.max(3 * scale, width - changeRailWidth - (7 * scale));
    for (let index = 0; index < sideRows.length; index++) {
      const row = sideRows[index];
      if (row.imaginary) continue;
      const y = Math.min(height - 1, Math.floor((index / count) * height));
      const nextY = Math.min(height, Math.floor(((index + 1) / count) * height));
      const lineHeight = Math.max(1, nextY - y);
      const lineWidth = Math.max(3 * scale, Math.ceil(availableWidth * (row.length / maxLength)));
      const changed = row.kind === 'modified'
        || (side === 'old' && row.kind === 'deleted')
        || (side === 'new' && row.kind === 'inserted');
      context.globalAlpha = changed ? (180 / 255) : (145 / 255);
      context.fillStyle = changed
        ? cssColor(side === 'old' ? '--removed-inline' : '--added-inline', side === 'old' ? '#ffc7ce' : '#c6efce')
        : cssColor('--overview', '#767e8c');
      context.fillRect(width - (3 * scale) - lineWidth, y, lineWidth, lineHeight);

      if (!changed) continue;
      const signalHeight = Math.min(height - y, Math.max(scale, lineHeight));
      signalContext.fillStyle = side === 'old'
        ? (row.kind === 'modified' ? '#f59e0b' : cssColor('--removed-text', '#9a1f23'))
        : cssColor('--added-text', '#006832');
      signalContext.fillRect(scale, y, changeRailWidth, signalHeight);
    }
    context.globalAlpha = 1;
  }

  function drawOverviews() {
    overviewFrame = 0;
    for (const overview of overviews) drawOverview(overview);
    updateViewports();
  }

  function scheduleOverviewDraw() {
    if (!overviewFrame) overviewFrame = requestAnimationFrame(drawOverviews);
  }

  window.addEventListener('resize', scheduleOverviewDraw);
  if ('ResizeObserver' in window) new ResizeObserver(scheduleOverviewDraw).observe(document.querySelector('.diff-stage'));
  const ready = document.fonts?.ready ?? Promise.resolve();
  ready.then(scheduleOverviewDraw);

  const memoPanel = document.getElementById('memo-panel');
  const memoList = document.getElementById('memo-list');
  const memoToggle = document.getElementById('memo-toggle');
  const memoClose = document.getElementById('memo-close');
  const memoCountLabel = document.getElementById('memo-count');
  const memoEmpty = document.getElementById('memo-empty');
  const unsavedBadge = document.getElementById('memo-unsaved');
  const addButton = document.getElementById('memo-add');
  const nameInput = document.getElementById('reviewer-name');
  const savedLine = document.getElementById('memo-saved');
  const savedText = document.getElementById('memo-saved-text');
  const relocateButton = document.getElementById('memo-relocate');
  const stage = document.querySelector('.diff-stage');
  const workspace = document.querySelector('.workspace');
  const linkPath = document.getElementById('memo-link-path');
  let flashTimer = 0;
  let dirty = false;
  let hoveredPair = null;
  let hoveredSide = 'new';
  let linkedCard = null;
  let saveHandle = null;
  let savedRound = 0;
  let pickerBroken = false;

  const cards = () => [...memoList.querySelectorAll('.memo-card')];

  /**
   * Each card owns one flag in the comparison text. They are written in the
   * same order per row, so pairing them by that order rebuilds the link every
   * time the file is opened - no extra markup has to survive a round trip.
   */
  const flagOfCard = new Map();

  function linkFlags() {
    flagOfCard.clear();
    const perCell = new Map();
    for (const flag of document.querySelectorAll('.memo-flag')) {
      const key = `${flag.closest('.diff-pair').dataset.row}/${flag.closest('.old-side') ? 'old' : 'new'}`;
      if (!perCell.has(key)) perCell.set(key, []);
      perCell.get(key).push(flag);
    }
    const used = new Map();
    for (const card of cards()) {
      if (card.dataset.row === undefined) continue;
      const key = `${card.dataset.row}/${card.dataset.side ?? 'new'}`;
      const index = used.get(key) ?? 0;
      used.set(key, index + 1);
      const flag = perCell.get(key)?.[index];
      if (flag) flagOfCard.set(card, flag);
    }
  }

  function shorten(text, limit) {
    return text.length <= limit ? text : text.slice(0, limit) + '…';
  }

  /** Keeps card numbers, flag numbers and the counters in one reading order. */
  function renumber() {
    const all = cards();
    all.forEach((card, index) => {
      const number = index + 1;
      card.id = `memo-${number}`;
      card.querySelector('.memo-number').textContent = number;
      const flag = flagOfCard.get(card);
      if (!flag) return;
      flag.textContent = number;
      flag.dataset.memo = card.id;
      const author = card.querySelector('.memo-card-foot span')?.textContent ?? '';
      const body = card.querySelector('.memo-body')?.textContent ?? '';
      flag.title = `검토 메모 ${number} · ${author} · ${shorten(body, 60)}`;
    });
    if (memoCountLabel) memoCountLabel.textContent = all.length;
    if (memoEmpty) memoEmpty.hidden = all.length > 0;
    if (memoToggle) memoToggle.textContent = all.length > 0 ? `검토 메모 ${all.length}` : '검토 메모';
  }

  function markDirty() {
    dirty = true;
    if (unsavedBadge) unsavedBadge.hidden = false;
  }

  // Memos and replies are only written to a file when the reader asks for it,
  // so leaving with unsaved ones has to be worth a question.
  window.addEventListener('beforeunload', event => {
    if (!dirty) return;
    event.preventDefault();
    event.returnValue = '';
  });

  function showMemoPanel(open) {
    document.documentElement.classList.toggle('with-memos', open);
    if (memoToggle) memoToggle.setAttribute('aria-pressed', String(open));
    scheduleOverviewDraw();
    drawLink();
  }

  function selectMemoCard(card) {
    for (const other of cards()) other.classList.toggle('selected', other === card);
    card.scrollIntoView({block: 'nearest'});
    linkedCard = card;
    drawLink();
  }

  /**
   * Draws the leader line of the selected memo, from its flag in the text to
   * its card in the panel. Only one at a time: every memo linked at once turns
   * into a web of crossing lines that hides what it should show.
   */
  function drawLink() {
    if (!linkPath) return;
    const card = linkedCard;
    const flag = card && flagOfCard.get(card);
    const open = document.documentElement.classList.contains('with-memos');
    if (!card || !flag || !open) {
      linkPath.removeAttribute('d');
      return;
    }
    const flagRect = flag.getBoundingClientRect();
    const viewRect = scroll.getBoundingClientRect();
    // The paragraph may be scrolled out of the comparison view entirely.
    if (flagRect.bottom < viewRect.top || flagRect.top > viewRect.bottom) {
      linkPath.removeAttribute('d');
      return;
    }
    const cardRect = card.getBoundingClientRect();
    const origin = workspace.getBoundingClientRect();
    const startX = flagRect.right - origin.left;
    const startY = Math.min(Math.max(flagRect.top + (flagRect.height / 2), viewRect.top), viewRect.bottom) - origin.top;
    const endX = cardRect.left - origin.left;
    const endY = cardRect.top + Math.min(18, cardRect.height / 2) - origin.top;
    const bend = Math.max(24, (endX - startX) / 3);
    linkPath.setAttribute('d',
      `M${startX} ${startY} C${startX + bend} ${startY}, ${endX - bend} ${endY}, ${endX} ${endY}`);
  }

  function revealRow(rowIndex) {
    const pair = pairs[rowIndex];
    if (!pair) return;
    scroll.scrollTop = Math.max(0, pair.offsetTop - Math.round(scroll.clientHeight / 3));
    scheduleViewportUpdate();
    if (flashTimer) clearTimeout(flashTimer);
    for (const other of pairs) other.classList.remove('target-flash');
    // Restart the CSS animation after the class is really gone.
    void pair.offsetWidth;
    pair.classList.add('target-flash');
    flashTimer = setTimeout(() => pair.classList.remove('target-flash'), 1400);
  }

  if (memoToggle) {
    memoToggle.addEventListener('click', () =>
      showMemoPanel(!document.documentElement.classList.contains('with-memos')));
  }
  if (memoClose) memoClose.addEventListener('click', () => showMemoPanel(false));

  function bindFlag(flag) {
    flag.addEventListener('click', () => {
      const card = document.getElementById(flag.dataset.memo);
      if (!card) return;
      showMemoPanel(true);
      selectMemoCard(card);
    });
  }

  for (const flag of document.querySelectorAll('.memo-flag')) bindFlag(flag);

  // Clicking the reply controls must not also drag the comparison to the row.
  const isCardControl = target => !!target.closest('.memo-card-tools, .memo-reply-editor, .memo-replies, .memo-card-remove');

  function bindCard(card) {
    const rowIndex = Number(card.dataset.row);
    if (!Number.isInteger(rowIndex)) return;
    card.addEventListener('click', event => {
      if (isCardControl(event.target)) return;
      selectMemoCard(card);
      revealRow(rowIndex);
    });
    card.addEventListener('keydown', event => {
      if (event.key !== 'Enter' && event.key !== ' ') return;
      if (isCardControl(event.target)) return;
      event.preventDefault();
      selectMemoCard(card);
      revealRow(rowIndex);
    });
  }

  function authorColorIndex(author) {
    let sum = 0;
    for (const character of author) sum = (sum + character.codePointAt(0)) % 4096;
    return sum % 8;
  }

  function stamp(date) {
    const pad = value => String(value).padStart(2, '0');
    return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} `
      + `${pad(date.getHours())}:${pad(date.getMinutes())}`;
  }

  function reviewerName() {
    const name = (nameInput?.value ?? '').trim();
    if (name) return name;
    showMemoPanel(true);
    alert('먼저 내 이름을 입력해 주세요.');
    nameInput?.focus();
    return '';
  }

  function element(tag, className, text) {
    const created = document.createElement(tag);
    if (className) created.className = className;
    if (text !== undefined) created.textContent = text;
    return created;
  }

  function actionButton(className, text, title) {
    const button = element('button', className, text);
    button.type = 'button';
    if (title) button.title = title;
    return button;
  }

  /** Builds a reply as real DOM so that re-saving the page keeps it. */
  function appendReply(card, author, text, at) {
    const reply = element('div', 'memo-reply');
    reply.dataset.authorColor = String(authorColorIndex(author));
    const head = element('div', 'memo-reply-head');
    head.append(element('span', 'reply-author', author), element('span', null, at));
    const remove = actionButton('reply-delete', '×', '이 회신 삭제');
    head.append(remove);
    reply.append(head, element('p', 'reply-body', text));
    card.querySelector('.memo-replies').append(reply);
    bindReplyDelete(remove);
    return reply;
  }

  function bindReplyDelete(button) {
    button.addEventListener('click', event => {
      event.stopPropagation();
      if (!confirm('이 회신을 삭제할까요?')) return;
      button.closest('.memo-reply').remove();
      markDirty();
    });
  }

  for (const button of document.querySelectorAll('.reply-delete')) bindReplyDelete(button);

  function openReplyEditor(card) {
    let editor = card.querySelector('.memo-reply-editor');
    if (editor) {
      editor.querySelector('textarea').focus();
      return;
    }
    editor = element('div', 'memo-reply-editor');
    const area = document.createElement('textarea');
    area.rows = 3;
    area.placeholder = '회신 내용을 입력하세요. (Ctrl+Enter로 등록)';
    const buttons = document.createElement('div');
    const save = actionButton('reply-save', '등록');
    const cancel = actionButton('reply-cancel', '취소');
    buttons.append(cancel, save);
    editor.append(area, buttons);
    card.querySelector('.memo-card-tools').before(editor);

    const commit = () => {
      const text = area.value.trim();
      if (!text) return;
      const author = reviewerName();
      if (!author) return;
      appendReply(card, author, text, stamp(new Date()));
      editor.remove();
      markDirty();
    };
    save.addEventListener('click', commit);
    cancel.addEventListener('click', () => editor.remove());
    area.addEventListener('keydown', event => {
      if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
        event.preventDefault();
        commit();
      } else if (event.key === 'Escape') {
        editor.remove();
      }
    });
    area.focus();
  }

  function bindReplyAdd(button) {
    button.addEventListener('click', event => {
      event.stopPropagation();
      openReplyEditor(button.closest('.memo-card'));
    });
  }

  for (const button of document.querySelectorAll('.memo-reply-add')) bindReplyAdd(button);

  /** Only memos this reader added carry a remove button; originals are read-only. */
  function bindCardRemove(button) {
    button.addEventListener('click', event => {
      event.stopPropagation();
      if (!confirm('내가 추가한 이 메모를 삭제할까요?')) return;
      const card = button.closest('.memo-card');
      const pair = pairs[Number(card.dataset.row)];
      flagOfCard.get(card)?.remove();
      card.remove();
      if (linkedCard === card) linkedCard = null;
      if (pair && !pair.querySelector('.memo-flag')) pair.classList.remove('has-memo');
      linkFlags();
      renumber();
      drawLink();
      markDirty();
    });
  }

  // Bound from the DOM, not from the call that created the card, so the button
  // still works after the saved file is reopened.
  for (const button of document.querySelectorAll('.memo-card-remove')) bindCardRemove(button);

  linkFlags();
  for (const card of cards()) bindCard(card);

  // --- Writing a brand new memo on any comparison row -----------------------

  /** A memo cannot sit on an empty cell, so a one-sided row forces its side. */
  function resolveSide(pair, side) {
    if (pair.querySelector('.old-side').classList.contains('imaginary')) return 'new';
    if (pair.querySelector('.new-side').classList.contains('imaginary')) return 'old';
    return side;
  }

  function sideElement(pair, side) {
    return pair.querySelector(`.${resolveSide(pair, side)}-side`);
  }

  function rowText(side) {
    return [...side.querySelectorAll('.line-text > span')].map(span => span.textContent).join('');
  }

  function rowKind(pair) {
    const side = pair.querySelector('.new-side');
    if (side.classList.contains('kind-modified')) return {css: 'modified', label: '수정'};
    if (side.classList.contains('kind-inserted')) return {css: 'inserted', label: '추가'};
    if (side.classList.contains('kind-deleted')) return {css: 'deleted', label: '삭제'};
    return {css: 'unchanged', label: '동일'};
  }

  function rowPosition(pair) {
    const number = side => pair.querySelector(`.${side}-side .line-number`)?.textContent.trim() || '–';
    return `문단 ${number('old')} → ${number('new')}`;
  }

  function placeAddButton(pair, side) {
    hoveredPair = pair;
    if (!pair) {
      addButton.hidden = true;
      return;
    }
    hoveredSide = resolveSide(pair, side ?? 'new');
    addButton.title = hoveredSide === 'old' ? '변경 전 문단에 검토 메모 추가' : '변경 후 문단에 검토 메모 추가';
    const sideRect = sideElement(pair, hoveredSide).getBoundingClientRect();
    const stageRect = stage.getBoundingClientRect();
    const scrollRect = scroll.getBoundingClientRect();
    const top = sideRect.top - stageRect.top;
    // A row half hidden under the pane header must not get a floating button.
    if (sideRect.bottom < scrollRect.top + 8 || sideRect.top > scrollRect.bottom - 8) {
      addButton.hidden = true;
      return;
    }
    const left = Math.min(
      sideRect.right - stageRect.left - 66,
      stageRect.width - 30);
    addButton.style.top = `${Math.max(scrollRect.top - stageRect.top + 2, top + 2)}px`;
    addButton.style.left = `${Math.max(4, left)}px`;
    addButton.hidden = false;
  }

  scroll.addEventListener('mousemove', event => {
    const pair = event.target.closest?.('.diff-pair') ?? null;
    // The column the pointer is over is the column the memo will belong to.
    const side = event.target.closest?.('.old-side') ? 'old' : 'new';
    // Also re-place when the button is hidden: the row may have been only
    // partly visible the last time the pointer entered it.
    if (pair !== hoveredPair || side !== hoveredSide || (pair && addButton.hidden)) placeAddButton(pair, side);
  });
  scroll.addEventListener('mouseleave', event => {
    if (event.relatedTarget === addButton) return;
    placeAddButton(null);
  });
  scroll.addEventListener('scroll', () => {
    placeAddButton(null);
    drawLink();
  }, {passive: true});
  window.addEventListener('resize', drawLink);

  function insertCardInRowOrder(card, rowIndex) {
    const following = cards().find(other => {
      const otherRow = Number(other.dataset.row);
      return !Number.isInteger(otherRow) || otherRow > rowIndex;
    });
    if (following) following.before(card);
    else memoList.append(card);
  }

  function createMemo(pair, sideName, author, text) {
    const rowIndex = Number(pair.dataset.row);
    const kind = rowKind(pair);
    const resolved = resolveSide(pair, sideName);
    const side = sideElement(pair, resolved);

    const card = element('article', 'memo-card added');
    card.dataset.row = String(rowIndex);
    card.dataset.side = resolved;
    card.dataset.added = '1';
    card.dataset.authorColor = String(authorColorIndex(author));
    card.tabIndex = 0;

    const head = element('header', 'memo-card-head');
    head.append(
      element('span', 'memo-number', '0'),
      element('span', `memo-side ${resolved}`, resolved === 'old' ? '전' : '후'),
      element('span', `memo-kind ${kind.css}`, kind.label),
      element('span', 'memo-where', rowPosition(pair)));
    const remove = actionButton('memo-card-remove', '×', '내가 추가한 메모 삭제');
    head.append(remove);

    const foot = element('footer', 'memo-card-foot');
    foot.append(element('span', null, author), element('span', null, stamp(new Date())));

    card.append(
      head,
      element('blockquote', 'memo-quote', shorten(rowText(side), 160)),
      element('p', 'memo-body', text),
      foot,
      element('div', 'memo-replies'));
    const tools = element('div', 'memo-card-tools');
    const reply = actionButton('memo-reply-add', '회신');
    tools.append(reply);
    card.append(tools);

    const flag = actionButton('memo-flag', '0');
    side.querySelector('.line-text').append(flag);
    pair.classList.add('has-memo');

    insertCardInRowOrder(card, rowIndex);
    linkFlags();
    renumber();
    bindCard(card);
    bindFlag(flag);
    bindReplyAdd(reply);
    bindCardRemove(remove);
    markDirty();
    return card;
  }

  function openMemoDraft(pair, sideName) {
    memoList.querySelector('.memo-draft')?.remove();
    const resolved = resolveSide(pair, sideName);
    const draft = element('article', 'memo-card memo-draft');
    const head = element('header', 'memo-card-head');
    const kind = rowKind(pair);
    head.append(
      element('span', `memo-side ${resolved}`, resolved === 'old' ? '전' : '후'),
      element('span', 'memo-kind ' + kind.css, kind.label),
      element('span', 'memo-where', rowPosition(pair)));
    const editor = element('div', 'memo-reply-editor');
    const area = document.createElement('textarea');
    area.rows = 3;
    area.placeholder = '이 문단에 남길 검토 메모. (Ctrl+Enter로 등록)';
    const buttons = document.createElement('div');
    const save = actionButton('reply-save', '등록');
    const cancel = actionButton('reply-cancel', '취소');
    buttons.append(cancel, save);
    editor.append(area, buttons);
    draft.append(
      head,
      element('blockquote', 'memo-quote', shorten(rowText(sideElement(pair, resolved)), 160)),
      editor);
    insertCardInRowOrder(draft, Number(pair.dataset.row));
    draft.scrollIntoView({block: 'nearest'});

    const commit = () => {
      const text = area.value.trim();
      if (!text) return;
      const author = reviewerName();
      if (!author) return;
      draft.remove();
      selectMemoCard(createMemo(pair, resolved, author, text));
    };
    save.addEventListener('click', commit);
    cancel.addEventListener('click', () => draft.remove());
    area.addEventListener('keydown', event => {
      if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
        event.preventDefault();
        commit();
      } else if (event.key === 'Escape') {
        draft.remove();
      }
    });
    area.focus();
  }

  addButton?.addEventListener('click', () => {
    if (!hoveredPair) return;
    showMemoPanel(true);
    openMemoDraft(hoveredPair, hoveredSide);
  });

  /**
   * Saves the page as it now stands. Memos and replies live in the DOM, so
   * writing the serialized document back out is enough to keep them: the
   * saved file opens exactly like this one, review included.
   *
   * Where the browser offers a real save dialog it is used, and the chosen file
   * is kept for later saves so a review session writes one file instead of a
   * pile of downloads. Browsers without it fall back to a plain download.
   */
  async function saveWithReplies() {
    const root = document.documentElement;
    for (const editor of document.querySelectorAll('.memo-reply-editor')) editor.remove();
    for (const draft of document.querySelectorAll('.memo-draft')) draft.remove();
    for (const card of cards()) card.classList.remove('selected');
    for (const pair of pairs) pair.classList.remove('target-flash');
    addButton.hidden = true;
    hoveredPair = null;
    linkedCard = null;
    drawLink();
    // A live input value is not part of outerHTML until it is written back.
    if (nameInput) nameInput.setAttribute('value', nameInput.value.trim());
    // Neither the unsaved badge nor last-saved line belongs in the saved file.
    if (unsavedBadge) unsavedBadge.hidden = true;
    if (savedLine) savedLine.hidden = true;
    if (savedText) savedText.textContent = '';

    const previousRound = Number(root.dataset.replyRound) || 0;
    const round = savedRound || previousRound + 1;
    root.dataset.replyRound = String(round);
    const base = root.dataset.exportName || 'Hdiff_비교결과';
    const name = `${base}_회신${round}.html`;
    const markup = '<!doctype html>' + root.outerHTML;

    const restore = () => {
      root.dataset.replyRound = String(previousRound);
      if (unsavedBadge) unsavedBadge.hidden = !dirty;
      if (savedLine) savedLine.hidden = !savedText?.textContent;
    };

    if (window.showSaveFilePicker && !pickerBroken) {
      try {
        if (!saveHandle) {
          saveHandle = await window.showSaveFilePicker({
            suggestedName: name,
            types: [{description: 'HTML 문서', accept: {'text/html': ['.html']}}],
          });
        }
        const writable = await saveHandle.createWritable();
        await writable.write(markup);
        await writable.close();
        finishSave(round, `저장됨 · ${saveHandle.name} · ${stamp(new Date())}`, true);
        return;
      } catch (error) {
        // The reader closed the dialog: leave everything as it was.
        if (error && error.name === 'AbortError') {
          restore();
          return;
        }
        // The dialog is unavailable here (some browsers refuse it for local
        // files); stop asking for it and keep saving by download.
        saveHandle = null;
        pickerBroken = true;
      }
    }

    try {
      const url = URL.createObjectURL(new Blob([markup], {type: 'text/html;charset=utf-8'}));
      const link = document.createElement('a');
      link.href = url;
      link.download = name;
      document.body.append(link);
      link.click();
      link.remove();
      setTimeout(() => URL.revokeObjectURL(url), 5000);
      // A plain download reports nothing back, and this browser may still be
      // asking the reader where to put it, so do not claim it is on disk.
      finishSave(round, `내려받는 중 · ${name} — 브라우저 저장 창이 뜨면 저장을 눌러 주세요`, false);
    } catch (error) {
      restore();
      alert('이 브라우저에서 파일 저장이 막혀 있습니다. 다른 브라우저로 열어 주세요.');
    }
  }

  function finishSave(round, message, confirmed) {
    savedRound = round;
    dirty = false;
    if (unsavedBadge) unsavedBadge.hidden = true;
    if (savedText) savedText.textContent = message;
    if (savedLine) {
      savedLine.classList.toggle('pending', !confirmed);
      savedLine.hidden = false;
    }
    // Only a picked file can be written again without asking.
    if (relocateButton) relocateButton.hidden = !saveHandle;
  }

  document.getElementById('memo-save')?.addEventListener('click', saveWithReplies);
  relocateButton?.addEventListener('click', () => {
    saveHandle = null;
    relocateButton.hidden = true;
    saveWithReplies();
  });
  renumber();
})();
</script>
""";
}
