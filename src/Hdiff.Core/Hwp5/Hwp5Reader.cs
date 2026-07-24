using System.IO.Compression;
using System.Text;
using Hdiff.Core.Documents;

namespace Hdiff.Core.Hwp5;

/// <summary>
/// Direct reader for HWP 5.0 binary documents. It has no Hancom COM or native
/// dependency. The first supported surface is body paragraphs; unsupported
/// controls are reported as warnings rather than silently changing text.
/// </summary>
public sealed class Hwp5Reader
{
    private const int FileHeaderLength = 256;
    private const int CompressionFlag = 0x01;
    private const int ParaHeaderTag = 66;
    private const int ParaTextTag = 67;

    public ParsedDocument Read(string path)
    {
        using var compound = CompoundFileReader.Open(path);
        if (!compound.ContainsStream("FileHeader"))
            throw new DocumentReadException("HWP5 FileHeader stream이 없습니다.");

        var header = compound.ReadStream("FileHeader");
        if (header.Length < FileHeaderLength || !LooksLikeHwp5(header))
            throw new DocumentReadException("HWP 5.0 FileHeader가 아닙니다.");

        var flags = BitConverter.ToInt32(header, 36);
        if ((flags & 0x02) != 0)
            throw new DocumentReadException("암호가 설정된 HWP5 문서는 직접 읽을 수 없습니다.");

        var compressed = (flags & CompressionFlag) != 0;
        var sectionNames = compound.StreamNames
            .Where(IsSectionName)
            .OrderBy(SectionIndex)
            .ToArray();
        if (sectionNames.Length == 0)
            throw new DocumentReadException("HWP5 BodyText/Section stream을 찾지 못했습니다.");

        var blocks = new List<DocumentBlock>();
        var warnings = new List<string>();
        foreach (var sectionName in sectionNames)
        {
            var records = compound.ReadStream(sectionName);
            if (compressed) records = Inflate(records, sectionName);
            ParseSection(records, $"본문 {SectionIndex(sectionName) + 1}", blocks, warnings);
        }

        // HwpObject writes a final BreakPara when saving, which materializes
        // as one empty paragraph after the last real paragraph. It is an end
        // marker rather than report layout; retain meaningful internal blank
        // paragraphs but discard only document-edge blanks.
        TrimDocumentEdgeBlankParagraphs(blocks);

        if (blocks.Count == 0)
            throw new DocumentReadException("HWP5 본문에서 문단 텍스트를 찾지 못했습니다.");
        return new ParsedDocument(path, blocks, "HWP5 직접 파서", warnings);
    }

    private static bool LooksLikeHwp5(byte[] header)
    {
        var signature = Encoding.ASCII.GetString(header, 0, Math.Min(32, header.Length));
        return signature.StartsWith("HWP Document File", StringComparison.Ordinal);
    }

    private static bool IsSectionName(string name)
        => name.StartsWith("Section", StringComparison.OrdinalIgnoreCase) && SectionIndex(name) >= 0;

    private static int SectionIndex(string name)
        => int.TryParse(name.AsSpan("Section".Length), out var index) ? index : -1;

    private static void ParseSection(
        byte[] data,
        string sectionPath,
        List<DocumentBlock> blocks,
        List<string> warnings)
    {
        StringBuilder? current = null;
        var offset = 0;
        var unsupported = new HashSet<int>();

        while (offset + 4 <= data.Length)
        {
            var packed = BitConverter.ToUInt32(data, offset);
            offset += 4;
            var tag = (int)(packed & 0x3FF);
            var size = (int)((packed >> 20) & 0xFFF);
            if (size == 0xFFF)
            {
                if (offset + 4 > data.Length) throw new DocumentReadException("HWP5 extended record 길이가 잘렸습니다.");
                size = BitConverter.ToInt32(data, offset);
                offset += 4;
            }
            if (size < 0 || offset + size > data.Length)
                throw new DocumentReadException("HWP5 record 길이가 본문 범위를 벗어났습니다.");

            var payload = data.AsSpan(offset, size);
            offset += size;

            if (tag == ParaHeaderTag)
            {
                FlushCurrent();
                current = new StringBuilder();
            }
            else if (tag == ParaTextTag)
            {
                current ??= new StringBuilder();
                current.Append(DecodeParaText(payload));
            }
            else if (tag is not 68 and not 69 and not 70 and not 71 and not 72 and not 73 and not 74 and not 75 and not 76 and not 77)
            {
                unsupported.Add(tag);
            }
        }
        FlushCurrent();

        if (unsupported.Count > 0)
            warnings.Add($"{sectionPath}: 해석하지 않은 HWP record tag {string.Join(", ", unsupported.Order())}");

        void FlushCurrent()
        {
            if (current is null) return;
            var text = Normalize(current.ToString());
            // An empty HWP paragraph is meaningful source layout. Retain it in
            // the model; DocumentDiffer decides whether its blank line should
            // participate in a particular comparison.
            blocks.Add(new DocumentBlock(DocumentBlockKind.Paragraph, text, SectionPath: sectionPath));
            current = null;
        }
    }

    private static string DecodeParaText(ReadOnlySpan<byte> payload)
    {
        var sb = new StringBuilder(payload.Length / 2);
        for (var i = 0; i + 1 < payload.Length; i += 2)
        {
            var value = (char)(payload[i] | payload[i + 1] << 8);
            switch (value)
            {
                case '\r':
                case '\n':
                    sb.Append('\n');
                    break;
                case >= '\u0001' and <= '\u0008':
                    // HWP5 extended controls (section definition, fields,
                    // etc.) occupy 16 bytes in the para-text stream: the
                    // WCHAR control marker plus 14 payload bytes. Consuming
                    // the payload prevents its control IDs from being read as
                    // gibberish text. Hancom's format guide identifies code
                    // 2 as the section/column-definition control.
                    i += 14;
                    break;
                case '\t':
                    sb.Append('\t');
                    break;
                case >= ' ':
                    sb.Append(value);
                    break;
                // Other controls are omitted until their record-specific
                // payloads are implemented (tables, fields, drawings).
            }
        }
        return sb.ToString();
    }

    private static string Normalize(string text)
        => string.Join(" ", text.Replace('\r', '\n').Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

    private static void TrimDocumentEdgeBlankParagraphs(List<DocumentBlock> blocks)
    {
        while (blocks.Count > 0 && blocks[0].Kind == DocumentBlockKind.Paragraph && string.IsNullOrWhiteSpace(blocks[0].Text))
            blocks.RemoveAt(0);
        while (blocks.Count > 0 && blocks[^1].Kind == DocumentBlockKind.Paragraph && string.IsNullOrWhiteSpace(blocks[^1].Text))
            blocks.RemoveAt(blocks.Count - 1);
    }

    private static byte[] Inflate(byte[] compressed, string sectionName)
    {
        try
        {
            using var input = new MemoryStream(compressed, writable: false);
            using var inflater = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            inflater.CopyTo(output);
            return output.ToArray();
        }
        catch (InvalidDataException first)
        {
            try
            {
                using var input = new MemoryStream(compressed, writable: false);
                using var inflater = new ZLibStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                inflater.CopyTo(output);
                return output.ToArray();
            }
            catch (InvalidDataException)
            {
                throw new DocumentReadException($"{sectionName}: HWP5 압축 해제 실패", first);
            }
        }
    }
}
