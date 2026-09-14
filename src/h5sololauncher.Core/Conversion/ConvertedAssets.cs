using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Forge;
using H5SoloLauncher.Core.Planning;
using H5SoloLauncher.Core.Preparation;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Core.Conversion;

public sealed record ConvertedAsset(int Item, string Name, byte[] Entry, int[] Resources, long Offset, int Length, string? Sha256, string Method);
public sealed record ConvertedAssetBatch(string Rules, string Key, string SourceFile, string SourceDigest, string PackId, long PackBytes, ConvertedAsset[] Assets);
public sealed record ConvertedAssetManifest(int Format, string Rules, string EffectivePlanId, string[] BatchIds, int Tags, int Resources, long Bytes);
public sealed record AssetConversionResult(string State, string? ManifestId = null, int Tags = 0, int Resources = 0, int ReusedModules = 0, string? Code = null, string? Message = null, string? Details = null);

/// <summary>Sealed resource packs are independent of module assembly and can be reused after interruption.</summary>
public static class ConvertedAssets
{
    public const string Rules = "campaign-assets-1";
    internal static ReadOnlySpan<byte> Header => "H5CA\x01\0\0\0"u8;
    public static AssetConversionResult Run(ConversionAuditRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation)
    {
        string? current = null; var tags = 0; var resources = 0; var reused = 0;
        try
        {
            var input = request.Inputs; var source = SourceDiscovery.Describe(input.SourceRoot); var cache = CacheFolders.Open(input.CacheRoot, source.Root, input.ForgeRoot);
            using var lease = new FileStream(SafePaths.Child(cache.Root, "index.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var catalog = new CatalogSnapshot(cache, source); BuildPlanStore.ReadVerified(cache, catalog, input.PlanId);
            var plan = InputFiles.Read<EffectivePlan>(cache.Root, InputFiles.PathFor("effective-plans", request.EffectivePlanId), 128L * 1024 * 1024, request.EffectivePlanId);
            if (plan.SourcePlanId != input.PlanId || plan.InputFingerprint != catalog.Fingerprint || plan.Rules != EffectivePlanBuilder.Rules) throw InputFiles.Damaged();
            var schemas = InputFiles.Read<NativeSchemaSnapshot>(cache.Root, InputFiles.PathFor("native-schemas", plan.SchemaId), 4 * 1024 * 1024, plan.SchemaId);
            var converter = new StructuralConverter(schemas); var files = catalog.Files.ToDictionary(x => x.Path);
            List<string> batches = []; long bytes = 0;
            foreach (var group in plan.Tags.GroupBy(x => x.File).OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                cancellation.ThrowIfCancellationRequested(); current = group.Key;
                var path = SafePaths.Child(source.Root, group.Key); var metadata = FileMetadata.Read(path, "Module", cancellation);
                if (metadata.Digest != files[group.Key].Digest || metadata.FileLength != files[group.Key].Length) throw new CacheException("DUMP_CHANGED", "A source module changed after indexing.");
                var key = InputFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(new { Rules, Plan = request.EffectivePlanId, File = group.Key, metadata.Digest }));
                var checkpoint = InputFiles.PathFor("asset-checkpoints", key);
                ConvertedAssetBatch? batch = null;
                if (File.Exists(SafePaths.Child(cache.Root, checkpoint)))
                {
                    try
                    {
                        var old = InputFiles.Read<ConvertedAssetBatch>(cache.Root, checkpoint, 32 * 1024 * 1024);
                        if (old.Rules == Rules && old.Key == key && old.SourceFile == group.Key && old.SourceDigest == metadata.Digest)
                        { Verify(cache.Root, old, cancellation); batch = old; reused++; }
                    }
                    catch (Exception e) when (e is IOException or CacheException or JsonException) { /* Rebuild this module's owned conversion pack. */ }
                }
                if (batch is null)
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var writer = new ConvertedAssetWriter(cache.Root);
                    var emitted = new HashSet<int>();
                    foreach (var tag in group.OrderBy(x => x.Item))
                    {
                        cancellation.ThrowIfCancellationRequested(); current = group.Key + " / item " + tag.Item;
                        var entry = metadata.Entry(tag.Item); var payload = ModulePayloadReader.Read(stream, metadata, entry, cancellation);
                        if (payload.Sha256 != tag.Sha256) throw new CacheException("DUMP_CHANGED", "A source tag changed after selecting campaign layers.");
                        var document = new TagDocument(payload.Bytes); var converted = converter.Convert(tag.Identity.Group, document);
                        int[] Children(ModuleEntry value) => Enumerable.Range(value.ResourceIndex, value.ResourceCount).Select(metadata.Resource).ToArray();
                        void Add(ModuleEntry value, byte[]? data, string method)
                        {
                            if (!emitted.Add(value.Index)) throw new CacheException("RESOURCE_GRAPH_DUPLICATE", "A resource is claimed more than once in the selected campaign graph.");
                            if (data is null && (value.StoredSize != 0 || value.LogicalSize != 0)) throw new CacheException("RESOURCE_STRIPPED_INVALID", "An empty resource still declares stored or logical bytes.");
                            writer.Add(value, Children(value), data, method);
                        }
                        Add(entry, converted.Bytes, converted.Method);
                        if (tag.Identity.Group == "bitm" && document.Metadata.Schema == BitmapConverter.SourceTagSchema)
                        {
                            var children = Children(entry);
                            var imageRef = document.Structures.Single(x => x.FieldBlock == 0 && x.FieldOffset == 240);
                            var images = imageRef.Target < 0 ? [] : document.Block(imageRef.Target).ToArray();
                            if (images.Length != children.Length * 40) throw new CacheException("BITMAP_IMAGES", "The bitmap image count differs from its resource graph.");
                            for (var j = 0; j < children.Length; j++)
                            {
                                var child = metadata.Entry(children[j]); if (child.Parent != entry.Index) throw Parent();
                                var chunks = Children(child);
                                if (child.StoredSize == 0)
                                {
                                    Add(child, null, "StrippedLayerResource");
                                    foreach (var chunk in chunks.Select(metadata.Entry))
                                    {
                                        if (chunk.Parent != child.Index || chunk.ResourceCount != 0 || chunk.StoredSize != 0 || chunk.LogicalSize != 0)
                                            throw new CacheException("BITMAP_STRIPPED_CHUNKS", "A stripped texture has a stored or unsupported streamed chunk.");
                                        Add(chunk, null, "StrippedLayerResource");
                                    }
                                    continue;
                                }
                                var chunkEntries = chunks.Select(metadata.Entry).ToArray();
                                if (chunkEntries.Any(x => x.Parent != child.Index || x.ResourceCount != 0)) throw Parent();
                                var chunkBytes = chunkEntries.Select(x => ModulePayloadReader.ReadResource(stream, metadata, x, cancellation).Bytes).ToArray();
                                var resource = new TagDocument(ModulePayloadReader.ReadResource(stream, metadata, child, cancellation).Bytes, resource: true);
                                var bitmap = BitmapConverter.Convert(images.AsSpan(j * 40, 40), resource, chunkBytes, TextureAddresses.BuiltIn, cancellation);
                                var check = converter.Convert("bitm/resource", new TagDocument(bitmap.Resource, resource: true));
                                if (check.Method != "NativeLayout") throw new CacheException("BITMAP_NATIVE_LAYOUT", "The converted texture resource doesn't match Forge's native layout.");
                                Add(child, bitmap.Resource, "UntiledBitmapResource");
                                for (var k = 0; k < chunks.Length; k++) Add(chunkEntries[k], bitmap.Chunks[k], "UntiledBitmapMip");
                            }
                        }
                        else
                        {
                            Queue<(int Index, int Parent)> pending = new(Children(entry).Select(x => (x, entry.Index)));
                            while (pending.TryDequeue(out var next))
                            {
                                cancellation.ThrowIfCancellationRequested(); var child = metadata.Entry(next.Index);
                                if (child.Parent != next.Parent) throw Parent();
                                byte[]? data = child.StoredSize == 0 ? null : ModulePayloadReader.ReadResource(stream, metadata, child, cancellation).Bytes;
                                var method = data is null ? "StrippedLayerResource" : "RawResource";
                                if (data is not null && data.AsSpan().StartsWith("ucsh"u8))
                                {
                                    var value = converter.Convert(tag.Identity.Group + "/resource", new TagDocument(data, resource: true)); data = value.Bytes; method = value.Method;
                                }
                                Add(child, data, method);
                                foreach (var item in Children(child)) pending.Enqueue((item, child.Index));
                            }
                        }
                        progress.Report(new("Converting campaign assets", tags + writer.TagCount, plan.Tags.Length, current, reused));
                    }
                    var after = new FileInfo(path);
                    if (after.Length != metadata.FileLength || after.LastWriteTimeUtc.Ticks != metadata.Ticks) throw new CacheException("DUMP_CHANGED", "The source changed during conversion. Let extraction finish before resuming.");
                    batch = writer.Complete(key, group.Key, metadata.Digest, cancellation);
                    InputFiles.Write(cache.Root, checkpoint, JsonSerializer.SerializeToUtf8Bytes(batch));
                }
                tags += batch.Assets.Count(x => BinaryPrimitives.ReadInt32LittleEndian(x.Entry.AsSpan(4)) == -1);
                resources += batch.Assets.Count(x => BinaryPrimitives.ReadInt32LittleEndian(x.Entry.AsSpan(4)) != -1); bytes += batch.PackBytes;
                batches.Add(InputFiles.Save(cache.Root, "asset-batches", batch));
                progress.Report(new("Converting campaign assets", tags, plan.Tags.Length, group.Key, reused));
            }
            var manifest = new ConvertedAssetManifest(1, Rules, request.EffectivePlanId, batches.ToArray(), tags, resources, bytes);
            var id = InputFiles.Save(cache.Root, "asset-manifests", manifest);
            CacheLog.Write(cache.Root, $"Converted campaign assets {id}: {tags} tags, {resources} resources, {bytes} bytes. Shader program conversion and module assembly remain required.");
            return new("Converted", id, tags, resources, reused, Message: "Campaign tags and resources converted. Shader programs and game modules still need preparation.");
        }
        catch (OperationCanceledException) { return new("Paused", Tags: tags, Resources: resources, ReusedModules: reused, Message: "Asset conversion paused. Completed module packs were kept."); }
        catch (Exception e) { return new("Failed", Tags: tags, Resources: resources, ReusedModules: reused, Code: e is CacheException known ? known.Code : "ASSET_CONVERSION_FAILED", Message: e.Message, Details: current + Environment.NewLine + e); }
    }

