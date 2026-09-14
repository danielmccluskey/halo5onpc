using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using H5SoloLauncher.Core.Content;

namespace H5SoloLauncher.Core.Conversion;

public sealed record ModuleWritePayload(byte[] Logical, byte[]? OriginalStoredManifest = null, bool RehashAsset = false);
public sealed record ModuleWriteEntry(byte[] Raw, Func<ModuleWritePayload?> Payload);
public sealed record ModuleWriteLayout(byte[] Header, ModuleWriteEntry[] Entries, byte[] Strings, int[] Resources, int ResourceBoundary);
public sealed record ModuleWriteResult(long Bytes, string Sha256, string TableSha256, int Entries, int Blocks, int VerifiedPayloads);

/// <summary>Streams revision-27 modules, verifies every logical payload and integrity block, then atomically publishes.</summary>
public static class ModuleWriter
{
    public const int PieceBytes = 4 * 1024 * 1024;
    public static ModuleWriteResult Write(string destination, ModuleWriteLayout layout, CancellationToken cancellation = default)
    {
        if (layout.Header.Length is not (48 or 56) || !layout.Header.AsSpan(0, 4).SequenceEqual("mohd"u8) ||
            layout.ResourceBoundary < 0 || layout.ResourceBoundary > layout.Entries.Length || layout.Entries.Any(x => x.Raw.Length != 88)) throw Invalid("The output module layout is invalid.");
        var parent = Path.GetDirectoryName(Path.GetFullPath(destination))!; Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, Guid.NewGuid().ToString("N") + ".tmp");
        var payloadPath = Path.Combine(parent, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var entries = layout.Entries.Select(x => (byte[])x.Raw.Clone()).ToArray();
            using var blocks = new MemoryStream(); List<(int Index, string Sha256)> proofs = [];
            long payloadLength;
            using (var payload = new FileStream(payloadPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 256 * 1024, FileOptions.SequentialScan))
            {
                Span<byte> block = stackalloc byte[32];
                for (var index = 0; index < entries.Length; index++)
                {
                    cancellation.ThrowIfCancellationRequested(); var entry = entries[index]; var value = layout.Entries[index].Payload();
                    var blockIndex = checked((int)(blocks.Length / 32));
                    if (value is null)
                    {
                        if (U(entry, 32) != 0 || U(entry, 36) != 0) throw Invalid("A stored module entry has no converted payload.");
                        W(entry, 16, 0); W(entry, 20, (uint)blockIndex); Q(entry, 24, 0); continue;
                    }
                    var bytes = value.Logical;
                    if (bytes.Length == 0 || bytes.Length > ModulePayloadReader.MaximumResourceBytes) throw Invalid("An output payload exceeds the supported size.");
                    proofs.Add((index, Convert.ToHexString(SHA256.HashData(bytes))));
                    var start = payload.Position; Q(entry, 24, checked((ulong)start));
                    if (value.OriginalStoredManifest is not null)
                    {
                        if (I(entry, 4) != -1 || U(entry, 44) != uint.MaxValue || entry[43] != 5 || U(entry, 16) != 0 ||
                            bytes.Length != checked(4L * U(layout.Header, 20)) || bytes.Length != U(entry, 36) || value.OriginalStoredManifest.Length != U(entry, 32))
                            throw Invalid("The original load manifest doesn't match its module header.");
                        payload.Write(value.OriginalStoredManifest); continue;
                    }
                    List<(int Start, int Length)> pieces = [];
                    if (bytes.AsSpan().StartsWith("ucsh"u8))
                    {
                        var document = new TagDocument(bytes, resource: true);
                        var hs = document.HeaderSize; var ds = document.DataSize; var rs = document.ResourceSize;
                        pieces.Add((0, hs));
                        for (var at = hs; at < hs + ds; at += PieceBytes) pieces.Add((at, Math.Min(PieceBytes, hs + ds - at)));
                        for (var at = hs + ds; at < bytes.Length; at += PieceBytes) pieces.Add((at, Math.Min(PieceBytes, bytes.Length - at)));
                        bytes.AsSpan(72, 3).CopyTo(entry.AsSpan(40)); W(entry, 68, (uint)hs); W(entry, 72, (uint)ds); W(entry, 76, (uint)rs);
                        H(entry, 80, 1); H(entry, 82, checked((ushort)((ds + (long)PieceBytes - 1) / PieceBytes))); H(entry, 84, checked((ushort)((rs + (long)PieceBytes - 1) / PieceBytes)));
                    }
                    else
                    {
                        if (U(entry, 68) != 0 || U(entry, 72) != 0 || U(entry, 76) != 0) throw Invalid("A raw resource unexpectedly declares tag sections.");
                        for (var at = 0; at < bytes.Length; at += PieceBytes) pieces.Add((at, Math.Min(PieceBytes, bytes.Length - at)));
                        entry.AsSpan(80, 6).Clear();
                    }
                    var anyCompressed = false;
                    foreach (var piece in pieces)
                    {
                        cancellation.ThrowIfCancellationRequested(); var part = bytes.AsSpan(piece.Start, piece.Length);
                        using var compressed = new MemoryStream();
                        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) zlib.Write(part);
                        var useCompressed = compressed.Length < piece.Length;
                        var storedLength = useCompressed ? checked((int)compressed.Length) : piece.Length;
                        block.Clear(); BinaryPrimitives.WriteUInt64LittleEndian(block, ModuleChecksum.Hash(part));
                        BinaryPrimitives.WriteUInt32LittleEndian(block[8..], checked((uint)(payload.Position - start)));
                        BinaryPrimitives.WriteUInt32LittleEndian(block[12..], (uint)storedLength);
                        BinaryPrimitives.WriteUInt32LittleEndian(block[16..], (uint)piece.Start);
                        BinaryPrimitives.WriteUInt32LittleEndian(block[20..], (uint)piece.Length);
                        BinaryPrimitives.WriteUInt32LittleEndian(block[24..], useCompressed ? 1u : 0u); blocks.Write(block);
                        if (useCompressed) payload.Write(compressed.GetBuffer().AsSpan(0, storedLength)); else payload.Write(part);
                        anyCompressed |= useCompressed;
                    }
                    W(entry, 16, (uint)pieces.Count); W(entry, 20, (uint)blockIndex); W(entry, 32, checked((uint)(payload.Position - start))); W(entry, 36, (uint)bytes.Length);
                    entry[43] = (byte)((entry[43] & ~1) | 2 | (anyCompressed ? 1 : 0));
                    if (value.RehashAsset) Q(entry, 56, ModuleChecksum.Hash(bytes));
                }
                payload.Flush(true); payloadLength = payload.Length;
            }
            var size = checked(56L + entries.Length * 88L + layout.Strings.Length + layout.Resources.Length * 4L + blocks.Length);
            if (size > FileMetadata.MaximumTableBytes) throw Invalid("The converted module tables exceed the supported size.");
            var tables = new byte[(int)size]; layout.Header.AsSpan(0, 48).CopyTo(tables); W(tables, 4, 27); W(tables, 16, (uint)entries.Length);
            W(tables, 24, (uint)layout.ResourceBoundary); W(tables, 28, (uint)layout.Strings.Length); W(tables, 32, (uint)layout.Resources.Length); W(tables, 36, checked((uint)(blocks.Length / 32)));
            var cursor = 56; foreach (var entry in entries) { entry.CopyTo(tables, cursor); cursor += 88; }
            layout.Strings.CopyTo(tables, cursor); cursor += layout.Strings.Length;
            foreach (var resource in layout.Resources) { W(tables, cursor, checked((uint)resource)); cursor += 4; }
            blocks.GetBuffer().AsSpan(0, (int)blocks.Length).CopyTo(tables.AsSpan(cursor)); Q(tables, 48, ModuleChecksum.Metadata(tables));
            var metadata = FileMetadata.FromModuleTables(tables, tables.Length + payloadLength, 0);
            ValidateGraph(metadata, layout.ResourceBoundary);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024))
            using (var payload = File.OpenRead(payloadPath))
            {
                output.Write(tables); var buffer = new byte[256 * 1024]; int read;
                while ((read = payload.Read(buffer)) > 0) { cancellation.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); }
                output.Flush(true);
            }
            var written = FileMetadata.Read(temporary, "Module", cancellation);
            if (!written.Tables.AsSpan().SequenceEqual(tables) || ModuleChecksum.Metadata(written.Tables) != BinaryPrimitives.ReadUInt64LittleEndian(tables.AsSpan(48))) throw Invalid("The output module tables failed verification.");
            string digest;
            using (var output = File.OpenRead(temporary))
            {
                foreach (var proof in proofs)
                {
                    var entry = written.Entry(proof.Index); var decoded = ModulePayloadReader.ReadResource(output, written, entry, cancellation);
                    if (decoded.Sha256 != proof.Sha256) throw Invalid("A converted payload failed read-back verification.");
                    for (var i = 0; i < entry.BlockCount; i++)
                    {
                        var block = written.Block(entry.BlockIndex + i);
                        if (ModuleChecksum.Hash(decoded.Bytes.AsSpan((int)block.LogicalOffset, (int)block.LogicalSize)) != BinaryPrimitives.ReadUInt64LittleEndian(block.Raw))
                            throw Invalid("A converted block failed its native integrity check.");
                    }
                }
                output.Position = 0; digest = Preparation.InputFiles.Digest(output, cancellation);
            }
            cancellation.ThrowIfCancellationRequested();
            // A verified immutable output may already be open inside Forge.
            // Keep that file when regeneration produces exactly the same bytes.
            var identical=false;
            if(File.Exists(destination))using(var existing=File.OpenRead(destination))
                identical=existing.Length==written.FileLength && Preparation.InputFiles.Digest(existing,cancellation)==digest;
            if(!identical)File.Move(temporary, destination, overwrite: true);
            return new(written.FileLength, digest, written.Digest, entries.Length, written.BlockCount, proofs.Count);
        }
        finally
        {
            // Both paths are direct, unique children of the explicit output directory, created by this operation.
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(payloadPath)) File.Delete(payloadPath);
        }
    }

    private static void ValidateGraph(FileMetadata metadata, int boundary)
    {
        var linked = new HashSet<int>();
        for (var i = 0; i < metadata.ItemCount; i++)
        {
            var entry = metadata.Entry(i);
            if ((entry.Parent == -1) != (i < boundary)) throw Invalid("The module's tag/resource boundary is inconsistent.");
            for (var j = 0; j < entry.ResourceCount; j++)
            {
                var child = metadata.Resource(entry.ResourceIndex + j);
                if (metadata.Entry(child).Parent != i || !linked.Add(child)) throw Invalid("The module resource graph has an invalid owner or duplicate link.");
            }
        }
        for (var i = boundary; i < metadata.ItemCount; i++) if (!linked.Contains(i)) throw Invalid("The module contains an unowned resource.");
    }
    private static uint U(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
    private static int I(byte[] b, int at) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at));
    private static void W(byte[] b, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), value);
    private static void Q(byte[] b, int at, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(at), value);
    private static void H(byte[] b, int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), value);
    private static CacheException Invalid(string message) => new("MODULE_OUTPUT_INVALID", message);
}
