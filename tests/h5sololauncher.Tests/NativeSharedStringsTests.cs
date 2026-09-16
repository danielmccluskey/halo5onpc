using System.Buffers.Binary;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Conversion;
using Xunit;

namespace H5SoloLauncher.Tests;

public sealed class NativeSharedStringsTests
{
    private const string Name = "ui/strings/_osiris/multiplayer/forge_palettes.multilingual_unicode_string_list";
    private const ulong Asset = 0x8f45242974f230ca;
    private const ulong NativeChecksum = 0x8abcfbc9ef85e0d8;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectsLatestNativePatchAndReplacesEveryCampaignRevision(bool reverse)
    {
        var bytes = ModuleWriterTests.PaddedTag(); bytes[192] = 73;
        NativeStringCandidate[] candidates = [
            new("deploy/any/levels/globals-rtx-1.module", Entry(1), () => throw new Exception("Stale native payload read")),
            new("deploy/any/levels/globals-rtx-12719-1.module", Entry(NativeChecksum), () => bytes)];
        var strings = new NativeSharedStrings(reverse ? candidates.Reverse() : candidates);
        bytes[192] = 99;
        foreach (var checksum in new ulong[] { 0x40fd499a1d75fb93, 0x37a0a6dffd10a544, 0xefeafbc6383c2ea0 })
        {
            var row = Entry(checksum).Raw;
            Assert.True(strings.TryReplace(row, Name, out var replacement));
            Assert.Equal(NativeChecksum, BinaryPrimitives.ReadUInt64LittleEndian(row.AsSpan(56)));
            Assert.Equal(73, replacement[192]);
            replacement[192] = 0;
        }
        Assert.Equal("8abcfbc9ef85e0d8", Assert.Single(strings.Selections).Checksum);
    }

    [Fact]
    public void RequiresExactIdentityAndPreservesMissionOnlyStrings()
    {
        var strings = Strings(); var row = Entry(1).Raw;
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(48), Asset + 1);
        var original = (byte[])row.Clone();
        Assert.False(strings.TryReplace(row, Name, out _)); Assert.Equal(original, row);
        Assert.False(strings.TryReplace(Entry(1).Raw, "levels/campaign/mission_strings", out _));
        Assert.Throws<CacheException>(() => strings.TryReplace(Entry(1).Raw, "ui/strings/wrong_name", out _));
        var resources = Entry(1).Raw; BinaryPrimitives.WriteInt32LittleEndian(resources.AsSpan(8), 1);
        Assert.Throws<CacheException>(() => strings.TryReplace(resources, Name, out _));
    }

    [Fact]
    public void IgnoresCampaignCandidatesAndRejectsUnexpectedNativeResources()
    {
        Assert.Empty(new NativeSharedStrings([new("deploy/any/levels/campaign/mission.module", Entry(1), () => throw new Exception())]).Selections);
        Assert.Throws<CacheException>(() => new NativeSharedStrings([
            new("deploy/any/levels/globals-rtx-12719-1.module", Entry(1) with { ResourceCount = 1 }, ModuleWriterTests.PaddedTag)]));
    }

    [Fact]
    public void WrittenModuleContainsMatchingNativePayloadAndChecksumAndPreservesNeighbor()
    {
        using var f = new IndexFixture(); var path = Path.Combine(f.Destination, "shared-strings.module");
        var source = Entry(0xefeafbc6383c2ea0).Raw; var row = (byte[])source.Clone();
        Assert.True(Strings().TryReplace(row, Name, out var payload));
        var neighbor = ModuleWriterTests.PaddedTag(); neighbor[192] = 42;
        var header = new byte[48]; "mohd"u8.CopyTo(header);
        var layout = new ModuleWriteLayout(header, [new(row, () => new(payload)),
            new(ModuleWriterTests.Entry(Name.Length + 1, -1, 0, 0, neighbor.Length, "scnr", 2), () => new(neighbor))],
            System.Text.Encoding.UTF8.GetBytes(Name + "\0neighbor\0"), [], 2);
        var result = ModuleWriter.Write(path, layout);
        var metadata = FileMetadata.Read(path, "Module", default);
        Assert.Equal("8abcfbc9ef85e0d8", metadata.Entry(0).Checksum);
        Assert.Equal(0xefeafbc6383c2ea0ul, BinaryPrimitives.ReadUInt64LittleEndian(source.AsSpan(56)));
        using (var stream = File.OpenRead(path))
        {
            Assert.Equal(payload, ModulePayloadReader.Read(stream, metadata, metadata.Entry(0), default).Bytes);
            Assert.Equal(neighbor, ModulePayloadReader.Read(stream, metadata, metadata.Entry(1), default).Bytes);
        }
        Assert.Equal(result.Sha256, ModuleWriter.Write(path, layout).Sha256);
    }

    private static NativeSharedStrings Strings() => new([
        new("deploy/any/levels/globals-rtx-12719-1.module", Entry(NativeChecksum), ModuleWriterTests.PaddedTag)]);

    private static ModuleEntry Entry(ulong checksum)
    {
        var raw = ModuleWriterTests.Entry(0, -1, 0, 0, 227, "unic", 0x74f230ca);
        BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(48), Asset);
        BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(56), checksum);
        return new(0, Name, -1, 0, 0, 0, 0, 0, 227, 227, 0, "unic", "74f230ca", Asset.ToString("x16"), checksum.ToString("x16"), raw);
    }
}
