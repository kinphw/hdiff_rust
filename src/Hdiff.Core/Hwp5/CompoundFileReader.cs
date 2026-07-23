using System.Buffers.Binary;
using System.Text;
using Hdiff.Core.Documents;

namespace Hdiff.Core.Hwp5;

/// <summary>
/// Read-only Compound File Binary Format reader. HWP 5.0 is an OLE compound
/// file, so this is the only container dependency needed by the direct reader.
/// It intentionally exposes streams by name and does not mutate the source.
/// </summary>
internal sealed class CompoundFileReader : IDisposable
{
    private const int FreeSect = -1;
    private const int EndOfChain = -2;
    private const int FatSect = -3;
    private const int DifSect = -4;
    private const int NoStream = -1;
    private const uint CompoundSignatureLow = 0xE011CFD0;
    private const uint CompoundSignatureHigh = 0xE11AB1A1;

    private readonly FileStream _stream;
    private readonly int _sectorSize;
    private readonly int _miniSectorSize;
    private readonly int _miniStreamCutoff;
    private readonly int[] _fat;
    private readonly int[] _miniFat;
    private readonly byte[] _miniStream;
    private readonly Dictionary<string, DirectoryEntry> _streams;

    private sealed record DirectoryEntry(string Name, byte Type, int StartSector, long Length);

    private CompoundFileReader(
        FileStream stream,
        int sectorSize,
        int miniSectorSize,
        int miniStreamCutoff,
        int[] fat,
        int[] miniFat,
        byte[] miniStream,
        Dictionary<string, DirectoryEntry> streams)
    {
        _stream = stream;
        _sectorSize = sectorSize;
        _miniSectorSize = miniSectorSize;
        _miniStreamCutoff = miniStreamCutoff;
        _fat = fat;
        _miniFat = miniFat;
        _miniStream = miniStream;
        _streams = streams;
    }

