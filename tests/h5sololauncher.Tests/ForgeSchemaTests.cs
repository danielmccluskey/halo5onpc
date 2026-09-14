using System.Buffers.Binary;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Forge;
using Xunit;

namespace H5SoloLauncher.Tests;

public class ForgeSchemaTests
{
    [Fact] public void CapturesPortableSchemasTwiceAndChecksIdentity()
    {
        using var memory = new Memory(); var result = ForgeSchemaRegistry.Capture(ForgeSchemaRegistry.Package, memory);
        Assert.Equal(100, result.Structures.Length); Assert.Equal(3, memory.IdentityChecks);
        Assert.All(result.Structures, x => { Assert.Equal(64u, x.Size); Assert.Equal("1122334455667788", x.Schema); });
    }
    [Fact] public void RejectsVersionBeforeMemoryReads()
    {
        using var memory = new Memory();
        Assert.Equal("FORGE_VERSION_UNSUPPORTED", Assert.Throws<CacheException>(() => ForgeSchemaRegistry.Capture("unknown", memory)).Code);
        Assert.Equal(0, memory.Reads);
    }
    [Fact] public void RejectsChangedCodeAndCyclicTree()
    {
        using var memory = new Memory(); memory.Data[(int)ForgeSchemaRegistry.LookupRva] = 0xcc;
        Assert.Equal("FORGE_SCHEMA_GUARD", Assert.Throws<CacheException>(() => ForgeSchemaRegistry.Capture(ForgeSchemaRegistry.Package, memory)).Code);
        ForgeSchemaRegistry.LookupGuard.CopyTo(memory.Data.AsSpan((int)ForgeSchemaRegistry.LookupRva));
        BinaryPrimitives.WriteUInt64LittleEndian(memory.Data.AsSpan(Memory.Node), memory.ImageBase + Memory.Node);
        Assert.Equal("FORGE_SCHEMA_TREE", Assert.Throws<CacheException>(() => ForgeSchemaRegistry.Capture(ForgeSchemaRegistry.Package, memory)).Code);
    }
    [Fact] public void RejectsLayoutChangingBetweenSnapshots()
    {
        using var memory = new Memory(); memory.ChangeAfterFirstSnapshot = true;
        Assert.Equal("FORGE_SCHEMA_CHANGED", Assert.Throws<CacheException>(() => ForgeSchemaRegistry.Capture(ForgeSchemaRegistry.Package, memory)).Code);
    }
    private sealed class Memory : IForgeMemory
    {
        public const int Sentinel = 0x10000, Node = 0x11000, Definition = 0x30000;
        public byte[] Data { get; } = new byte[ForgeSchemaRegistry.RegistryRva + 8];
        public ulong ImageBase => 0x140000000;
        public int IdentityChecks, Reads;
        public bool ChangeAfterFirstSnapshot;
        public Memory()
        {
            void Q(int at, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(Data.AsSpan(at), value);
            ForgeSchemaRegistry.LookupGuard.CopyTo(Data.AsSpan((int)ForgeSchemaRegistry.LookupRva));
            Q((int)ForgeSchemaRegistry.RegistryRva, ImageBase + Sentinel); Q(Sentinel + 8, ImageBase + Node);
            for (var i = 0; i < 100; i++)
            {
                var node = Node + i * 64; var definition = Definition + i * 160;
                Q(node, ImageBase + Sentinel); Q(node + 16, ImageBase + (ulong)(i == 99 ? Sentinel : node + 64));
                Q(node + 32, (ulong)i + 1); Q(node + 48, ImageBase + (ulong)definition);
                Data.AsSpan(node + 32, 16).CopyTo(Data.AsSpan(definition + 16));
                BinaryPrimitives.WriteUInt32LittleEndian(Data.AsSpan(definition + 40), 64); Q(definition + 144, 0x1122334455667788);
            }
        }
        public byte[] Read(ulong address, int count) { Reads++; return Data.AsSpan(checked((int)(address - ImageBase)), count).ToArray(); }
        public void VerifyIdentity()
        {
            if (++IdentityChecks == 2 && ChangeAfterFirstSnapshot) Data[Definition + 144] ^= 1;
        }
        public void Dispose() { }
    }
}