    public static byte[] Read(string root, ConvertedAssetBatch batch, ConvertedAsset asset, CancellationToken cancellation = default)
    {
        if (batch.Rules != Rules || !batch.Assets.Contains(asset) || asset.Length <= 0) throw InputFiles.Damaged();
        using var stream = File.OpenRead(SafePaths.Child(root, InputFiles.PathFor("asset-packs", batch.PackId, ".pack")));
        CheckHeader(stream);
        return ReadRecord(stream, batch.PackBytes, asset, cancellation);
    }
    internal static void Verify(string root, ConvertedAssetBatch batch, CancellationToken cancellation)
    {
        using var stream = File.OpenRead(SafePaths.Child(root, InputFiles.PathFor("asset-packs", batch.PackId, ".pack")));
        VerifyStream(stream, batch, cancellation);
    }
    internal static void VerifyStream(FileStream stream, ConvertedAssetBatch batch, CancellationToken cancellation)
    {
        if (stream.Length != batch.PackBytes) throw InputFiles.Damaged();
        CheckHeader(stream);
        long cursor = Header.Length;
        foreach (var asset in batch.Assets)
        {
            cancellation.ThrowIfCancellationRequested();
            if (asset.Length == 0) { if (asset.Offset != 0 || asset.Sha256 is not null) throw InputFiles.Damaged(); continue; }
            if (asset.Offset != cursor + 36) throw InputFiles.Damaged();
            ReadRecord(stream, batch.PackBytes, asset, cancellation); cursor = asset.Offset + asset.Length;
        }
        if (cursor != stream.Length) throw InputFiles.Damaged();
        stream.Position = 0; if (InputFiles.Digest(stream, cancellation) != batch.PackId) throw InputFiles.Damaged();
    }
    private static byte[] ReadRecord(FileStream stream, long length, ConvertedAsset asset, CancellationToken cancellation)
    {
        if (stream.Length != length || asset.Length <= 0 || asset.Length > ModulePayloadReader.MaximumResourceBytes || asset.Offset < 44 || asset.Offset > length - asset.Length || asset.Sha256 is null) throw InputFiles.Damaged();
        stream.Position = asset.Offset - 36; Span<byte> record = stackalloc byte[36]; stream.ReadExactly(record);
        if (BinaryPrimitives.ReadInt32LittleEndian(record) != asset.Length || System.Convert.ToHexString(record[4..]) != asset.Sha256) throw InputFiles.Damaged();
        var bytes = new byte[asset.Length];
        for (var at = 0; at < bytes.Length;)
        { cancellation.ThrowIfCancellationRequested(); var take = Math.Min(256 * 1024, bytes.Length - at); stream.ReadExactly(bytes.AsSpan(at, take)); at += take; }
        if (InputFiles.Hash(bytes) != asset.Sha256) throw InputFiles.Damaged(); return bytes;
    }
    private static void CheckHeader(FileStream stream)
    { stream.Position = 0; Span<byte> header = stackalloc byte[8]; stream.ReadExactly(header); if (!header.SequenceEqual(Header)) throw InputFiles.Damaged(); }
    private static CacheException Parent() => new("RESOURCE_PARENT", "A resource belongs to a different tag or has an unsupported child graph.");
}

