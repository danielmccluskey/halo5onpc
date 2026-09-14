using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Conversion;
using Xunit;

namespace H5SoloLauncher.Tests;

public sealed class ModuleWriterTests
{
    [Fact]
    public void ReusesIdenticalOutputWhileForgeHasAReadHandle()
    {
        using var f=new IndexFixture();var destination=Path.Combine(f.Destination,"reusable.module");
        var header=new byte[48];"mohd"u8.CopyTo(header);var payload=PaddedTag();
        var layout=new ModuleWriteLayout(header,[new(Entry(0,-1,0,0,payload.Length,"scnr",1),()=>new(payload))],"root\0"u8.ToArray(),[],1);
        var first=ModuleWriter.Write(destination,layout);var before=File.GetLastWriteTimeUtc(destination);
        using var game=new FileStream(destination,FileMode.Open,FileAccess.Read,FileShare.Read);
        Assert.Equal(first,ModuleWriter.Write(destination,layout));Assert.Equal(before,File.GetLastWriteTimeUtc(destination));
        Assert.Empty(Directory.GetFiles(f.Destination,"*.tmp"));
    }
    [Fact]
    public void WritesExplicitSectionsAndResourcesWithRoundTripAndNativeIntegrity()
    {
        using var f = new IndexFixture(); var destination = Path.Combine(f.Destination, "verified.module");
        var tag = PaddedTag(); var resource = new byte[ModuleWriter.PieceBytes + 73]; new Random(71).NextBytes(resource);
        var root = Entry(0, -1, 1, 0, tag.Length, "scnr", 1); var child = Entry(5, 0, 0, 1, resource.Length, "????", uint.MaxValue);
        var header = new byte[48]; "mohd"u8.CopyTo(header); W(header, 4, 23);
        var layout = new ModuleWriteLayout(header, [new(root, () => new(tag)), new(child, () => new(resource))], "root\0resource\0"u8.ToArray(), [1], 1);
        var result = ModuleWriter.Write(destination, layout);
        Assert.Equal(5, result.Blocks); Assert.Equal(2, result.VerifiedPayloads);
        var metadata = FileMetadata.Read(destination, "Module", default); Assert.Equal(27, metadata.Revision);
        Assert.Equal(ModuleChecksum.Metadata(metadata.Tables), BinaryPrimitives.ReadUInt64LittleEndian(metadata.Header.AsSpan(48)));
        Assert.Equal(1, metadata.Resource(0)); Assert.Equal(0, metadata.Entry(1).Parent);
        using var stream = File.OpenRead(destination);
        Assert.Equal(tag, ModulePayloadReader.Read(stream, metadata, metadata.Entry(0), default).Bytes);
        Assert.Equal(resource, ModulePayloadReader.ReadResource(stream, metadata, metadata.Entry(1), default).Bytes);
        var document = new TagDocument(tag); Assert.Equal(new byte[] { 17, 18, 19 }, document.Block(1).ToArray());
        Assert.Equal(224, document.BlockOffset(1));
        Assert.Empty(Directory.GetFiles(f.Destination, "*.tmp"));
    }

    [Fact]
    public void CancellationPreservesPublishedFileAndRemovesOnlyTemporaryOutput()
    {
        using var f = new IndexFixture(); Directory.CreateDirectory(f.Destination); var destination = Path.Combine(f.Destination, "verified.module");
        File.WriteAllBytes(destination, [9, 8, 7]); using var stop = new CancellationTokenSource();
        var header = new byte[48]; "mohd"u8.CopyTo(header);
        var layout = new ModuleWriteLayout(header, [new(Entry(0, -1, 0, 0, 3, "test", 1), () => { stop.Cancel(); return new([1, 2, 3]); })], "root\0"u8.ToArray(), [], 1);
        Assert.Throws<OperationCanceledException>(() => ModuleWriter.Write(destination, layout, stop.Token));
        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(destination)); Assert.Empty(Directory.GetFiles(f.Destination, "*.tmp"));
    }

    [Fact]
    public void PreservesLoadManifestStorageAndStrippedResourceLinks()
    {
        using var f = new IndexFixture(); var destination = Path.Combine(f.Destination, "manifest.module");
        var manifest = Enumerable.Repeat((byte)1, 128).ToArray(); using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.SmallestSize, true)) z.Write(manifest);
        var stored = compressed.ToArray(); var header = new byte[48]; "mohd"u8.CopyTo(header); W(header, 20, 32);
        var root = Entry(0, -1, 1, 0, manifest.Length, "????", uint.MaxValue); root[43] = 5; W(root, 32, (uint)stored.Length);
        var child = Entry(13, 0, 0, 1, 0, "????", uint.MaxValue);
        var layout = new ModuleWriteLayout(header, [new(root, () => new(manifest, stored)), new(child, () => null)], "loadmanifest\0stripped\0"u8.ToArray(), [1], 1);
        var result = ModuleWriter.Write(destination, layout); Assert.Equal(0, result.Blocks);
        var metadata = FileMetadata.Read(destination, "Module", default); Assert.Equal(0, metadata.Entry(1).StoredSize); Assert.Equal(1, metadata.Resource(0));
        using var stream = File.OpenRead(destination); stream.Position = metadata.TableBytes;
        var found = new byte[stored.Length]; stream.ReadExactly(found); Assert.Equal(stored, found);
    }

    [Fact]
    public void RejectsResourceGraphCyclesBeforePublishing()
    {
        using var f = new IndexFixture(); var destination = Path.Combine(f.Destination, "invalid.module");
        var header = new byte[48]; "mohd"u8.CopyTo(header);
        var layout = new ModuleWriteLayout(header, [new(Entry(0, -1, 1, 0, 0, "test", 1), () => null)], "root\0"u8.ToArray(), [0], 1);
        Assert.Throws<CacheException>(() => ModuleWriter.Write(destination, layout)); Assert.False(File.Exists(destination));
    }

    internal static byte[] PaddedTag()
    {
        var bytes = new byte[227]; "ucsh"u8.CopyTo(bytes); W(bytes, 32, 2); W(bytes, 60, 192); W(bytes, 64, 32); W(bytes, 68, 3);
        W(bytes, 80, 4); BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(86), 1);
        W(bytes, 96, 3); BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(102), 2);
        bytes[192] = 1; bytes[224] = 17; bytes[225] = 18; bytes[226] = 19; return bytes;
    }
    internal static byte[] Entry(int name, int parent, int resources, int resourceAt, int size, string group, uint id)
    {
        var raw = new byte[88]; W(raw, 0, (uint)name); BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(4), parent);
        W(raw, 8, (uint)resources); W(raw, 12, (uint)resourceAt); W(raw, 32, (uint)size); W(raw, 36, (uint)size); W(raw, 44, id);
        Encoding.ASCII.GetBytes(new string(group.Reverse().ToArray())).CopyTo(raw, 64); return raw;
    }
    private static void W(byte[] b, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), value);
}
