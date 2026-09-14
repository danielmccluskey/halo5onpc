using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace H5SoloLauncher.Core.Content;

public sealed record ModuleEntry(int Index, string Name, int Parent, int ResourceIndex, int ResourceCount,
    int BlockIndex, int BlockCount, long DataOffset, long StoredSize, long LogicalSize, int Flags,
    string Group, string TagId, string AssetId, string Checksum, byte[] Raw);
public sealed record ModuleBlock(int Index, long StoredOffset, long StoredSize, long LogicalOffset, long LogicalSize, int Flags, byte[] Raw);
public sealed record AudioEntry(string Kind, string Id, long Language, long BlockSize, long Offset, long Length);

/// <summary>Reads tables only. No module decompression or large media payload reads.</summary>
public sealed class FileMetadata
{
    public const int MaximumTableBytes = 128 * 1024 * 1024;
    private readonly byte[] data;
    public string Kind { get; }
    public long FileLength { get; }
    public long Ticks { get; }
    public string Digest { get; }
    public int Revision { get; }
    public int ItemCount { get; }
    public int ResourceCount { get; }
    public int BlockCount { get; }
    public int TableBytes => data.Length;
    public int HeaderSize => Revision == 27 ? 56 : 48;
    public int BlockSize => Revision == 27 ? 32 : 20;
    private int StringStart => HeaderSize + ItemCount * 88;
    private int ResourceStart => StringStart + Int(data, 28);
    private int BlockStart => ResourceStart + ResourceCount * 4;
    public byte[] Header => Kind == "Module" ? data[..HeaderSize] : data;
    public byte[] Tables => (byte[])data.Clone();

    public static int ModuleTableLength(byte[] header, long fileLength)
    {
        if (header.Length < 48 || !header.AsSpan(0, 4).SequenceEqual("mohd"u8)) Invalid("Module header is not recognized.");
        var revision = Int(header, 4);
        if (revision is not (23 or 27)) Invalid($"Module revision {revision} is not supported.");
        var items = Int(header, 16); var strings = Int(header, 28); var resources = Int(header, 32); var blocks = Int(header, 36);
        if (items < 0 || strings < 0 || resources < 0 || blocks < 0) Invalid("Module table counts are negative.");
        var count = (revision == 27 ? 56L : 48L) + items * 88L + strings + resources * 4L + blocks * (revision == 27 ? 32L : 20L);
        if (count > fileLength || count > MaximumTableBytes) Invalid("Module tables are truncated or exceed 128 MiB.");
        return (int)count;
    }

    public static FileMetadata FromModuleTables(byte[] tables, long originalLength, long ticks)
    {
        if (ModuleTableLength(tables, originalLength) != tables.Length) Invalid("Module table length does not match its header.");
        return new("Module", (byte[])tables.Clone(), originalLength, ticks);
    }

    public static int AudioTableLength(byte[] header, long fileLength)
    {
        if (header.Length < 28 || !header.AsSpan(0, 4).SequenceEqual("AKPK"u8) || UInt(header, 8) != 1) Invalid("Audio package header is not recognized.");
        var length = UInt(header, 4) + 8L;
        if (length != 28L + UInt(header, 12) + UInt(header, 16) + UInt(header, 20) + UInt(header, 24) || length > fileLength || length > MaximumTableBytes)
            Invalid("Audio tables are truncated or exceed the supported size.");
        return checked((int)length);
    }

    public static FileMetadata FromAudioTables(byte[] tables, long originalLength, long ticks)
    {
        if (AudioTableLength(tables, originalLength) != tables.Length) Invalid("Audio table length does not match its header.");
        var result = new FileMetadata("Audio", (byte[])tables.Clone(), originalLength, ticks);
        var entries = result.AudioEntries().ToArray();
        if (entries.Select(x => (x.Kind, x.Id, x.Language)).Distinct().Count() != entries.Length) Invalid("Audio package contains duplicate identities.");
        return result;
    }

    private FileMetadata(string kind, byte[] data, long length, long ticks)
    {
        Kind = kind; this.data = data; FileLength = length; Ticks = ticks;
        Digest = Convert.ToHexString(SHA256.HashData(data));
        if (kind == "Module")
        { Revision = Int(data, 4); ItemCount = Int(data, 16); ResourceCount = Int(data, 32); BlockCount = Int(data, 36); }
    }

