using System.Buffers.Binary;
using System.Text;

namespace Hdiff.Core.Hwp5;

/// <summary>
/// Creates a small, deterministic HWP5 compound-file fixture for parser tests.
/// It is deliberately a binary-reader fixture, not a general HWP authoring API.
/// </summary>
public static class Hwp5FixtureWriter
{
    private const int SectorSize = 512;
    private const int MiniSectorSize = 64;
    private const int FreeSect = -1;
    private const int EndOfChain = -2;
    private const int FatSect = -3;
    private const int NoStream = -1;

    public static void Write(string path, params string[] paragraphs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (paragraphs.Length == 0) throw new ArgumentException("문단이 하나 이상 필요합니다.", nameof(paragraphs));

        WriteParaTextPayloads(
            path,
            paragraphs.Select(paragraph => Encoding.Unicode.GetBytes(paragraph ?? string.Empty)).ToArray());
    }

    /// <summary>
    /// Writes raw HWPTAG_PARA_TEXT payloads for byte-level parser regression tests.
    /// Each payload must contain complete UTF-16LE WCHAR data.
    /// </summary>
    public static void WriteParaTextPayloads(string path, params byte[][] paragraphPayloads)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (paragraphPayloads.Length == 0)
            throw new ArgumentException("문단 payload가 하나 이상 필요합니다.", nameof(paragraphPayloads));
        if (paragraphPayloads.Any(payload => payload is null || payload.Length % sizeof(char) != 0))
            throw new ArgumentException("문단 payload는 완전한 UTF-16LE WCHAR 배열이어야 합니다.", nameof(paragraphPayloads));

        var fileHeader = CreateFileHeader();
        var section = CreateSection(paragraphPayloads);
        var fileHeaderMiniCount = DivRoundUp(fileHeader.Length, MiniSectorSize);
        var sectionMiniStart = fileHeaderMiniCount;
        var sectionMiniCount = DivRoundUp(section.Length, MiniSectorSize);
        var miniStreamLength = (fileHeaderMiniCount + sectionMiniCount) * MiniSectorSize;

        // Four normal sectors: directory, mini FAT, root mini-stream, FAT.
        var header = new byte[SectorSize];
        WriteHeader(header, firstDirectorySector: 0, firstMiniFatSector: 1, firstMiniStreamSector: 2, miniStreamLength, fatSector: 3);

        var directory = new byte[SectorSize];
        WriteDirectoryEntry(directory, 0, "Root Entry", type: 5, child: 1, right: NoStream, start: 2, length: miniStreamLength);
        WriteDirectoryEntry(directory, 1, "FileHeader", type: 2, child: NoStream, right: 2, start: 0, length: fileHeader.Length);
        WriteDirectoryEntry(directory, 2, "BodyText", type: 1, child: 3, right: NoStream, start: EndOfChain, length: 0);
        WriteDirectoryEntry(directory, 3, "Section0", type: 2, child: NoStream, right: NoStream, start: sectionMiniStart, length: section.Length);

        var miniFat = Enumerable.Repeat(FreeSect, SectorSize / 4).ToArray();
        Chain(miniFat, 0, fileHeaderMiniCount);
        Chain(miniFat, sectionMiniStart, sectionMiniCount);
        var miniFatBytes = ToBytes(miniFat);

        var miniStream = new byte[SectorSize];
        Buffer.BlockCopy(fileHeader, 0, miniStream, 0, fileHeader.Length);
        Buffer.BlockCopy(section, 0, miniStream, sectionMiniStart * MiniSectorSize, section.Length);

        var fat = Enumerable.Repeat(FreeSect, SectorSize / 4).ToArray();
        fat[0] = EndOfChain;
        fat[1] = EndOfChain;
        fat[2] = EndOfChain;
        fat[3] = FatSect;
        var fatBytes = ToBytes(fat);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        output.Write(header);
        output.Write(directory);
        output.Write(miniFatBytes);
        output.Write(miniStream);
        output.Write(fatBytes);
    }

    private static byte[] CreateFileHeader()
    {
        var result = new byte[256];
        Encoding.ASCII.GetBytes("HWP Document File").CopyTo(result, 0);
        // HWP 5.0.3.2, represented in file order. Compression and encryption are off.
        result[32] = 0x05;
        result[33] = 0x00;
        result[34] = 0x03;
        result[35] = 0x02;
        return result;
    }

    private static byte[] CreateSection(IEnumerable<byte[]> paragraphPayloads)
    {
        using var ms = new MemoryStream();
        foreach (var paraText in paragraphPayloads)
        {
            var paraHeader = new byte[22];
            BinaryPrimitives.WriteUInt32LittleEndian(
                paraHeader.AsSpan(0, 4),
                checked((uint)(paraText.Length / sizeof(char))));
            WriteRecord(ms, tag: 66, level: 0, payload: paraHeader);
            WriteRecord(ms, tag: 67, level: 1, payload: paraText);
        }
        return ms.ToArray();
    }

    private static void WriteRecord(Stream stream, int tag, int level, byte[] payload)
    {
        if (payload.Length >= 0xFFF) throw new ArgumentOutOfRangeException(nameof(payload));
        var packed = (uint)tag | ((uint)level << 10) | ((uint)payload.Length << 20);
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, packed);
        stream.Write(header);
        stream.Write(payload);
    }

    private static void WriteHeader(byte[] header, int firstDirectorySector, int firstMiniFatSector, int firstMiniStreamSector, int miniStreamLength, int fatSector)
    {
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x18, 2), 0x003E);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x1A, 2), 0x0003);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x1C, 2), 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x1E, 2), 9);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x20, 2), 6);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x2C, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x30, 4), firstDirectorySector);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x38, 4), 4096);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x3C, 4), firstMiniFatSector);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x40, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x44, 4), EndOfChain);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x48, 4), 0);
        for (var i = 0; i < 109; i++)
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x4C + i * 4, 4), i == 0 ? fatSector : FreeSect);
    }

    private static void WriteDirectoryEntry(byte[] directory, int index, string name, byte type, int child, int right, int start, long length)
    {
        var offset = index * 128;
        var encoded = Encoding.Unicode.GetBytes(name + '\0');
        encoded.CopyTo(directory, offset);
        BinaryPrimitives.WriteUInt16LittleEndian(directory.AsSpan(offset + 64, 2), checked((ushort)encoded.Length));
        directory[offset + 66] = type;
        directory[offset + 67] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(directory.AsSpan(offset + 68, 4), NoStream);
        BinaryPrimitives.WriteInt32LittleEndian(directory.AsSpan(offset + 72, 4), right);
        BinaryPrimitives.WriteInt32LittleEndian(directory.AsSpan(offset + 76, 4), child);
        BinaryPrimitives.WriteInt32LittleEndian(directory.AsSpan(offset + 116, 4), start);
        BinaryPrimitives.WriteInt64LittleEndian(directory.AsSpan(offset + 120, 8), length);
    }

    private static void Chain(int[] fat, int start, int count)
    {
        for (var i = 0; i < count; i++) fat[start + i] = i == count - 1 ? EndOfChain : start + i + 1;
    }

    private static byte[] ToBytes(IEnumerable<int> values)
    {
        var data = new byte[SectorSize];
        var index = 0;
        foreach (var value in values)
        {
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(index, 4), value);
            index += 4;
        }
        return data;
    }

    private static int DivRoundUp(int value, int divisor) => (value + divisor - 1) / divisor;
}
