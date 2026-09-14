using System.Buffers.Binary;
using System.Security.Cryptography;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Core.Preparation;

/// <summary>Shared reader for converters. Each returned payload is verified against its full SHA-256.</summary>
public static class InputPack
{
    internal static ReadOnlySpan<byte> Header => "H5IP\x01\0\0\0"u8;
    public static byte[] Read(string cacheRoot, InputBatch batch, InputRecord record, CancellationToken cancellation = default)
    {
        if (batch.Rules != InputCacheBuilder.Rules || !batch.Records.Contains(record)) throw InputFiles.Damaged();
        using var stream = File.OpenRead(SafePaths.Child(cacheRoot, InputFiles.PathFor("packs", batch.PackId, ".pack")));
        CheckHeader(stream, batch.PackBytes); return ReadRecord(stream, record, cancellation);
    }
    internal static void Verify(string path, InputBatch batch, CancellationToken cancellation)
    {
        using var stream = File.OpenRead(path); CheckHeader(stream, batch.PackBytes);
        if (batch.PackBytes > InputCacheBuilder.TargetPackBytes + ModulePayloadReader.MaximumTagBytes + 4096L ||
            batch.Records.Length == 0) throw InputFiles.Damaged();
        long cursor = Header.Length;
        foreach (var record in batch.Records)
        {
            cancellation.ThrowIfCancellationRequested();
            if (record.Offset != cursor + 36) throw InputFiles.Damaged();
            ReadRecord(stream, record, cancellation); cursor = record.Offset + record.Length;
        }
        if (cursor != stream.Length) throw InputFiles.Damaged();
        stream.Position = 0; if (InputFiles.Digest(stream, cancellation) != batch.PackId) throw InputFiles.Damaged();
    }
    private static void CheckHeader(FileStream stream, long length)
    {
        Span<byte> header = stackalloc byte[8]; stream.ReadExactly(header);
        if (!header.SequenceEqual(Header) || stream.Length != length) throw InputFiles.Damaged();
    }
    private static byte[] ReadRecord(FileStream stream, InputRecord record, CancellationToken cancellation)
    {
        if (record.Length <= 0 || record.Length > ModulePayloadReader.MaximumTagBytes || record.Offset < 44 ||
            record.Offset > stream.Length - record.Length) throw InputFiles.Damaged();
        InputFiles.PathFor("packs", record.Sha256);
        stream.Position = record.Offset - 36; Span<byte> header = stackalloc byte[36]; stream.ReadExactly(header);
        if (BinaryPrimitives.ReadInt32LittleEndian(header) != record.Length || Convert.ToHexString(header[4..]) != record.Sha256) throw InputFiles.Damaged();
        var bytes = new byte[record.Length];
        for (var at = 0; at < bytes.Length;)
        {
            cancellation.ThrowIfCancellationRequested(); var count = Math.Min(256 * 1024, bytes.Length - at);
            stream.ReadExactly(bytes.AsSpan(at, count)); at += count;
        }
        if (InputFiles.Hash(bytes) != record.Sha256) throw InputFiles.Damaged();
        return bytes;
    }
}

internal sealed class InputPackWriter : IDisposable
{
    private readonly string root, temporary;
    private readonly FileStream stream;
    private readonly List<InputRecord> records = [];
    public InputPackWriter(string root)
    {
        this.root = root; Directory.CreateDirectory(SafePaths.Child(root, "inputs/work"));
        temporary = SafePaths.Child(root, "inputs/work/" + Guid.NewGuid().ToString("N") + ".tmp");
        stream = new(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None); stream.Write(InputPack.Header);
    }
    public void Add(byte[] bytes, TagMetadata metadata)
    {
        Span<byte> header = stackalloc byte[36]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        SHA256.HashData(bytes, header[4..]); stream.Write(header);
        records.Add(new(Convert.ToHexString(header[4..]), bytes.Length, stream.Position, metadata)); stream.Write(bytes);
    }
    public InputBatch Complete(string key, CancellationToken cancellation)
    {
        stream.Flush(true); var length = stream.Length; stream.Position = 0;
        var id = InputFiles.Digest(stream, cancellation); stream.Dispose();
        var batch = new InputBatch(InputCacheBuilder.Rules, key, id, length, records.ToArray());
        InputPack.Verify(temporary, batch, cancellation);
        var destination = SafePaths.Child(root, InputFiles.PathFor("packs", id, ".pack"));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
        cancellation.ThrowIfCancellationRequested(); File.Move(temporary, destination, overwrite: true);
        return batch;
    }
    public void Dispose() { stream.Dispose(); if (File.Exists(temporary)) File.Delete(temporary); }
}
