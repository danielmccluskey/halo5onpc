using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace H5SoloLauncher.Core.Conversion;

public sealed record ShaderChunk(string Kind, byte[] Bytes);
public sealed record ShaderBinding(uint Type, uint Register, uint Count, uint Flags, uint Return, uint Dimension, uint Samples, uint ConstantBytes, string Name);
public sealed record ShaderChange(int Word, uint Opcode, uint[] Before, uint[] After);
public sealed record TranslatedShader(byte[] Unsigned, uint Version, ShaderBinding[] Bindings, ShaderChange[] Changes);
public interface IShaderValidator : IDisposable
{
    void VerifyHash(byte[] program);
    byte[] FinalizeAndValidate(TranslatedShader program);
}

/// <summary>Bounded SM4/5 metadata repair and narrowly proven Xbox instruction translations.</summary>
public static class ShaderProgram
{
    public static string PcInstructionIdentity(byte[] source)
    {
        // CUSTOMDATA_OPAQUE (class 2) carries platform compiler attachments, not executable
        // SM instructions or immediate constants. Keep its position/class, but compare the
        // surrounding instructions and every other chunk exactly. Never ignore class 3 data.
        // The optional eight-byte XHSH is an Xbox compiler hash. It is retained in the
        // chosen program, but doesn't change its SM instructions or Windows reflection.
        var parts = Chunks(source).Where(x => x.Kind != "XHSH" || x.Bytes.Length != 8).Select(chunk =>
        {
            if (chunk.Kind is not ("SHDR" or "SHEX")) return chunk;
            var parsed = Instructions(chunk.Bytes); List<uint> words = [parsed.Version, 0];
            foreach (var instruction in parsed.Instructions)
                words.AddRange(instruction.Opcode == 53 && instruction.Words[0] >> 11 == 2 ? new uint[] { 0x1035, 2 } : instruction.Words);
            words[1] = (uint)words.Count; return chunk with { Bytes = Words(words) };
        });
        return Convert.ToHexString(SHA256.HashData(Container(parts)));
    }
    public static ShaderChunk[] Chunks(byte[] bytes)
    {
        if (bytes.Length is < 32 or > 16 * 1024 * 1024 || !bytes.AsSpan(0, 4).SequenceEqual("DXBC"u8) || U(bytes, 24) != bytes.Length || U(bytes, 20) != 1) throw Invalid("Invalid shader container.");
        var count = U(bytes, 28); if (count is 0 or > 64 || 32 + count * 4 > bytes.Length) throw Invalid("Invalid shader chunk table.");
        List<ShaderChunk> chunks = []; List<(int Start, int End)> ranges = [];
        for (var i = 0; i < count; i++)
        {
            var offset = checked((int)U(bytes, 32 + i * 4));
            if (offset < 32 + count * 4 || offset > bytes.Length - 8) throw Invalid("Shader chunk offset is outside the container.");
            var size = checked((int)U(bytes, offset + 4)); if (size > bytes.Length - offset - 8) throw Invalid("Shader chunk is truncated.");
            ranges.Add((offset, offset + 8 + size)); chunks.Add(new(Encoding.ASCII.GetString(bytes, offset, 4), bytes.AsSpan(offset + 8, size).ToArray()));
        }
        var end = checked((int)(32 + count * 4)); foreach (var range in ranges.OrderBy(x => x.Start)) { if (range.Start < end) throw Invalid("Shader chunks overlap."); end = range.End; }
        if (chunks.Count(x => x.Kind is "SHDR" or "SHEX") != 1) throw Invalid("Shader must contain exactly one instruction stream.");
        return chunks.ToArray();
    }
    public static byte[] Container(IEnumerable<ShaderChunk> chunks)
    {
        var parts = chunks.ToArray(); var size = checked(32 + parts.Length * 4 + parts.Sum(x => x.Bytes.Length + 8));
        if (size > 16 * 1024 * 1024) throw Invalid("Converted shader is too large.");
        var bytes = new byte[size]; "DXBC"u8.CopyTo(bytes); W(bytes, 20, 1); W(bytes, 24, (uint)size); W(bytes, 28, (uint)parts.Length);
        var at = 32 + parts.Length * 4;
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Kind.Length != 4) throw Invalid("Invalid shader chunk kind.");
            W(bytes, 32 + i * 4, (uint)at); Encoding.ASCII.GetBytes(parts[i].Kind).CopyTo(bytes, at); W(bytes, at + 4, (uint)parts[i].Bytes.Length);
            parts[i].Bytes.CopyTo(bytes, at + 8); at += 8 + parts[i].Bytes.Length;
        }
        return bytes;
    }
    private sealed record Instruction(int Offset, uint[] Words) { public uint Opcode => Words[0] & 2047; }
    private static (uint Version, Instruction[] Instructions) Instructions(byte[] code)
    {
        if (code.Length < 8 || code.Length % 4 != 0 || U(code, 4) != code.Length / 4) throw Invalid("Invalid shader word count.");
        var version = U(code, 0); var major = (version >> 4) & 15; var minor = version & 15;
        if (!(major == 4 && minor <= 1 || major == 5 && minor == 0) || version >> 16 > 5) throw Invalid("Unsupported shader version.");
        List<Instruction> output = [];
        for (var at = 2; at < code.Length / 4;)
        {
            var token = U(code, at * 4); var opcode = token & 2047;
            if (opcode == 53 && at + 1 >= code.Length / 4) throw Invalid("Truncated custom-data instruction.");
            var size = opcode == 53 ? U(code, (at + 1) * 4) : (token >> 24) & 127;
            if (size == 0 || size > code.Length / 4 - at) throw Invalid("Shader instruction is truncated.");
            var words = new uint[size]; for (var i = 0; i < words.Length; i++) words[i] = U(code, (at + i) * 4);
            output.Add(new(at, words)); at += (int)size;
        }
        return (version, output.ToArray());
    }
    private static bool Standard(uint op) => op < 235 && op is not (107 or 112 or 209 or 218);

    public static TranslatedShader Translate(byte[] source)
    {
        var chunks = Chunks(source);
        if (chunks.Any(x => x.Kind == "RDEF")) throw Invalid("Source reflection already exists; this repair applies only to stripped source programs.");
        var code = chunks.Single(x => x.Kind is "SHDR" or "SHEX"); var parsed = Instructions(code.Bytes);
        var bindings = Declarations(parsed.Instructions); var unknown = parsed.Instructions.Where(x => !Standard(x.Opcode)).Select(x => x.Opcode).ToHashSet();
        var max3 = unknown.SetEquals([282u]); var fetch = unknown.Count > 0 && unknown.All(x => x is 247 or 250 or 355);
        if (unknown.Count > 0 && !max3 && !fetch) throw Invalid("No verified translation exists for shader opcode(s) " + string.Join(", ", unknown.Order()));
        List<ShaderChange> changes = []; var result = code.Bytes;
        if (max3 || fetch)
        {
            var declarations = parsed.Instructions.Where(x => x.Opcode == 104).ToArray();
            if (declarations.Length != 1 || declarations[0].Words.Length != 2 || declarations[0].Words[1] >= (fetch ? 4095 : 4096) || fetch && parsed.Version != 0x10050)
                throw Invalid("Shader temporary registers or phase layout are unsupported.");
            var scratch = declarations[0].Words[1]; List<uint> output = [parsed.Version, 0];
            foreach (var instruction in parsed.Instructions)
            {
                var v = instruction.Words;
                if (instruction.Opcode == 104) { output.Add(v[0]); output.Add(scratch + 1); continue; }
                if (Standard(instruction.Opcode)) { output.AddRange(v); continue; }
                var replacement = max3 ? Max3(v, scratch) : Fetch(v, scratch);
                changes.Add(new(instruction.Offset, instruction.Opcode, v, replacement)); output.AddRange(replacement);
            }
            if (fetch && (changes.Count is not (4 or 5 or 6) || changes[0].Opcode != 250 || changes[1].Opcode != 247 || changes.Skip(2).Any(x=>x.Opcode!=355))) throw Invalid("Explicit-fetch shader doesn't match a verified instruction sequence: " + string.Join(", ",changes.Select(x=>x.Opcode)));
            output[1] = (uint)output.Count; result = Words(output);
            if (Instructions(result).Instructions.Any(x => !Standard(x.Opcode))) throw Invalid("Translated shader still contains unsupported instructions.");
        }
        var rdef = Reflection(parsed.Version, bindings);
        var parts = new[] { new ShaderChunk("RDEF", rdef) }.Concat(chunks.Select(x => ReferenceEquals(x, code) ? x with { Bytes = result } : x));
        return new(Container(parts), parsed.Version, bindings, changes.ToArray());
    }
    private static uint[] Max3(uint[] v, uint scratch)
    {
        if (v.Length != 9 || v[0] != 0x0900011a || v[1] is not (0x100012 or 0x100022 or 0x100042 or 0x100082) || v[2] >= scratch ||
            new[] { v[3], v[5], v[7] }.Any(x => x is not (0x10000a or 0x10001a or 0x10002a or 0x10003a)) ||
            new[] { v[3], v[5], v[7] }.Distinct().Count() != 3 || v[4] != v[6] || v[4] != v[8] || v[4] >= scratch) throw Invalid("Unverified scalar maximum operand shape.");
        var component = (uint)BitOperations.Log2((v[1] >> 4) & 15);
        return [0x07000034, v[1], scratch, v[5], v[6], v[3], v[4], 0x07000034, v[1], v[2], v[7], v[8], 0x10000a | (component << 4), scratch];
    }
    private static uint[] Fetch(uint[] v, uint scratch)
    {
        var op = v[0] & 2047;
        if (op == 250)
        {
            if (!v.AsSpan().SequenceEqual(new uint[] { 0x0a0000fa, 0x100082, 0, 0x10003a, 0, 0x20800a, 3, 0, 0x10100a, 0 })) throw Invalid("Unverified integer address calculation.");
            var changed = (uint[])v.Clone(); changed[0] = (changed[0] & ~2047u) | 35; return changed;
        }
        if (op == 247)
        {
            if (!v.AsSpan().SequenceEqual(new uint[] { 0x070000f7, 0x100082, 0, 0x10003a, 0, 0x4001, 20 })) throw Invalid("Unverified vertex stride calculation.");
            return [0x08000026, 0xd000, ..v[1..]];
        }
        if (op != 355 || v.Length != 14 || !v.AsSpan(0, 3).SequenceEqual(new uint[] { 0x8e000163, 0x800002c2, 0x199983 })) throw Invalid("Unverified formatted buffer fetch.");
        var d = v[3]; var di = v[4]; var address = v[5]; var ai = v[6]; var resource = v[7]; var format = v[10]; var mask = (d >> 4) & 15;
        if (d is not (0x100032 or 0x100052 or 0x100072 or 0x1000f2) || di >= scratch || ai >= scratch || address is not (0x10000a or 0x10001a or 0x10002a or 0x10003a) || v[8] != 4 || v[9] != 0x4002 ||
            v[11] != format || v[12] != format || v[13] != format || (format, mask, resource) is not ((12, 7, 0x107246) or (5, 3, 0x107046) or (5, 5, 0x107106) or (9, 7, 0x107246) or (9, 15, 0x107e46))) throw Invalid("Unverified buffer format, register or mask: " + string.Join(" ",v.Select(x=>x.ToString("x8"))) + $"; temporaries={scratch}.");
        static uint[] Dest(uint reg, uint mask) => [0x100002 | (mask << 4), reg];
        static uint[] Src(uint reg, uint swizzle) => [0x100006 | (swizzle << 4), reg];
        static uint[] Imm(params uint[] values) => [0x4002, ..values];
        static uint[] Inst(uint opcode, params uint[][] args) { var words = args.SelectMany(x => x).ToArray(); return [((uint)(words.Length + 1) << 24) | opcode, ..words]; }
        var width = format == 9 ? 10u : 16u; var scale = BitConverter.SingleToUInt32Bits(1f / ((1u << (int)width) - 1));
        // Format 9 packs UNORM RGB into ten bits each and alpha into the top two.
        // Three-component callers retain their original lowering byte for byte.
        var alpha = format == 9 && mask == 15;
        return [0x890000a5, 0x800002c2, 0x199983, ..Dest(scratch, format == 12 ? 3u : 1u), address, ai, 0x107046, 4,
            // The xz destination uses the resource's xxyx swizzle: low 16 bits
            // go to x and high 16 bits to z; untouched destination lanes survive.
            ..Inst(138, Dest(scratch, mask), Imm(width, width, width, alpha ? 2u : width), format == 9 ? Imm(0, 10, 20, alpha ? 30u : 0u) : mask == 5 ? Imm(0, 0, 16, 0) : Imm(0, 16, 0, 0), Src(scratch, format == 12 ? 0x50u : 0u)),
            ..Inst(86, Dest(scratch, mask), Src(scratch, 0xe4)), ..Inst(56, [d, di], Src(scratch, 0xe4), Imm(scale, scale, scale, alpha ? BitConverter.SingleToUInt32Bits(1f / 3f) : scale))];
    }
    private static ShaderBinding[] Declarations(Instruction[] instructions)
    {
        List<ShaderBinding> bindings = [];
        foreach (var instruction in instructions)
        {
            var op = instruction.Opcode;
            if (op is not (88 or 89 or 90 or 156 or 157 or 158 or 161 or 162)) continue;
            var v = instruction.Words; if (v.Length < 3 || v[0] >> 31 != 0 || v[1] >> 31 != 0) throw Invalid("Extended shader resource declarations are unsupported.");
            var dims = (v[1] >> 20) & 3;
            if (dims is not (1 or 2) || dims + 2 > v.Length || Enumerable.Range(0, (int)dims).Any(i => ((v[1] >> (22 + 3 * i)) & 7) != 0)) throw Invalid("Resource declaration uses unsupported register indexing.");
            var reg = v[2]; var rest = v[(2 + (int)dims)..]; ShaderBinding binding;
            if (op == 89)
            {
                if (dims != 2 || rest.Length != 0) throw Invalid("Invalid constant-buffer declaration.");
                binding = new(0, reg, 1, reg == 0 ? 0u : 1u, 0, 0, 0, checked(v[3] * 16), "cb" + reg);
            }
            else if (op == 90)
            {
                if (dims != 1 || rest.Length != 0) throw Invalid("Invalid sampler declaration.");
                binding = new(3, reg, 1, ((v[0] >> 11) & 15) == 1 ? 3u : 1u, 0, 0, 0, 0, "s" + reg);
            }
            else if (op is 157 or 161)
            {
                if (dims != 1 || rest.Length != 0) throw Invalid("Invalid raw buffer declaration.");
                binding = new(op == 157 ? 8u : 7u, reg, 1, 1, 6, 1, 0, 0, (op == 157 ? "u" : "t") + reg);
            }
            else if (op is 158 or 162)
            {
                if (dims != 1 || rest.Length != 1) throw Invalid("Invalid structured buffer declaration.");
                binding = new(op == 158 ? ((v[0] & 0x800000) != 0 ? 11u : 6u) : 5u, reg, 1, 1, 6, 1, rest[0], 0, (op == 158 ? "u" : "t") + reg);
            }
            else
            {
                if (dims != 1 || rest.Length != 1) throw Invalid("Invalid typed resource declaration.");
                uint[] dimensions = [0, 1, 2, 4, 6, 8, 9, 3, 5, 7, 10]; var dimension = (v[0] >> 11) & 31;
                var components = Enumerable.Range(0, 4).Count(i => ((rest[0] >> (i * 4)) & 15) != 0);
                if (dimension >= dimensions.Length || components == 0) throw Invalid("Unsupported resource dimension or return type.");
                binding = new(op == 156 ? 4u : 2u, reg, 1, (uint)(1 + 4 * (components - 1)), rest[0] & 15, dimensions[dimension],
                    dimension is 4 or 9 ? (v[0] >> 16) & 127 : uint.MaxValue, 0, (op == 156 ? "u" : "t") + reg);
            }
            bindings.Add(binding);
        }
        if (bindings.Count > 4096) throw Invalid("Shader binding table is too large."); return bindings.ToArray();
    }
    private static byte[] Reflection(uint version, ShaderBinding[] bindings)
    {
        var major = (version >> 4) & 15; var minor = version & 15; var stage = version >> 16;
        uint[] targets = [0xffff, 0xfffe, 0x4753, 0x4853, 0x4453, 0x4353]; var target = (targets[stage] << 16) | (major << 8) | minor;
        var head = major >= 5 ? 60 : 28; var cbs = bindings.Where(x => x.Type == 0).ToArray(); var cbOffset = head + bindings.Length * 32;
        using var stream = new MemoryStream(); stream.SetLength(cbOffset + cbs.Length * 24); stream.Position = stream.Length;
        Dictionary<string, uint> strings = [];
        uint String(string value)
        { if (strings.TryGetValue(value, out var at)) return at; at = (uint)stream.Position; stream.Write(Encoding.UTF8.GetBytes(value)); stream.WriteByte(0); strings.Add(value, at); return at; }
        var names = bindings.Select(x => String(x.Name)).ToArray(); var creator = String("Source-declaration reflection repair for H5 Forge");
        while (stream.Length % 4 != 0) stream.WriteByte(0); var bytes = stream.ToArray();
        void Row(int offset, params uint[] values) { for (var i = 0; i < values.Length; i++) W(bytes, offset + i * 4, values[i]); }
        for (var i = 0; i < bindings.Length; i++) { var x = bindings[i]; Row(head + i * 32, names[i], x.Type, x.Return, x.Dimension, x.Samples, x.Register, x.Count, x.Flags); }
        for (var i = 0; i < cbs.Length; i++) Row(cbOffset + i * 24, strings[cbs[i].Name], 0, 0, cbs[i].ConstantBytes, 0, 0);
        Row(0, (uint)cbs.Length, (uint)cbOffset, (uint)bindings.Length, (uint)head, target, 0x100, creator);
        if (major >= 5) Row(28, 0x31314452, 60, 24, 32, 40, 36, 12, 0); return bytes;
    }
    private static byte[] Words(IEnumerable<uint> words) { var array = words.ToArray(); var bytes = new byte[array.Length * 4]; for (var i = 0; i < array.Length; i++) W(bytes, i * 4, array[i]); return bytes; }
    private static uint U(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
    private static void W(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
    private static CacheException Invalid(string message) => new("SHADER_PROGRAM_UNSUPPORTED", message);
}