    public static CompoundFileReader Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            var header = ReadExactly(stream, 512);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4)) != CompoundSignatureLow ||
                BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4)) != CompoundSignatureHigh)
                throw new DocumentReadException("OLE Compound File 서명이 아닙니다.");

            var sectorShift = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x1E, 2));
            var miniSectorShift = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x20, 2));
            if (sectorShift is not (9 or 12) || miniSectorShift != 6)
                throw new DocumentReadException("지원하지 않는 OLE sector 크기입니다.");

            var sectorSize = 1 << sectorShift;
            var miniSectorSize = 1 << miniSectorShift;
            var fatCount = ReadInt32(header, 0x2C);
            var firstDirectory = ReadInt32(header, 0x30);
            var miniStreamCutoff = ReadInt32(header, 0x38);
            var firstMiniFat = ReadInt32(header, 0x3C);
            var miniFatCount = ReadInt32(header, 0x40);
            var firstDifat = ReadInt32(header, 0x44);
            var difatCount = ReadInt32(header, 0x48);

            if (fatCount < 0 || fatCount > 10_000 || miniFatCount < 0 || miniFatCount > 10_000 || difatCount < 0 || difatCount > 10_000)
                throw new DocumentReadException("OLE FAT 메타데이터가 비정상입니다.");

            var sectorCount = checked((int)((stream.Length - 512) / sectorSize));
            if (sectorCount <= 0)
                throw new DocumentReadException("OLE sector가 없습니다.");

            var fatSectors = new List<int>(fatCount);
            for (var i = 0; i < 109 && fatSectors.Count < fatCount; i++)
            {
                var sid = ReadInt32(header, 0x4C + i * 4);
                if (sid >= 0) fatSectors.Add(sid);
            }

            var nextDifat = firstDifat;
            for (var i = 0; i < difatCount && fatSectors.Count < fatCount; i++)
            {
                ValidateSector(nextDifat, sectorCount, "DIFAT");
                var data = ReadSector(stream, nextDifat, sectorSize);
                var difatEntries = sectorSize / 4 - 1;
                for (var j = 0; j < difatEntries && fatSectors.Count < fatCount; j++)
                {
                    var sid = ReadInt32(data, j * 4);
                    if (sid >= 0) fatSectors.Add(sid);
                }
                nextDifat = ReadInt32(data, sectorSize - 4);
                if (nextDifat == EndOfChain) break;
            }

            if (fatSectors.Count != fatCount)
                throw new DocumentReadException("OLE FAT sector 목록이 불완전합니다.");

            var fat = new List<int>(fatCount * (sectorSize / 4));
            foreach (var sid in fatSectors)
            {
                ValidateSector(sid, sectorCount, "FAT");
                var data = ReadSector(stream, sid, sectorSize);
                for (var i = 0; i < data.Length; i += 4)
                    fat.Add(ReadInt32(data, i));
            }

            var reader = new ContainerReader(stream, sectorSize, fat.ToArray(), sectorCount);
            var directoryBytes = reader.ReadRegularChain(firstDirectory, maxBytes: 16 * 1024 * 1024);
            var entries = ParseDirectory(directoryBytes);
            var root = entries.FirstOrDefault(e => e.Type == 5)
                ?? throw new DocumentReadException("OLE Root Entry가 없습니다.");

            var miniFatBytes = miniFatCount == 0
                ? Array.Empty<byte>()
                : reader.ReadRegularChain(firstMiniFat, maxBytes: checked(miniFatCount * sectorSize));
            var miniFat = new int[miniFatBytes.Length / 4];
            for (var i = 0; i < miniFat.Length; i++) miniFat[i] = ReadInt32(miniFatBytes, i * 4);

            var miniStream = root.Length == 0
                ? Array.Empty<byte>()
                : reader.ReadRegularChain(root.StartSector, maxBytes: checked((int)Math.Min(root.Length, 256L * 1024 * 1024)));
            if (miniStream.Length > root.Length) Array.Resize(ref miniStream, checked((int)root.Length));

            var streams = entries
                .Where(e => e.Type == 2)
                .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            return new CompoundFileReader(stream, sectorSize, miniSectorSize, miniStreamCutoff, fat.ToArray(), miniFat, miniStream, streams);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public bool ContainsStream(string name) => _streams.ContainsKey(name);

    public IReadOnlyList<string> StreamNames => _streams.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

    public byte[] ReadStream(string name)
    {
        if (!_streams.TryGetValue(name, out var entry))
            throw new DocumentReadException($"OLE stream을 찾을 수 없습니다: {name}");
        if (entry.Length < 0 || entry.Length > 512L * 1024 * 1024)
            throw new DocumentReadException($"OLE stream 길이가 비정상입니다: {name}");
        if (entry.Length == 0) return Array.Empty<byte>();

        if (entry.Length < _miniStreamCutoff)
            return ReadMiniChain(entry.StartSector, checked((int)entry.Length));
        return ReadRegularChain(entry.StartSector, checked((int)entry.Length));
    }

    private byte[] ReadRegularChain(int startSector, int expectedLength)
    {
        using var ms = new MemoryStream(expectedLength);
        var visited = new HashSet<int>();
        var sid = startSector;
        while (sid != EndOfChain && ms.Length < expectedLength)
        {
            ValidateSector(sid, _fat.Length, "stream");
            if (!visited.Add(sid)) throw new DocumentReadException("OLE stream chain에 순환이 있습니다.");
            var sector = ReadSector(_stream, sid, _sectorSize);
            ms.Write(sector, 0, sector.Length);
            sid = _fat[sid];
        }
        if (ms.Length < expectedLength)
            throw new DocumentReadException("OLE stream chain이 예상보다 짧습니다.");
        return ms.ToArray()[..expectedLength];
    }

    private byte[] ReadMiniChain(int startSector, int expectedLength)
    {
        if (_miniStream.Length == 0 || _miniFat.Length == 0)
            throw new DocumentReadException("OLE mini stream 정보가 없습니다.");

        using var ms = new MemoryStream(expectedLength);
        var visited = new HashSet<int>();
        var sid = startSector;
        while (sid != EndOfChain && ms.Length < expectedLength)
        {
            if (sid < 0 || sid >= _miniFat.Length)
                throw new DocumentReadException("OLE mini stream sector가 범위를 벗어났습니다.");
            if (!visited.Add(sid)) throw new DocumentReadException("OLE mini stream chain에 순환이 있습니다.");
            var offset = checked(sid * _miniSectorSize);
            if (offset + _miniSectorSize > _miniStream.Length)
                throw new DocumentReadException("OLE mini stream 데이터가 잘렸습니다.");
            ms.Write(_miniStream, offset, _miniSectorSize);
            sid = _miniFat[sid];
        }
        if (ms.Length < expectedLength)
            throw new DocumentReadException("OLE mini stream chain이 예상보다 짧습니다.");
        return ms.ToArray()[..expectedLength];
    }

    public void Dispose() => _stream.Dispose();

    private sealed class ContainerReader(FileStream stream, int sectorSize, int[] fat, int sectorCount)
    {
        public byte[] ReadRegularChain(int startSector, int maxBytes)
        {
            using var ms = new MemoryStream();
            var visited = new HashSet<int>();
            var sid = startSector;
            while (sid != EndOfChain)
            {
                ValidateSector(sid, sectorCount, "chain");
                if (!visited.Add(sid)) throw new DocumentReadException("OLE chain에 순환이 있습니다.");
                var sector = ReadSector(stream, sid, sectorSize);
                ms.Write(sector, 0, sector.Length);
                // A regular chain always ends on a sector boundary, while the
                // directory's advertised stream length may end inside it.
                if (ms.Length > (long)maxBytes + sectorSize)
                    throw new DocumentReadException("OLE chain이 허용 한도를 초과했습니다.");
                sid = fat[sid];
            }
            return ms.ToArray();
        }
    }

    private static List<DirectoryEntry> ParseDirectory(byte[] data)
    {
        var result = new List<DirectoryEntry>();
        for (var offset = 0; offset + 128 <= data.Length; offset += 128)
        {
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 64, 2));
            var type = data[offset + 66];
            if (type == 0 || nameLength < 2 || nameLength > 64) continue;
            var chars = (nameLength - 2) / 2;
            var name = Encoding.Unicode.GetString(data, offset, chars * 2);
            var start = ReadInt32(data, offset + 116);
            var length = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset + 120, 8));
            result.Add(new DirectoryEntry(name, type, start, length));
        }
        return result;
    }

    private static int ReadInt32(byte[] data, int offset) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));

    private static byte[] ReadSector(FileStream stream, int sid, int sectorSize)
    {
        var offset = checked(512L + (long)sid * sectorSize);
        if (offset < 512 || offset + sectorSize > stream.Length)
            throw new DocumentReadException("OLE sector가 파일 범위를 벗어났습니다.");
        stream.Position = offset;
        return ReadExactly(stream, sectorSize);
    }

    private static byte[] ReadExactly(Stream stream, int length)
    {
        var data = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = stream.Read(data, read, length - read);
            if (n == 0) throw new DocumentReadException("파일이 예기치 않게 끝났습니다.");
            read += n;
        }
        return data;
    }

    private static void ValidateSector(int sid, int sectorCount, string label)
    {
        if (sid < 0 || sid >= sectorCount)
            throw new DocumentReadException($"OLE {label} sector가 범위를 벗어났습니다.");
    }
}