internal sealed class ConvertedAssetWriter : IDisposable
{
    private readonly string root, temporary;
    private readonly FileStream stream;
    private readonly List<ConvertedAsset> assets = [];
    public int TagCount { get; private set; }
    public ConvertedAssetWriter(string root)
    {
        this.root = root; Directory.CreateDirectory(SafePaths.Child(root, "inputs/work"));
        temporary = SafePaths.Child(root, "inputs/work/" + Guid.NewGuid().ToString("N") + ".tmp");
        stream = new(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None); stream.Write(ConvertedAssets.Header);
    }
    public void Add(ModuleEntry entry, int[] resources, byte[]? bytes, string method)
    {
        if (entry.Parent == -1) TagCount++;
        if (bytes is null) { assets.Add(new(entry.Index, entry.Name, entry.Raw, resources, 0, 0, null, method)); return; }
        Span<byte> header = stackalloc byte[36]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length); SHA256.HashData(bytes, header[4..]); stream.Write(header);
        assets.Add(new(entry.Index, entry.Name, entry.Raw, resources, stream.Position, bytes.Length, Convert.ToHexString(header[4..]), method)); stream.Write(bytes);
    }
    public ConvertedAssetBatch Complete(string key, string source, string digest, CancellationToken cancellation)
    {
        stream.Flush(true); var length = stream.Length; stream.Position = 0; var id = InputFiles.Digest(stream, cancellation);
        var batch = new ConvertedAssetBatch(ConvertedAssets.Rules, key, source, digest, id, length, assets.ToArray());
        ConvertedAssets.VerifyStream(stream, batch, cancellation); stream.Dispose();
        var destination = SafePaths.Child(root, InputFiles.PathFor("asset-packs", id, ".pack")); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        cancellation.ThrowIfCancellationRequested(); File.Move(temporary, destination, overwrite: true); return batch;
    }
    public void Dispose() { stream.Dispose(); if (File.Exists(temporary)) File.Delete(temporary); }
}