    public static FileMetadata Read(string path, string kind, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var before = new FileInfo(path);
        var ticks = before.LastWriteTimeUtc.Ticks;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var length = stream.Length;
        long count = kind == "Movie" ? 0 : length;
        if (kind == "Module")
        {
            var header = new byte[48]; stream.ReadExactly(header);
            if (!header.AsSpan(0, 4).SequenceEqual("mohd"u8)) Invalid("Module signature is not recognized.");
            var revision = Int(header, 4);
            if (revision is not (23 or 27)) Invalid($"Module revision {revision} is not supported.");
            var items = Int(header, 16); var strings = Int(header, 28); var resources = Int(header, 32); var blocks = Int(header, 36);
            if (items < 0 || strings < 0 || resources < 0 || blocks < 0) Invalid("Module table counts are negative.");
            count = (revision == 27 ? 56L : 48L) + items * 88L + strings + resources * 4L + blocks * (revision == 27 ? 32L : 20L);
        }
        else if (kind == "Audio")
        {
            var header = new byte[28]; stream.ReadExactly(header);
            if (!header.AsSpan(0, 4).SequenceEqual("AKPK"u8) || UInt(header, 8) != 1) Invalid("Audio package header is not recognized.");
            count = UInt(header, 4) + 8L;
            if (count != 28L + UInt(header, 12) + UInt(header, 16) + UInt(header, 20) + UInt(header, 24)) Invalid("Audio table sizes do not agree.");
        }
        if (count > length || count > MaximumTableBytes || count < 0) Invalid("Metadata tables are truncated or exceed the supported 128 MiB table limit.");
        var data = new byte[(int)count];
        stream.Position = 0;
        for (var offset = 0; offset < data.Length;)
        {
            cancellation.ThrowIfCancellationRequested();
            var chunk = Math.Min(1024 * 1024, data.Length - offset);
            stream.ReadExactly(data.AsSpan(offset, chunk)); offset += chunk;
        }
        before.Refresh();
        if (before.Length != length || before.LastWriteTimeUtc.Ticks != ticks)
            throw new CacheException("DUMP_CHANGED", "A source file changed while its metadata was being read. Let extraction finish, then resume.");
        return new(kind, data, length, ticks);
    }

    public ModuleEntry Entry(int index)
    {
        var raw = data.AsSpan(HeaderSize + index * 88, 88).ToArray();
        var nameOffset = Int(raw, 0); var stringLength = Int(data, 28);
        if (nameOffset < 0 || nameOffset >= stringLength) Invalid($"Entry {index} has an invalid name offset.");
        var start = StringStart + nameOffset;
        var end = Array.IndexOf(data, (byte)0, start, stringLength - nameOffset);
        if (end < 0) Invalid($"Entry {index} has an unterminated name.");
        var parent = Int(raw, 4); var rc = Int(raw, 8); var ri = Int(raw, 12); var bc = Int(raw, 16); var bi = Int(raw, 20);
        if (parent < -1 || parent >= ItemCount || parent == index || rc < 0 || ri < 0 || ri + (long)rc > ResourceCount || bc < 0 || bi < 0 || bi + (long)bc > BlockCount)
            Invalid($"Entry {index} has invalid parent, resource or block links.");
        var offset = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(24));
        var stored = UInt(raw, 32); var logical = UInt(raw, 36);
        if (stored > 0 && (offset < 0 || offset > FileLength - TableBytes || stored > FileLength - TableBytes - offset)) Invalid($"Entry {index} points outside the module.");
        var group = raw.AsSpan(64, 4).ToArray(); Array.Reverse(group);
        return new(index, new UTF8Encoding(false, true).GetString(data, start, end - start), parent, ri, rc, bi, bc, offset, stored, logical,
            raw[43], Encoding.ASCII.GetString(group), UInt(raw, 44).ToString("x8"),
            BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(48)).ToString("x16"),
            BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(56)).ToString("x16"), raw);
    }

    public ModuleBlock Block(int index)
    {
        var raw = data.AsSpan(BlockStart + index * BlockSize, BlockSize).ToArray();
        var offset = BlockSize == 32 ? 8 : 0;
        var flags = UInt(raw, offset + 16);
        if (flags > 1) Invalid($"Block {index} has an unknown compression flag.");
        return new(index, UInt(raw, offset), UInt(raw, offset + 4), UInt(raw, offset + 8), UInt(raw, offset + 12), (int)flags, raw);
    }

    public int Resource(int index)
    {
        var target = Int(data, ResourceStart + index * 4);
        if (target < 0 || target >= ItemCount) Invalid($"Resource {index} points outside the item table.");
        return target;
    }

    public IEnumerable<AudioEntry> AudioEntries()
    {
        var offset = checked(28 + (int)UInt(data, 12));
        foreach (var (kind, lengthOffset, stride) in new[] { ("Bank", 16, 20), ("Media", 20, 20), ("External", 24, 24) })
        {
            var length = UInt(data, lengthOffset);
            if (length == 0) continue;
            if (length < 4 || offset > data.Length - 4) Invalid("Audio entry table is truncated.");
            var count = UInt(data, offset);
            if (length != 4L + count * stride || offset + (long)length > data.Length) Invalid("Audio entry counts do not match the table size.");
            for (var i = 0; i < count; i++) yield return ReadAudio(kind, offset + 4 + i * stride, stride);
            offset += (int)length;
        }
    }

    private AudioEntry ReadAudio(string kind, int offset, int stride)
    {
        var id = stride == 24 ? BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset)) : UInt(data, offset);
        var at = offset + (stride == 24 ? 8 : 4);
        long block = UInt(data, at), size = UInt(data, at + 4), start = UInt(data, at + 8), language = UInt(data, at + 12);
        var position = checked(start * block);
        if ((block == 0 && size > 0) || position > FileLength || size > FileLength - position || (size > 0 && position < data.Length)) Invalid("An audio entry points outside its payload area.");
        return new(kind, id.ToString("x16"), language, block, position, size);
    }

    private static int Int(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
    private static uint UInt(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static void Invalid(string message) => throw new CacheException("METADATA_INVALID", message);
}
