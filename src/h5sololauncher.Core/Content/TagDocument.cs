using System.Buffers.Binary;
using H5SoloLauncher.Core.Preparation;

namespace H5SoloLauncher.Core.Content;

public sealed record TagBlock(int Size, short Section, ulong Offset, int TableOffset);
public sealed record TagStructure(string Guid, int Kind, int Target, int FieldBlock, int FieldOffset, int TableOffset);
public sealed record TagReference(int FieldBlock, int FieldOffset, int NameOffset, int Dependency);
public sealed record TagDataReference(int Parent, int Unknown, int Target, int FieldBlock, int FieldOffset);

/// <summary>Bounded ucsh document shared by structural, texture and shader converters.</summary>
public sealed class TagDocument
{
    public byte[] Bytes { get; }
    public TagMetadata Metadata { get; }
    public int HeaderSize { get; }
    public int DataSize { get; }
    public int ResourceSize { get; }
    public TagBlock[] Blocks { get; }
    public TagStructure[] Structures { get; }
    public TagReference[] References { get; }
    public TagDataReference[] DataReferences { get; }
    private readonly int[] offsets;
    public TagDocument(byte[] bytes, bool resource = false)
    {
        if (bytes.Length > (resource ? ModulePayloadReader.MaximumResourceBytes : ModulePayloadReader.MaximumTagBytes)) throw Invalid("Payload exceeds the supported size.");
        Metadata = TagMetadataReader.Read(bytes); Bytes = bytes;
        HeaderSize = checked((int)U(bytes, 60)); DataSize = checked((int)U(bytes, 64)); ResourceSize = checked((int)U(bytes, 68));
        var at = checked(80 + (int)U(bytes, 28) * 24);
        Blocks = new TagBlock[U(bytes, 32)];
        for (var i = 0; i < Blocks.Length; i++, at += 16)
            Blocks[i] = new(checked((int)U(bytes, at)), BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(at + 6)),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at + 8)), at);
        offsets = new int[Blocks.Length];
        for (var i = 0; i < Blocks.Length; i++)
        {
            var block = Blocks[i];
            if (block.Section is not (1 or 2) || block.Offset > int.MaxValue) throw Invalid("A block has an unsupported section or offset.");
            // DataSize includes alignment gaps between section-one blocks. Summing their sizes loses that padding.
            var start = HeaderSize + (block.Section == 2 ? (long)DataSize : 0) + (long)block.Offset;
            if (start > bytes.Length || block.Size > bytes.Length - start) throw Invalid("A block lies outside the payload.");
            offsets[i] = checked((int)start);
        }
        // Section offsets can contain alignment gaps; enforce declared section boundaries, not just the whole payload.
        foreach (var section in Blocks.Select((b, i) => (Block: b, Index: i)).GroupBy(x => x.Block.Section))
        {
            var boundary = section.Key == 1 ? HeaderSize + DataSize : bytes.Length;
            var start = section.Key == 1 ? HeaderSize : HeaderSize + DataSize;
            long end = start;
            foreach (var item in section.OrderBy(x => offsets[x.Index]))
            {
                if (offsets[item.Index] < end || offsets[item.Index] < start || offsets[item.Index] + (long)item.Block.Size > boundary)
                    throw Invalid("Tag blocks overlap or cross a section boundary.");
                end = offsets[item.Index] + (long)item.Block.Size;
            }
        }
        Structures = new TagStructure[U(bytes, 36)];
        for (var i = 0; i < Structures.Length; i++, at += 32)
        {
            var structure = new TagStructure(Convert.ToHexString(bytes.AsSpan(at, 16)).ToLowerInvariant(), I(bytes, at + 16), I(bytes, at + 20), I(bytes, at + 24), I(bytes, at + 28), at);
            if (structure.Target < -1 || structure.Target >= Blocks.Length || structure.FieldBlock < -1 || structure.FieldBlock >= Blocks.Length)
                throw Invalid("A structure has an invalid block reference.");
            Structures[i] = structure;
        }
        DataReferences = new TagDataReference[U(bytes, 40)];
        for (var i = 0; i < DataReferences.Length; i++, at += 20)
        {
            var reference = new TagDataReference(I(bytes, at), I(bytes, at + 4), I(bytes, at + 8), I(bytes, at + 12), I(bytes, at + 16));
            if (reference.Target < -1 || reference.Target >= Blocks.Length) throw Invalid("A data reference has an invalid target.");
            Field(reference.FieldBlock, reference.FieldOffset, 20); DataReferences[i] = reference;
        }
        References = new TagReference[U(bytes, 44)];
        for (var i = 0; i < References.Length; i++, at += 16)
        {
            var reference = new TagReference(I(bytes, at), I(bytes, at + 4), I(bytes, at + 8), I(bytes, at + 12));
            if (reference.Dependency < -1 || reference.Dependency >= U(bytes, 28)) throw Invalid("A tag reference has an invalid dependency.");
            Field(reference.FieldBlock, reference.FieldOffset, 32); References[i] = reference;
        }
    }
    public int BlockOffset(int index)
    {
        if ((uint)index >= Blocks.Length) throw Invalid("Block index is outside the tag.");
        return offsets[index];
    }
    public ReadOnlySpan<byte> Block(int index) => Bytes.AsSpan(BlockOffset(index), Blocks[index].Size);
    public void Field(int index, int offset, int width)
    {
        if ((uint)index >= Blocks.Length || offset < 0 || width < 0 || width > Blocks[index].Size - offset)
            throw Invalid("A field lies outside its containing block.");
    }
    public byte[] WithSchema(string schema)
    {
        var value = ulong.Parse(schema, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
        var output = (byte[])Bytes.Clone(); BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(8), value); return output;
    }
    private static uint U(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static int I(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
    private static CacheException Invalid(string reason) => new("TAG_LAYOUT_INVALID", reason);
}
