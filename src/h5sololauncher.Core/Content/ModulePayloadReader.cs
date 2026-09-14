using System.IO.Compression;
using System.Security.Cryptography;

namespace H5SoloLauncher.Core.Content;

public sealed record TagPayload(byte[] Bytes, string Sha256, long StoredBytesRead);

/// <summary>Bounded tag decoding. Resource conversion is a separate stage.</summary>
public static class ModulePayloadReader
{
    public const int MaximumTagBytes = 64 * 1024 * 1024;
    public const int MaximumResourceBytes = 512 * 1024 * 1024;

    public static TagPayload Read(Stream source, FileMetadata metadata, ModuleEntry entry, CancellationToken cancellation)
        => ReadBounded(source, metadata, entry, MaximumTagBytes, cancellation);
    public static TagPayload ReadResource(Stream source, FileMetadata metadata, ModuleEntry entry, CancellationToken cancellation)
        => ReadBounded(source, metadata, entry, MaximumResourceBytes, cancellation);
    private static TagPayload ReadBounded(Stream source, FileMetadata metadata, ModuleEntry entry, int maximum, CancellationToken cancellation)
    {
        if (entry.StoredSize <= 0 || entry.LogicalSize <= 0 || entry.LogicalSize > maximum)
            throw new CacheException("TAG_SIZE_UNSUPPORTED", $"This payload has no stored data or exceeds the {maximum / (1024 * 1024)} MiB limit.");
        List<ModuleBlock> blocks = [];
        if (entry.BlockCount == 0)
            blocks.Add(new(0, 0, entry.StoredSize, 0, entry.LogicalSize, (entry.Flags & 1) != 0 && entry.StoredSize < entry.LogicalSize ? 1 : 0, []));
        else for (var i = 0; i < entry.BlockCount; i++) blocks.Add(metadata.Block(entry.BlockIndex + i));
        long cursor = 0;
        foreach (var b in blocks.OrderBy(x => x.LogicalOffset))
        {
            if (b.LogicalOffset != cursor || b.LogicalSize <= 0 || b.LogicalSize > entry.LogicalSize - cursor)
                throw new CacheException("TAG_BLOCK_COVERAGE", "Tag blocks contain a gap, overlap or invalid logical range.");
            cursor += b.LogicalSize;
        }
        if (cursor != entry.LogicalSize) throw new CacheException("TAG_BLOCK_COVERAGE", "Tag blocks do not cover the declared payload.");
        var output = new byte[(int)entry.LogicalSize]; long read = 0;
        foreach (var b in blocks)
        {
            cancellation.ThrowIfCancellationRequested();
            var size = b.Flags == 0 ? b.LogicalSize : b.StoredSize;
            if (size <= 0 || size > maximum || entry.DataOffset < 0 || b.StoredOffset > source.Length - metadata.TableBytes - entry.DataOffset ||
                size > source.Length - metadata.TableBytes - entry.DataOffset - b.StoredOffset)
                throw new CacheException("TAG_PAYLOAD_RANGE", "A tag block points outside the source module.");
            source.Position = metadata.TableBytes + entry.DataOffset + b.StoredOffset;
            var stored = new byte[(int)size]; ReadExactly(source, stored, cancellation); read += size;
            var destination = output.AsSpan((int)b.LogicalOffset, (int)b.LogicalSize);
            if (b.Flags == 0) stored.CopyTo(destination);
            else
            {
                try
                {
                    using var memory = new MemoryStream(stored, writable: false);
                    using var zlib = new ZLibStream(memory, CompressionMode.Decompress);
                    ReadExactly(zlib, destination, cancellation);
                    if (zlib.ReadByte() != -1) throw new InvalidDataException("The block decoded beyond its declared length.");
                }
                catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
                { throw new CacheException("TAG_DECOMPRESSION_FAILED", "A compressed tag block could not be decoded to its declared size. " + e.Message); }
            }
        }
        return new(output, Convert.ToHexString(SHA256.HashData(output)), read);
    }

    private static void ReadExactly(Stream stream, Span<byte> bytes, CancellationToken cancellation)
    {
        while (!bytes.IsEmpty)
        {
            cancellation.ThrowIfCancellationRequested(); var take = Math.Min(bytes.Length, 256 * 1024);
            stream.ReadExactly(bytes[..take]); bytes = bytes[take..];
        }
    }
}
