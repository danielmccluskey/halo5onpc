using System.Text.Json;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Core.Preparation;

public static class InputCacheStore
{
    private sealed record Saved(string CacheId, InputSummary Summary);
    internal static string BatchKey(IEnumerable<(string Hash, long Length)> records) =>
        InputFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(new { InputCacheBuilder.Rules, Records = records.Select(x => new { x.Hash, x.Length }).ToArray() }));
    internal static InputBatch? TryBatch(string root, string key, (string Hash, long Length)[] expected, CancellationToken cancellation)
    {
        try
        {
            var reference = InputFiles.Read<InputBatchReference>(root, InputFiles.PathFor("checkpoints", key), 4096);
            if (reference.Key != key) return null;
            var batch = ReadBatch(root, reference);
            if (!batch.Records.Select(x => (x.Sha256, (long)x.Length)).SequenceEqual(expected)) return null;
            InputPack.Verify(SafePaths.Child(root, InputFiles.PathFor("packs", batch.PackId, ".pack")), batch, cancellation);
            return batch;
        }
        catch (Exception e) when (e is IOException or JsonException || e is CacheException { Code: "INPUT_CACHE_DAMAGED" }) { return null; }
    }
    public static InputBatch ReadBatch(string root, InputBatchReference reference)
    {
        var batch = InputFiles.Read<InputBatch>(root, InputFiles.PathFor("batches", reference.Id), 64L * 1024 * 1024, reference.Id);
        if (batch.Rules != InputCacheBuilder.Rules || batch.Key != reference.Key || batch.Records is null || batch.Records.Length is 0 or > 150000 ||
            batch.Records.Any(x => x is null || x.Metadata is null || x.Metadata.Dependencies is null || x.Metadata.RootGuids is null) ||
            BatchKey(batch.Records.Select(x => (x.Sha256, (long)x.Length))) != batch.Key) throw InputFiles.Damaged();
        return batch;
    }
    internal static InputBatchReference Checkpoint(string root, InputBatch batch)
    {
        var reference = new InputBatchReference(InputFiles.Save(root, "batches", batch), batch.Key);
        InputFiles.Write(root, InputFiles.PathFor("checkpoints", batch.Key), JsonSerializer.SerializeToUtf8Bytes(reference));
        return reference;
    }
    internal static InputBatchReference Reference(InputBatch batch) => new(InputFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(batch)), batch.Key);
    internal static InputSummary Publish(CacheLocation cache, InputManifest manifest, int reused, long sourceBytes, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var id = InputFiles.Save(cache.Root, "manifests", manifest);
        var summary = Summary(id, manifest, reused, sourceBytes);
        cancellation.ThrowIfCancellationRequested();
        InputFiles.Write(cache.Root, "inputs/summary.json", JsonSerializer.SerializeToUtf8Bytes(new Saved(cache.Id, summary)));
        return summary;
    }
    public static InputSummary? ReadSummary(CacheLocation cache, string planId, string package, string catalogId)
    {
        var path = SafePaths.Child(cache.Root, "inputs/summary.json"); if (!File.Exists(path)) return null;
        var saved = InputFiles.Read<Saved>(cache.Root, "inputs/summary.json", 16384); var summary = saved.Summary;
        if (saved.CacheId != cache.Id || summary.PlanId != planId || summary.PackageFullName != package || summary.ForgeCatalogId != catalogId ||
            summary.Rules != InputCacheBuilder.Rules) return null;
        var manifest = ReadManifest(cache.Root, summary);
        return Summary(summary.ManifestId, manifest, summary.ReusedPacks, summary.SourceBytesRead);
    }
    public static InputManifest ReadManifest(string root, InputSummary summary)
    {
        if (summary.RelativePath != InputFiles.PathFor("manifests", summary.ManifestId)) throw InputFiles.Damaged();
        var manifest = InputFiles.Read<InputManifest>(root, summary.RelativePath, 64L * 1024 * 1024, summary.ManifestId);
        if (manifest.Format != 1 || manifest.Rules != InputCacheBuilder.Rules || manifest.PlanId != summary.PlanId ||
            manifest.PackageFullName != summary.PackageFullName || manifest.ForgeCatalogId != summary.ForgeCatalogId ||
            manifest.InputFingerprint != summary.InputFingerprint || manifest.Language != summary.Language) throw InputFiles.Damaged();
        return manifest;
    }
    private static InputSummary Summary(string id, InputManifest manifest, int reused, long sourceBytes) =>
        new(id, InputFiles.PathFor("manifests", id), manifest.PlanId, manifest.InputFingerprint, manifest.PackageFullName,
            manifest.ForgeCatalogId, manifest.Language, manifest.Tags, manifest.UniquePayloads, manifest.PayloadBytes,
            manifest.Batches.Length, reused, manifest.MissingReferences.Count(x => x.Names.Length > 0),
            manifest.MissingReferences.Count(x => x.State == "PotentialPlatformMatch"), manifest.SchemaHeaders.Length,
            manifest.TagsWithoutSingleRoot, manifest.PatchChoices, manifest.UnresolvedResources, sourceBytes, manifest.Rules);
}
