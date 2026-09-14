using System.Buffers.Binary;
using H5SoloLauncher.Core.Content;

namespace H5SoloLauncher.Core.Conversion;

public sealed record ShaderField(int Stage, int Offset, ulong Key);
public sealed record ShaderBankStage(int ArrayStructure, int KeyStructure, int Array, int KeyArray, int Stride, uint[] Keys, byte[] Rows);
public sealed record ShaderBankProgram(int Stage, uint Key, byte[] Row, byte[] Code, byte[]? Auxiliary, TagStructure AuxiliaryStructure, TagDataReference DataReference);
public sealed record MergedShaderBank(byte[] Resource, int[] Counts);

public static class ShaderBank
{
    public static ShaderField[] Fields(TagDocument tag)
    {
        List<ShaderField> fields = [];
        foreach (var structure in tag.Structures.Where(x => x.Guid == "84a4ddb66c45fe3b03f0bba4de6498b0" && x.Target >= 0))
        {
            var bytes = tag.Block(structure.Target); if (bytes.Length % 8 != 0 || bytes.Length > 6 * 8) throw Invalid("Unexpected shader-definition stage array.");
            for (var i = 0; i < bytes.Length / 8; i++) fields.Add(new(i, tag.BlockOffset(structure.Target) + i * 8, BinaryPrimitives.ReadUInt64LittleEndian(bytes[(i * 8)..])));
        }
        return fields.ToArray();
    }
    public static string BankId(TagDocument tag)
    {
        if (U(tag.Bytes, 28) < 1 || !tag.Bytes.AsSpan(80, 4).SequenceEqual("bstm"u8)) throw Invalid("Shader definition has no first bank dependency.");
        return U(tag.Bytes, 96).ToString("x8");
    }
    public static ShaderBankStage[] Stages(TagDocument bank)
    {
        var arrays = bank.Structures.Select((x, i) => (Structure: x, Index: i)).Where(x => x.Structure.FieldBlock == 0).ToDictionary(x => x.Structure.FieldOffset);
        List<ShaderBankStage> stages = [];
        for (var stage = 0; stage < 6; stage++)
        {
            if (!arrays.TryGetValue(stage * 28, out var a) || !arrays.TryGetValue(168 + stage * 28, out var k)) throw Invalid("Shader bank stage tables are incomplete.");
            var keyBytes = k.Structure.Target < 0 ? [] : bank.Block(k.Structure.Target).ToArray();
            if (keyBytes.Length % 4 != 0) throw Invalid("Shader key array has an invalid stride.");
            var keys = Enumerable.Range(0, keyBytes.Length / 4).Select(i => U(keyBytes, i * 4)).ToArray();
            if (!keys.SequenceEqual(keys.Distinct().OrderBy(x => unchecked((int)x)))) throw Invalid("Shader keys are duplicated or not in native signed order.");
            var rows = a.Structure.Target < 0 ? [] : bank.Block(a.Structure.Target).ToArray();
            var stride = keys.Length == 0 ? 0 : rows.Length / keys.Length;
            if (keys.Length == 0 ? rows.Length != 0 : stride is not (56 or 72 or 104) || rows.Length != keys.Length * stride) throw Invalid("Shader bank row stride is unsupported.");
            bank.Field(0, stage * 28, 28); bank.Field(0, 168 + stage * 28, 28);
            if (BinaryPrimitives.ReadUInt32LittleEndian(bank.Block(0)[(stage * 28 + 16)..]) != keys.Length || BinaryPrimitives.ReadUInt32LittleEndian(bank.Block(0)[(168 + stage * 28 + 16)..]) != keys.Length) throw Invalid("Shader bank root counts differ from its arrays.");
            stages.Add(new(a.Index, k.Index, a.Structure.Target, k.Structure.Target, stride, keys, rows));
        }
        return stages.ToArray();
    }
    public static ShaderBankProgram[] Select(TagDocument bank, HashSet<uint> wanted)
    {
        var stages = Stages(bank); var dr = bank.DataReferences.ToDictionary(x => (x.FieldBlock, x.FieldOffset));
        var aux = bank.Structures.Where(x => x.FieldBlock > 0).ToDictionary(x => (x.FieldBlock, x.FieldOffset));
        List<ShaderBankProgram> selected = []; HashSet<uint> seen = [];
        for (var stage = 0; stage < stages.Length; stage++)
        {
            var array = stages[stage];
            for (var i = 0; i < array.Keys.Length; i++)
            {
                var key = array.Keys[i]; if (!wanted.Contains(key)) continue;
                if (array.Stride != 56 || !seen.Add(key)) throw Invalid("A selected program uses a specialized stage or an ambiguous key.");
                if (!dr.TryGetValue((array.Array, i * 56), out var data) || data.Target < 0 || !aux.TryGetValue((array.Array, i * 56 + 28), out var fix)) throw Invalid("A selected shader has incomplete bytecode fixups.");
                var code = bank.Block(data.Target).ToArray(); var row = array.Rows.AsSpan(i * 56, 56).ToArray();
                if (U(row, 24) != code.Length) throw Invalid("A shader row's declared bytecode length differs from its block.");
                selected.Add(new(stage, key, row, code, fix.Target < 0 ? null : bank.Block(fix.Target).ToArray(), fix, data));
            }
        }
        if (!seen.SetEquals(wanted)) throw Invalid("A required program wasn't found in the source bank."); return selected.ToArray();
    }

