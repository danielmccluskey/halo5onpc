using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Forge;
using H5SoloLauncher.Core.Planning;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Core.Preparation;

public sealed class InputCacheBuilder
{
    public const string Rules = "source-tag-input-packs-1";
    public const int TargetPackBytes = 32 * 1024 * 1024;
    private const long Reserve = 256L * 1024 * 1024;
    private sealed class Batch(PlannedTag[] tags)
    {
        public PlannedTag[] Tags { get; } = tags;
        public string Key { get; } = InputCacheStore.BatchKey(tags.Select(x => (x.PayloadSha256, x.LogicalBytes)));
        public InputBatch? Saved { get; set; }
        public InputBatchReference? Reference { get; set; }
        public int Written { get; set; }
    }
    public InputResult Run(InputRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation)
    {
        CacheLocation? cache = null; string? current = null; bool locked = false;
        try
        {
            cancellation.ThrowIfCancellationRequested(); ForgePaths.ValidatePackage(request.PackageFullName);
            var source = SourceDiscovery.Describe(request.SourceRoot);
            cache = CacheFolders.Open(request.CacheRoot, source.Root, request.ForgeRoot);
            using var lease = Lock(cache.Root); locked = true;
            InputFiles.CleanWork(cache.Root);
            using var snapshot = new CatalogSnapshot(cache, source);
            var plan = BuildPlanStore.ReadVerified(cache, snapshot, request.PlanId);
            var forge = ForgeReportStore.Read(cache, request.PackageFullName, request.PlanId) ??
                throw new CacheException("FORGE_DATA_REQUIRED", "Prepare Forge data before building conversion inputs.");
            var forgeReport = InputFiles.Read<ForgeReport>(cache.Root, forge.RelativePath, 128L * 1024 * 1024, forge.ReportId);
            if (plan.Tags.Length == 0 || plan.Tags.Length > 150000) throw new CacheException("INPUT_LIMIT", "The saved plan has no supported tags or exceeds the input-cache limit.");
            var inventory = SourceIndexer.Inventory(source.Root, cancellation);
            if (!inventory.Select(x => (x.Relative, x.Kind, x.Length)).SequenceEqual(snapshot.Files.Select(x => (x.Path, x.Kind, x.Length)))) throw Changed();
            CacheLog.Write(cache.Root, $"Building conversion inputs for plan {request.PlanId}; source payloads remain unconverted.");
            var checkedFiles = 0;
            foreach (var file in snapshot.Files)
            {
                current = file.Path; var metadata = FileMetadata.Read(SafePaths.Child(source.Root, file.Path), file.Kind, cancellation);
                if (metadata.Digest != file.Digest || metadata.FileLength != file.Length) throw Changed();
                progress.Report(new("Verifying source tables", ++checkedFiles, snapshot.Files.Length, current));
            }
            var tags = plan.Tags.OrderBy(x => x.File, StringComparer.Ordinal).ThenBy(x => x.Item).ToArray();
            var unique = tags.DistinctBy(x => x.PayloadSha256).ToArray();
            foreach (var tag in tags)
                if (tag.LogicalBytes is <= 0 or > ModulePayloadReader.MaximumTagBytes) throw new CacheException("TAG_SIZE_UNSUPPORTED", "A planned tag exceeds the supported input size.");
            var batches = Batches(unique); var byHash = batches.SelectMany(x => x.Tags.Select(t => (t.PayloadSha256, Batch: x)))
                .ToDictionary(x => x.PayloadSha256, x => x.Batch, StringComparer.Ordinal);
            var reused = 0;
            foreach (var batch in batches)
            {
                cancellation.ThrowIfCancellationRequested(); progress.Report(new("Verifying saved input packs", reused, batches.Length));
                batch.Saved = InputCacheStore.TryBatch(cache.Root, batch.Key, batch.Tags.Select(x => (x.PayloadSha256, x.LogicalBytes)).ToArray(), cancellation);
                if (batch.Saved is not null) { batch.Reference = InputCacheStore.Reference(batch.Saved); reused++; }
            }
            Space(cache.Root, batches.Where(x => x.Saved is null).Sum(x => x.Tags.Sum(t => t.LogicalBytes + 36)) + batches.Length * 8L);
            var files = snapshot.Files.ToDictionary(x => x.Path, StringComparer.Ordinal);
            var written = new HashSet<string>(StringComparer.Ordinal); long sourceBytes = 0; int checkedTags = 0;
            string? openFile = null; FileStream? stream = null; FileMetadata? module = null; InputPackWriter? writer = null;
            try
            {
                foreach (var tag in tags)
                {
                    cancellation.ThrowIfCancellationRequested(); current = tag.File + " / item " + tag.Item;
                    if (openFile != tag.File)
                    {
                        stream?.Dispose(); openFile = null;
                        var path = SafePaths.Child(source.Root, tag.File);
                        stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                        module = FileMetadata.Read(path, "Module", cancellation);
                        if (!files.TryGetValue(tag.File, out var expected) || module.Digest != expected.Digest || module.FileLength != expected.Length) throw Changed();
                        openFile = tag.File;
                    }
                    var entry = module!.Entry(tag.Item);
                    if (entry.Group != tag.Identity.Group || entry.TagId != tag.Identity.TagId || entry.AssetId != tag.Identity.AssetId ||
                        entry.Checksum != tag.Checksum || entry.Name != tag.Name) throw Changed();
                    var payload = ModulePayloadReader.Read(stream!, module, entry, cancellation); sourceBytes += payload.StoredBytesRead;
                    if (payload.Sha256 != tag.PayloadSha256 || payload.Bytes.Length != tag.LogicalBytes) throw Changed();
                    var batch = byHash[tag.PayloadSha256];
                    if (batch.Saved is null && written.Add(tag.PayloadSha256))
                    {
                        Space(cache.Root, payload.Bytes.Length + 1024L * 1024);
                        writer ??= new(cache.Root); writer.Add(payload.Bytes, TagMetadataReader.Read(payload.Bytes)); batch.Written++;
                        if (batch.Written == batch.Tags.Length)
                        {
                            batch.Saved = writer.Complete(batch.Key, cancellation); writer.Dispose(); writer = null;
                            batch.Reference = InputCacheStore.Checkpoint(cache.Root, batch.Saved);
                            progress.Report(new("Input pack saved", checkedTags + 1, tags.Length, current, reused));
                        }
                    }
                    progress.Report(new("Caching verified campaign tags", ++checkedTags, tags.Length, current, reused));
                }
            }
            finally { writer?.Dispose(); stream?.Dispose(); }
            cancellation.ThrowIfCancellationRequested();
            if (!inventory.SequenceEqual(SourceIndexer.Inventory(source.Root, cancellation)) || SourceDiscovery.Describe(source.Root) != source) throw Changed();
            progress.Report(new("Saving conversion input manifest", tags.Length, tags.Length, Reused: reused));
            var records = batches.SelectMany(x => x.Saved!.Records).ToDictionary(x => x.Sha256, StringComparer.Ordinal);
            var missingNames = records.Values.SelectMany(x => x.Metadata.Dependencies).ToLookup(x => x.Identity);
            var evidence = forgeReport.MissingReferences.Select(x => DependencyNames.Compare(x, missingNames[x.Identity])).ToArray();
            var schemas = tags.SelectMany(t => records[t.PayloadSha256].Metadata.RootGuids.Select(guid =>
                (Group: t.Identity.Group, Guid: guid, records[t.PayloadSha256].Metadata.Schema)))
                .GroupBy(x => x).Select(x => new SchemaHeader(x.Key.Group, x.Key.Guid, x.Key.Schema, x.Count()))
                .OrderBy(x => x.Group, StringComparer.Ordinal).ThenBy(x => x.Guid, StringComparer.Ordinal).ThenBy(x => x.Schema, StringComparer.Ordinal).ToArray();
            var manifest = new InputManifest(1, Rules, request.PlanId, plan.InputFingerprint, request.PackageFullName, forge.CatalogId,
                plan.Bundle.Id, plan.Language, batches.Select(x => x.Reference!).ToArray(), schemas, evidence, tags.Length, unique.Length,
                unique.Sum(x => x.LogicalBytes), tags.Count(x => records[x.PayloadSha256].Metadata.RootGuids.Length != 1),
                plan.Issues.Count(x => x.Code == "LAYER_SELECTION_PENDING"), plan.Issues.Count(x => x.Code == "RESOURCE_UNRESOLVED"),
                ["Validate the collected source schema headers against Forge and implement supported converters.",
                 "Validate platform identities and effective patch order; name matches alone are not replacements.",
                 "Read and convert resources, shaders and audio from the supplied dump.",
                 "Assemble game modules, verify access to the generated files and test the campaign in Forge."]);
            var summary = InputCacheStore.Publish(cache, manifest, reused, sourceBytes, cancellation);
            CacheLog.Write(cache.Root, $"Saved input manifest {summary.ManifestId}: {summary.Tags} tags, {summary.UniquePayloads} unique payloads, {summary.Packs} packs, {reused} reused; {summary.NameMatches} potential named platform matches. Game conversion pending.");
            return new("Cached", summary, Message: "Conversion inputs cached. These are verified source tag bytes; game conversion is still required.");
        }
        catch (OperationCanceledException)
        {
            if (cache is not null && locked) CacheLog.TryWrite(cache.Root, "Input caching paused; sealed packs kept.");
            return new("Paused", Message: "Input caching paused. Completed packs were kept. Build conversion inputs again to resume.");
        }
        catch (Exception e)
        {
            if (cache is not null && locked) CacheLog.TryWrite(cache.Root, $"Input caching failed at {current}: {e}");
            return new("Failed", Code: e is CacheException known ? known.Code : "INPUT_CACHE_FAILED",
                Message: e is CacheException ? e.Message : "Conversion inputs could not be cached. Check the dump and cache drives, then retry or copy the details.", Details: $"File: {current ?? "(setup)"}\n{e}");
        }
    }
    private static Batch[] Batches(PlannedTag[] tags)
    {
        List<Batch> result = []; List<PlannedTag> current = []; long bytes = 8;
        foreach (var tag in tags)
        {
            if (current.Count > 0 && bytes + tag.LogicalBytes + 36 > TargetPackBytes)
            { result.Add(new(current.ToArray())); current.Clear(); bytes = 8; }
            current.Add(tag); bytes += tag.LogicalBytes + 36;
        }
        if (current.Count > 0) result.Add(new(current.ToArray())); return result.ToArray();
    }
    private static FileStream Lock(string root)
    {
        try { return new(SafePaths.Child(root, "index.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new CacheException("CACHE_IN_USE", "Another worker is using this cache. Pause it or wait for it to finish, then retry."); }
    }
    private static void Space(string root, long needed)
    {
        if (new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace < Reserve + needed)
            throw new CacheException("CACHE_NEEDS_SPACE", $"Free at least {(Reserve + needed) / (1024.0 * 1024):N0} MiB on the cache drive, then resume. Completed packs were kept.");
    }
    private static CacheException Changed() => new("DUMP_CHANGED", "The dump no longer matches the saved plan. Reindex it and check dependencies again before caching inputs. Previous completed inputs were kept.");
}
