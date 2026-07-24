using System.IO.Compression;
using System.Xml.Linq;
using Hdiff.Core.Documents;

namespace Hdiff.Core.Hwpx;

public sealed class HwpxReader
{
    public ParsedDocument Read(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var sections = archive.Entries
                .Where(e => Path.GetFileNameWithoutExtension(e.Name).StartsWith("Section", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (sections.Length == 0) throw new DocumentReadException("HWPX Section XML이 없습니다.");

            var blocks = new List<DocumentBlock>();
            foreach (var entry in sections)
            {
                using var stream = entry.Open();
                var xml = XDocument.Load(stream);
                ParseChildren(xml.Root, entry.Name, blocks);
            }
            if (blocks.Count == 0) throw new DocumentReadException("HWPX 본문 텍스트를 찾지 못했습니다.");
            return new ParsedDocument(path, blocks, "HWPX 직접 파서", Array.Empty<string>());
        }
        catch (InvalidDataException ex)
        {
            throw new DocumentReadException("HWPX ZIP을 열 수 없습니다. DRM 또는 손상 파일일 수 있습니다.", ex);
        }
    }

    private static void ParseChildren(XElement? element, string section, List<DocumentBlock> blocks)
    {
        if (element is null) return;
        foreach (var child in element.Elements())
        {
            switch (child.Name.LocalName.ToLowerInvariant())
            {
                case "p":
                case "para":
                    var text = Normalize(string.Concat(child.Descendants().Where(e => e.Name.LocalName is "t" or "T" or "text").Select(e => e.Value)));
                    // Preserve a deliberately blank paragraph. The comparison
                    // option, rather than XML parsing, controls whether it is
                    // visible as a diff row.
                    blocks.Add(new DocumentBlock(DocumentBlockKind.Paragraph, text, SectionPath: section));
                    break;
                case "tbl":
                case "table":
                    var rows = child.Descendants().Where(e => e.Name.LocalName is "tr" or "Tr")
                        .Select(row => (IReadOnlyList<string>)row.Elements().Where(e => e.Name.LocalName is "tc" or "Tc")
                            .Select(cell => Normalize(string.Concat(cell.Descendants().Where(e => e.Name.LocalName is "t" or "T" or "text").Select(e => e.Value))))
                            .ToArray())
                        .Where(row => row.Count > 0)
                        .ToArray();
                    if (rows.Length > 0) blocks.Add(new DocumentBlock(DocumentBlockKind.Table, "", rows, section));
                    break;
                default:
                    ParseChildren(child, section, blocks);
                    break;
            }
        }
    }

    private static string Normalize(string value) => string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