    public static MergedShaderBank Merge(TagDocument native, ShaderBankProgram[] additions)
    {
        var info = Stages(native); var blocks = native.Blocks.Select((_, i) => native.Block(i).ToArray()).ToList();
        var structures = native.Structures.ToList(); var datarefs = native.DataReferences.ToList(); int[] counts = new int[6];
        int Append(byte[] bytes) { blocks.Add(bytes); return blocks.Count - 1; }
        for (var stage = 0; stage < 6; stage++)
        {
            var added = additions.Where(x => x.Stage == stage).ToArray(); var array = info[stage]; counts[stage] = array.Keys.Length + added.Length;
            if (counts[stage] > ushort.MaxValue) throw Invalid("The native bank stage count exceeds its tag field.");
            if (added.Length == 0) continue;
            if (array.Stride is not (0 or 56) || added.Any(x => x.Row.Length != 56)) throw Invalid("Specialized shader stages cannot be extended by this converter.");
            var rows = array.Keys.Select((key, index) => (Key: key, Row: array.Rows.AsSpan(index * 56, 56).ToArray(), Old: (int?)index, Added: (ShaderBankProgram?)null))
                .Concat(added.Select(x => (x.Key, x.Row, Old: (int?)null, Added: (ShaderBankProgram?)x))).OrderBy(x => unchecked((int)x.Key)).ToArray();
            if (rows.Select(x => x.Key).Distinct().Count() != rows.Length) throw Invalid("A converted program collides with a native bank key.");
            var rowBlock = array.Array; var keyBlock = array.KeyArray;
            if (rowBlock < 0) { rowBlock = Append([]); structures[array.ArrayStructure] = structures[array.ArrayStructure] with { Target = rowBlock }; }
            if (keyBlock < 0) { keyBlock = Append([]); structures[array.KeyStructure] = structures[array.KeyStructure] with { Target = keyBlock }; }
            var remap = rows.Select((row, index) => (row.Old, Index: index)).Where(x => x.Old is not null).ToDictionary(x => x.Old!.Value, x => x.Index);
            int Offset(int offset)
            { if (offset < 0 || !remap.TryGetValue(offset / 56, out var index)) throw Invalid("A native shader relocation points outside its row array."); return index * 56 + offset % 56; }
            for (var i = 0; i < structures.Count; i++) if (structures[i].FieldBlock == rowBlock) structures[i] = structures[i] with { FieldOffset = Offset(structures[i].FieldOffset) };
            for (var i = 0; i < datarefs.Count; i++) if (datarefs[i].FieldBlock == rowBlock) datarefs[i] = datarefs[i] with { FieldOffset = Offset(datarefs[i].FieldOffset) };
            blocks[rowBlock] = rows.SelectMany(x => x.Row).ToArray(); blocks[keyBlock] = new byte[rows.Length * 4];
            for (var i = 0; i < rows.Length; i++) W(blocks[keyBlock], i * 4, rows[i].Key);
            W(blocks[0], stage * 28 + 16, (uint)rows.Length); W(blocks[0], 168 + stage * 28 + 16, (uint)rows.Length);
            for (var i = 0; i < rows.Length; i++)
            {
                var add = rows[i].Added; if (add is null) continue;
                datarefs.Add(add.DataReference with { Parent = array.ArrayStructure, FieldBlock = rowBlock, FieldOffset = i * 56, Target = Append(add.Code) });
                structures.Add(add.AuxiliaryStructure with { FieldBlock = rowBlock, FieldOffset = i * 56 + 28, Target = add.Auxiliary is null ? -1 : Append(add.Auxiliary) });
            }
        }
        var output = Serialize(native, blocks, structures, datarefs); var rebuilt = new TagDocument(output, resource: true); var after = Stages(rebuilt);
        for (var stage = 0; stage < 6; stage++)
        {
            var before = info[stage]; var found = after[stage]; var indices = found.Keys.Select((key, index) => (key, index)).ToDictionary(x => x.key, x => x.index);
            for (var i = 0; i < before.Keys.Length; i++)
                if (!indices.TryGetValue(before.Keys[i], out var index) || !before.Rows.AsSpan(i * before.Stride, before.Stride).SequenceEqual(found.Rows.AsSpan(index * found.Stride, found.Stride))) throw Invalid("A native shader row changed during bank extension.");
        }
        var mutable = info.Where((_, i) => additions.Any(x => x.Stage == i)).SelectMany(x => new[] { x.Array, x.KeyArray }).Append(0).ToHashSet();
        for (var i = 0; i < native.Blocks.Length; i++) if (!mutable.Contains(i) && !native.Block(i).SequenceEqual(rebuilt.Block(i))) throw Invalid("An unrelated native shader block changed.");
        if (additions.Length == 0 && !output.AsSpan().SequenceEqual(native.Bytes)) throw Invalid("The native bank didn't round-trip unchanged.");
        foreach (var added in additions)
        {
            var found = Select(rebuilt, [added.Key]).Single();
            if (found.Stage != added.Stage || !found.Code.AsSpan().SequenceEqual(added.Code)) throw Invalid("A newly added shader failed read-back verification.");
        }
        return new(output, counts);
    }
    private static byte[] Serialize(TagDocument source, List<byte[]> blocks, List<TagStructure> structures, List<TagDataReference> datarefs)
    {
        if (source.Blocks.Any(x => x.Section != 1) || U(source.Bytes, 28) != 0 || source.References.Length != 0 || source.ResourceSize != 0) throw Invalid("The bank serialization layout is unsupported.");
        using var payload = new MemoryStream(); var descriptors = new byte[blocks.Count * 16]; long priorEnd = 0;
        for (var i = 0; i < blocks.Count; i++)
        {
            ushort marker = 0;
            if (i < source.Blocks.Length)
            {
                var old = source.Blocks[i]; if ((long)old.Offset < priorEnd) throw Invalid("Shader blocks aren't stored in index order.");
                payload.Write(source.Bytes.AsSpan(source.HeaderSize + (int)priorEnd, checked((int)((long)old.Offset - priorEnd))));
                marker = BinaryPrimitives.ReadUInt16LittleEndian(source.Bytes.AsSpan(old.TableOffset + 4)); priorEnd = checked((long)old.Offset + old.Size);
            }
            else if (i == source.Blocks.Length) payload.Write(source.Bytes.AsSpan(source.HeaderSize + (int)priorEnd, source.DataSize - (int)priorEnd));
            W(descriptors, i * 16, (uint)blocks[i].Length); BinaryPrimitives.WriteUInt16LittleEndian(descriptors.AsSpan(i * 16 + 4), marker);
            BinaryPrimitives.WriteInt16LittleEndian(descriptors.AsSpan(i * 16 + 6), 1); BinaryPrimitives.WriteUInt64LittleEndian(descriptors.AsSpan(i * 16 + 8), checked((ulong)payload.Position)); payload.Write(blocks[i]);
        }
        if (blocks.Count == source.Blocks.Length) payload.Write(source.Bytes.AsSpan(source.HeaderSize + (int)priorEnd, source.DataSize - (int)priorEnd));
        var stringIdsAt = 80 + source.Blocks.Length * 16 + source.Structures.Length * 32 + source.DataReferences.Length * 20;
        var stringIdsBytes = checked((int)U(source.Bytes, 48) * 8); var preserved = source.Bytes.AsSpan(stringIdsAt, source.HeaderSize - stringIdsAt).ToArray();
        if (preserved.Length < stringIdsBytes + U(source.Bytes, 52)) throw Invalid("Shader bank strings are truncated.");
        var headerSize = checked(80 + descriptors.Length + structures.Count * 32 + datarefs.Count * 20 + preserved.Length);
        if (headerSize + payload.Length > ModulePayloadReader.MaximumResourceBytes) throw Invalid("The shader bank exceeds the conversion size limit.");
        var output = new byte[checked(headerSize + (int)payload.Length)]; source.Bytes.AsSpan(0, 80).CopyTo(output);
        W(output, 32, (uint)blocks.Count); W(output, 36, (uint)structures.Count); W(output, 40, (uint)datarefs.Count); W(output, 60, (uint)headerSize); W(output, 64, (uint)payload.Length);
        descriptors.CopyTo(output, 80); var at = 80 + descriptors.Length;
        foreach (var structure in structures)
        {
            Convert.FromHexString(structure.Guid).CopyTo(output, at); I(output, at + 16, structure.Kind); I(output, at + 20, structure.Target); I(output, at + 24, structure.FieldBlock); I(output, at + 28, structure.FieldOffset); at += 32;
        }
        foreach (var data in datarefs) { I(output, at, data.Parent); I(output, at + 4, data.Unknown); I(output, at + 8, data.Target); I(output, at + 12, data.FieldBlock); I(output, at + 16, data.FieldOffset); at += 20; }
        preserved.CopyTo(output, at); payload.GetBuffer().AsSpan(0, (int)payload.Length).CopyTo(output.AsSpan(headerSize)); return output;
    }
    private static uint U(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
    private static void W(byte[] b, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), value);
    private static void I(byte[] b, int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at), value);
    private static CacheException Invalid(string message) => new("SHADER_BANK_UNSUPPORTED", message);
}
