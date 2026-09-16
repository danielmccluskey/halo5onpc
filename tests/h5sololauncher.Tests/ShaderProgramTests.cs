using System.Buffers.Binary;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Conversion;
using Xunit;

namespace H5SoloLauncher.Tests;

public sealed class ShaderProgramTests
{
    [Theory]
    [InlineData(0x0000ffffu, 1f, 0f)]
    [InlineData(0xffff0000u, 0f, 1f)]
    [InlineData(0x80004000u, 16384f / 65535f, 32768f / 65535f)]
    public void TwoComponentFetchRoutesPackedValuesToXAndZ(uint packed, float x, float z)
    {
        uint[] fetch = [0x8e000163, 0x800002c2, 0x199983, 0x100052, 2, 0x10000a, 2, 0x107106, 4, 0x4002, 5, 5, 5, 5];
        var result = ShaderProgram.Translate(Program([0x02000068, 10,
            0x0a0000fa, 0x100082, 0, 0x10003a, 0, 0x20800a, 3, 0, 0x10100a, 0,
            0x070000f7, 0x100082, 0, 0x10003a, 0, 0x4001, 20,
            ..fetch, ..fetch, 0x0100003e]));
        var code = result.Changes[^1].After;
        Assert.Equal(5u, (code[30] >> 4) & 15); // Final write preserves y and w.
        foreach (var lane in new[] { 0, 2 })
        {
            var value = ((packed >> (int)code[18 + lane]) & ((1u << (int)code[13 + lane]) - 1)) * BitConverter.UInt32BitsToSingle(code[35 + lane]);
            Assert.InRange(Math.Abs(value - (lane == 0 ? x : z)), 0, 0.000001f);
        }
    }
    [Theory]
    [InlineData(0u,0f,0f,0f,0f)]
    [InlineData(0xffffffffu,1f,1f,1f,1f)]
    [InlineData(0x400003ffu,1f,0f,0f,1f/3f)]
    [InlineData(0x800ffc00u,0f,1f,0f,2f/3f)]
    [InlineData(0x3ff00000u,0f,0f,1f,0f)]
    public void PackedVertexFetchPreservesAllFourUnormComponents(uint packed,float r,float g,float b,float a)
    {
        uint[] fetch=[0x8e000163,0x800002c2,0x199983,0x1000f2,5,0x10002a,2,0x107e46,4,0x4002,9,9,9,9];
        var result=ShaderProgram.Translate(Program([0x02000068,10,
            0x0a0000fa,0x100082,0,0x10003a,0,0x20800a,3,0,0x10100a,0,
            0x070000f7,0x100082,0,0x10003a,0,0x4001,20,
            ..fetch,..fetch,..fetch,..fetch,0x0100003e]));
        Assert.Equal(6,result.Changes.Length);
        var code=result.Changes[^1].After;
        Assert.Equal(165u,code[0]&2047); // raw load
        Assert.Equal(138u,code[9]&2047); // unsigned bit-field extraction
        Assert.Equal(86u,code[24]&2047); // uint-to-float
        Assert.Equal(56u,code[29]&2047); // normalization
        float[] expected=[r,g,b,a];
        for(var i=0;i<4;i++)
        {
            var width=(int)code[13+i];var offset=(int)code[18+i];
            var value=((packed>>offset)&((1u<<width)-1))*BitConverter.UInt32BitsToSingle(code[35+i]);
            Assert.InRange(Math.Abs(value-expected[i]),0,0.000001f);
        }
    }
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
