using System.Buffers.Binary;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Conversion;
using Xunit;

namespace H5SoloLauncher.Tests;

public sealed class ShaderProgramTests
{
    [Fact]
    public void ScalarMaxUsesFreshTemporaryAndNativeOperandOrder()
    {
        uint[] instruction = [0x0900011a, 0x100042, 1, 0x10000a, 0, 0x10001a, 0, 0x10002a, 0];
        var source = Program([0x02000068, 2, ..instruction, 0x0100003e]);
        var result = ShaderProgram.Translate(source); var change = Assert.Single(result.Changes);
        Assert.Equal(new uint[] { 0x07000034, 0x100042, 2, 0x10001a, 0, 0x10000a, 0, 0x07000034, 0x100042, 1, 0x10002a, 0, 0x10002a, 2 }, change.After);
        var chunks = ShaderProgram.Chunks(result.Unsigned); Assert.Equal("RDEF", chunks[0].Kind);
        var code = chunks.Single(x => x.Kind == "SHDR").Bytes;
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(code.AsSpan(12)));
        Assert.Equal(new byte[] { 11, 22, 33, 44 }, chunks.Single(x => x.Kind == "PRIV").Bytes);
    }

    [Fact]
    public void RejectsUnprovenOperandAliasingAndUnknownOpcodes()
    {
        // Distinct components of a single source temporary are required by the verified lowering.
        var source = Program([0x02000068, 2, 0x0900011a, 0x100012, 1, 0x10000a, 0, 0x10001a, 1, 0x10002a, 0]);
        Assert.Throws<CacheException>(() => ShaderProgram.Translate(source));
        Assert.Throws<CacheException>(() => ShaderProgram.Translate(Program([0x01000120])));
    }

    [Fact]
    public void PcIdentityDistinguishesConstantsAndInstructionsButIgnoresOpaqueAttachments()
    {
        var a = Program([0x1035, 3, 0xabcdef, 0x1835, 3, 23, 0x0100003e]);
        var b = Program([0x1035, 4, 0x123456, 99, 0x1835, 3, 23, 0x0100003e]);
        var changedConstant = Program([0x1035, 3, 0xabcdef, 0x1835, 3, 24, 0x0100003e]);
        Assert.Equal(ShaderProgram.PcInstructionIdentity(a), ShaderProgram.PcInstructionIdentity(b));
        Assert.NotEqual(ShaderProgram.PcInstructionIdentity(a), ShaderProgram.PcInstructionIdentity(changedConstant));
        var converted = ShaderProgram.Translate(a);
        Assert.Equal(ShaderProgram.Chunks(a).Single(x => x.Kind == "SHDR").Bytes, ShaderProgram.Chunks(converted.Unsigned).Single(x => x.Kind == "SHDR").Bytes);
    }

    [Fact]
    public void RejectsOverlappingChunksAndTruncatedInstructions()
    {
        var source = Program([0x0100003e]); BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(36), BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(32)));
        Assert.Throws<CacheException>(() => ShaderProgram.Chunks(source));
        Assert.Throws<CacheException>(() => ShaderProgram.Translate(Program([0x05000000, 1])));
    }
    private static byte[] Program(uint[] body)
    {
        var code = new byte[(body.Length + 2) * 4]; BinaryPrimitives.WriteUInt32LittleEndian(code, 0x10050); BinaryPrimitives.WriteUInt32LittleEndian(code.AsSpan(4), (uint)(body.Length + 2));
        for (var i = 0; i < body.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(code.AsSpan((i + 2) * 4), body[i]);
        return ShaderProgram.Container([new("SHDR", code), new("PRIV", [11, 22, 33, 44])]);
    }
}
