using System.Buffers.Binary;
using H5SoloLauncher.Core.Content;

namespace H5SoloLauncher.Core.Conversion;

public sealed record ConvertedBitmap(byte[] Resource, byte[][] Chunks, BitmapDescriptor Descriptor);
public static class BitmapConverter
{
    public const string SourceTagSchema = "5a8d019491b343b0", NativeTagSchema = "ffe30ac53257ec83";
    public static ConvertedBitmap Convert(ReadOnlySpan<byte> image, TagDocument resource, byte[][] chunks, TextureAddresses addresses, CancellationToken cancellation = default)
    {
        var descriptor = BitmapDescriptor.Read(image, resource); var n = chunks.Length;
        if (resource.Metadata.Schema != "704947f99b79968a" || resource.Metadata.Dependencies.Length != 0 || resource.References.Length != 0 || resource.Structures.Length != 2 ||
            resource.DataReferences.Length != 1 || resource.Blocks[0] != new TagBlock(80, 1, 0, resource.Blocks[0].TableOffset) || resource.Blocks.Length != (n == 0 ? 2 : 3)) throw Unsupported("Unexpected bitmap resource structure.");
        var root = resource.Block(0);
        if (n >= descriptor.Mips || U(root, 64) != n || resource.Structures[1].FieldOffset != 48 || resource.Structures[1].FieldBlock != 0 || resource.Structures[1].Target != (n > 0 ? 1 : -1)) throw Unsupported("Unexpected bitmap streaming layout.");
        if (descriptor.Kind == 1 && (descriptor.Format != 11 || descriptor.Mips != 1 || n != 0 || descriptor.Width > 256 || descriptor.Height > 256 || root[33] != 3)) throw Unsupported("This volume texture needs a separately validated converter.");
        if (descriptor.Kind != 1 && root[33] is not (0 or 2)) throw Unsupported("The bitmap resource kind is unsupported.");
        if (descriptor.Kind == 2 && n > 0) throw Unsupported("Streamed cubemaps need a separately validated converter.");
        if (descriptor.Kind == 3 && descriptor.Depth > 1 && descriptor.Mips > 1 && descriptor.Format is not (2 or 3 or 16 or 39 or 49)) throw Unsupported("This texture array format needs separate validation.");
        if (n > 0)
        {
            if (resource.Block(1).Length != n * 8) throw Unsupported("Streaming metadata count differs from its chunks.");
            for (var i = 0; i < n; i++) if (U(resource.Block(1), i * 8 + 4) != (chunks[i].LongLength + 65535) / 65536) throw Unsupported("A streamed texture chunk has a different size from its metadata.");
        }
        var tail = resource.Block(n > 0 ? 2 : 1); var total = tail.Length + chunks.Sum(x => (long)x.Length);
        if (total > ModulePayloadReader.MaximumResourceBytes) throw Unsupported("This tiled texture exceeds the conversion size limit.");
        var tiled = new byte[(int)total]; var at = 0;
        foreach (var chunk in chunks.Reverse()) { chunk.CopyTo(tiled, at); at += chunk.Length; } tail.CopyTo(tiled.AsSpan(at));
        var mips = addresses.Untile(descriptor, tiled, cancellation);
        for (var i = 0; i < n; i++) if (mips[i].Length > chunks[n - i - 1].Length) throw Unsupported("A converted mip exceeds its source streamed chunk.");
        var newChunks = mips.Take(n).Reverse().ToArray(); var newTailLength = mips.Skip(n).Sum(x => x.Length);
        var newRoot = new byte[88]; root[..48].CopyTo(newRoot); root[48..].CopyTo(newRoot.AsSpan(56));
        newRoot[32] = 0; newRoot[33] = descriptor.Kind == 1 ? (byte)1 : (byte)0; newRoot[34] = 0; newRoot.AsSpan(84, 4).Clear();
        W(newRoot, 24, (uint)newTailLength);
        var sparse = new byte[n * 12];
        for (var i = 0; i < n; i++) { resource.Block(1).Slice(i * 8, 4).CopyTo(sparse.AsSpan(i * 12)); W(sparse, i * 12 + 8, checked((uint)((newChunks[i].LongLength + 65535) / 65536))); }
        var header = resource.Bytes[..resource.HeaderSize];
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), 0x98820e556647dcae); W(header, 64, (uint)(88 + sparse.Length)); W(header, 68, (uint)newTailLength); header[74] = 2;
        void Block(int index, int size, short section, ulong offset)
        {
            var pos = resource.Blocks[index].TableOffset; W(header, pos, (uint)size);
            BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(pos + 6), section); BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(pos + 8), offset);
        }
        Block(0, 88, 1, 0); if (n > 0) Block(1, n * 12, 1, 88); Block(n > 0 ? 2 : 1, newTailLength, 2, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(resource.Structures[1].TableOffset + 28), 56);
        var output = new byte[checked(header.Length + newRoot.Length + sparse.Length + newTailLength)];
        header.CopyTo(output, 0); newRoot.CopyTo(output, header.Length); sparse.CopyTo(output, header.Length + newRoot.Length); at = header.Length + newRoot.Length + sparse.Length;
        foreach (var mip in mips.Skip(n)) { mip.CopyTo(output, at); at += mip.Length; }
        _ = new TagDocument(output, resource: true);
        return new(output, newChunks, descriptor);
    }
    private static uint U(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
    private static void W(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
    private static CacheException Unsupported(string message) => new("BITMAP_CONVERSION_UNSUPPORTED", message);
}
