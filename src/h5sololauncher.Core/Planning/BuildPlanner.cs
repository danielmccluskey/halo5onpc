using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Core.Planning;

public sealed record PlanOptions(string[] Languages, PlanSummary? SavedPlan);

public sealed class BuildPlanner
{
    public const string Rules = "source-closure-all-variants-1";
    public static PlanOptions Inspect(string root, string sourceRoot, string? forgeRoot = null)
    {
        var source = SourceDiscovery.Describe(sourceRoot); var cache = CacheFolders.Open(root, source.Root, forgeRoot);
        using var snapshot = new CatalogSnapshot(cache, source);
        return new(snapshot.Languages(), BuildPlanStore.ReadSummary(cache, snapshot.Fingerprint));
    }

    public PlanResult Run(PlanRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation, ContentBundle? bundleOverride = null)
    {
        string? current = null;
        CacheLocation? activeCache = null; bool ownsLease = false;
        try
        {
            var bundle = bundleOverride ?? ContentBundles.Get(request.BundleId);
            var source = SourceDiscovery.Describe(request.SourceRoot); var cache = CacheFolders.Open(request.CacheRoot, source.Root, request.ForgeRoot);
            using var lease = Lock(cache.Root);
            activeCache = cache; ownsLease = true;
            CacheLog.Write(cache.Root, $"Started source dependency plan: {bundle.Id}, language {request.Language}.");
            using var snapshot = new CatalogSnapshot(cache, source);
            if (!snapshot.Languages().Contains(request.Language, StringComparer.Ordinal))
                throw new CacheException("AUDIO_LANGUAGE_MISSING", "The selected voice language is not present in this index. Choose an available language.");
            var inventory = SourceIndexer.Inventory(source.Root, cancellation);
            if (!inventory.Select(x => (x.Relative, x.Kind, x.Length)).SequenceEqual(snapshot.Files.Select(x => (x.Path, x.Kind, x.Length))))
                throw Changed();
            var files = snapshot.Files.ToDictionary(x => x.Path, StringComparer.Ordinal);
            // All indexed tables participate in candidate discovery, so verify every table.
            // This still avoids reading unrelated game payloads.
            var verified = 0;
            foreach (var file in snapshot.Files)
            {
                current = file.Path; cancellation.ThrowIfCancellationRequested();
                var metadata = FileMetadata.Read(SafePaths.Child(source.Root, file.Path), file.Kind, cancellation);
                if (metadata.FileLength != file.Length || metadata.Digest != file.Digest) throw Changed();
                progress.Report(new("Checking indexed files", ++verified, snapshot.Files.Length, current));
            }
            Space(cache.Root);
            using var store = new BuildPlanStore(cache);
            var roots = new SortedSet<string>(StringComparer.Ordinal); List<PlanIssue> issues = [];
            var queue = new SortedDictionary<string, CatalogTag>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal); var rootTags = new HashSet<string>(StringComparer.Ordinal);
            var requested = new HashSet<AssetReference>(); List<PlannedTag> tags = [];
            foreach (var scenario in bundle.Scenarios)
            {
                if (scenario.Contains("..", StringComparison.Ordinal) || scenario.StartsWith('/') || scenario.Contains('\\'))
                    throw new CacheException("CONTENT_INVALID", "The campaign recipe has an invalid scenario path.");
                var folder = scenario[..scenario.LastIndexOf('/')]; var found = false;
                foreach (var file in snapshot.Files.Where(f => f.Kind == "Module" &&
                    (Parent(f.Path) == "deploy/any/" + folder || Parent(f.Path) == "deploy/x1/" + folder)))
                {
                    roots.Add(file.Path);
                    foreach (var tag in snapshot.InModule(file.Path))
                    {
                        if (tag.Identity.Group == "scnr" && tag.Name.Replace('\\', '/') == scenario + ".scenario") found = true;
                        Enqueue(tag); rootTags.Add(Key(tag));
                    }
                }
                if (!found) issues.Add(new("SCENARIO_MISSING", "The bundle's scenario is not stored in the selected dump: " + scenario));
            }
            var globalFiles = snapshot.Files.Where(f => f.Kind == "Module" &&
                (Parent(f.Path) == "deploy/any/levels" || Parent(f.Path) == "deploy/x1/levels") && Path.GetFileName(f.Path).StartsWith("globals-", StringComparison.Ordinal))
                .Select(x => x.Path).ToHashSet(StringComparer.Ordinal);
            var reused = 0;
            string? openPath = null; FileMetadata? module = null; FileStream? stream = null;
            try
            {
                while (queue.Count > 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (visited.Count > 150000 || issues.Count > 20000) throw new CacheException("PLAN_LIMIT", "The dependency graph exceeds this planner's supported size.");
                    var pair = queue.First(); queue.Remove(pair.Key); var tag = pair.Value;
                    if (!visited.Add(pair.Key)) continue;
                    current = tag.File + " / item " + tag.Item;
                    progress.Report(new("Reading campaign dependencies", visited.Count, 0, current, reused));
                    if (openPath != tag.File)
                    {
                        stream?.Dispose(); openPath = null;
                        var path = SafePaths.Child(source.Root, tag.File);
                        stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                        module = FileMetadata.Read(path, "Module", cancellation);
                        if (module.Digest != files[tag.File].Digest || module.FileLength != files[tag.File].Length) throw Changed();
                        openPath = tag.File;
                    }
                    try
                    {
                        var entry = module!.Entry(tag.Item);
                        if (entry.Group != tag.Identity.Group || entry.TagId != tag.Identity.TagId || entry.AssetId != tag.Identity.AssetId || entry.Checksum != tag.Checksum)
                            throw new CacheException("INDEX_MISMATCH", "The catalog does not match the source item. Index the dump again.");
                        var payload = ModulePayloadReader.Read(stream!, module, entry, cancellation);
                        var dependencies = store.Analyze(payload, out var hit); if (hit) reused++;
                        List<PlanResource> resources = [];
                        for (var i = 0; i < entry.ResourceCount; i++)
                        {
                            var r = module.Entry(module.Resource(entry.ResourceIndex + i));
                            // Resource data is not read here. A zero-size resource needs explicit
                            // platform resolution, even if its old block ranges happen to fit.
                            var state = r.StoredSize > 0 && r.LogicalSize > 0 ? "Unverified" : "Unresolved";
                            resources.Add(new(r.Index, state, r.StoredSize, r.LogicalSize));
                            if (state == "Unresolved") issues.Add(new("RESOURCE_UNRESOLVED", "A required resource has no declared local payload. Platform resolution is required.", tag.File, r.Index, tag.Identity));
                        }
                        tags.Add(new(tag.File, tag.Item, tag.Name, tag.Identity, tag.Checksum, rootTags.Contains(pair.Key), payload.Sha256,
                            payload.StoredBytesRead, payload.Bytes.Length, dependencies, resources.ToArray()));
                        foreach (var dependency in dependencies)
                        {
                            if (!requested.Add(dependency)) continue;
                            var all = snapshot.Candidates(dependency);
                            var scoped = all.Where(x => roots.Contains(x.File) || globalFiles.Contains(x.File)).ToList();
                            var candidates = scoped.Count > 0 ? scoped : all;
                            if (candidates.Count == 0)
                                issues.Add(new("DEPENDENCY_MISSING", "No stored tag matches this exact group, tag ID and asset ID. Forge availability and platform aliases are not assumed.", tag.File, tag.Item, dependency));
                            else
                            {
                                if (candidates.Select(x => x.Checksum).Distinct(StringComparer.Ordinal).Skip(1).Any())
                                    issues.Add(new("LAYER_SELECTION_PENDING", "Several checksum versions match this identity. All candidate versions are retained; runtime patch order is not selected.", Identity: dependency));
                                foreach (var candidate in candidates) Enqueue(candidate);
                            }
                        }
                    }
                    catch (CacheException e) when (e.Code is not ("INDEX_MISMATCH" or "DUMP_CHANGED"))
                    { issues.Add(new(e.Code, e.Message, tag.File, tag.Item, tag.Identity)); }
                    Space(cache.Root);
                }
            }
            finally { stream?.Dispose(); }
            progress.Report(new("Saving source build plan", visited.Count, visited.Count, Reused: reused));
            if (!inventory.SequenceEqual(SourceIndexer.Inventory(source.Root, cancellation)) || SourceDiscovery.Describe(source.Root) != source) throw Changed();
            var audio = snapshot.Files.Where(x => x.Kind == "Audio" && (CatalogSnapshot.LanguageOf(x.Path) == request.Language || CatalogSnapshot.LanguageOf(x.Path) == "SFX")).Select(x => x.Path).ToArray();
            var movies = snapshot.Files.Where(x => x.Kind == "Movie").Select(x => x.Path).ToArray();
            var plan = new SourceBuildPlan(1, Rules, snapshot.Fingerprint, bundle, request.Language, roots.ToArray(), snapshot.Files,
                tags.OrderBy(x => x.File, StringComparer.Ordinal).ThenBy(x => x.Item).ToArray(),
                issues.Distinct().OrderBy(x => x.Code, StringComparer.Ordinal).ThenBy(x => x.File, StringComparer.Ordinal).ThenBy(x => x.Item)
                    .ThenBy(x => x.Identity?.Group, StringComparer.Ordinal).ThenBy(x => x.Identity?.TagId, StringComparer.Ordinal).ThenBy(x => x.Identity?.AssetId, StringComparer.Ordinal).ToArray(),
                audio, movies,
                ["Acquire compatible Forge metadata and verify game access to the chosen cache.",
                 "Resolve missing identities, platform resources and effective patch order.",
                 "Resolve required audio banks and cinematic routes; listed packages/movies are candidates.",
                 "Convert supported assets, assemble modules and verify a playable generation."]);
            var summary = store.Publish(plan, reused, cancellation);
            CacheLog.TryWrite(cache.Root, $"Saved plan {summary.PlanId}: {summary.Tags} tags, {summary.DependencyReferences} references, {summary.Issues} issues; {reused} analyses reused. Conversion not built.");
            return new("Planned", summary, Message: "Source build plan saved. Conversion and Forge compatibility checks are still required.");

            void Enqueue(CatalogTag tag) { var key = Key(tag); if (!visited.Contains(key)) queue.TryAdd(key, tag); }
        }
        catch (OperationCanceledException)
        {
            if (activeCache is not null && ownsLease) CacheLog.TryWrite(activeCache.Root, "Dependency analysis paused; completed analyses kept.");
            return new("Paused", Message: "Dependency analysis paused. Completed analyses were kept; check dependencies again to resume.");
        }
        catch (Exception e)
        {
            if (activeCache is not null && ownsLease) CacheLog.TryWrite(activeCache.Root, $"Plan failed at {current ?? "setup"}: {e}");
            return new("Failed", Code: e is CacheException known ? known.Code : "PLAN_FAILED",
                Message: e is CacheException ? e.Message : "The source build plan could not be completed. Check the dump and cache, then retry or copy the details.",
                Details: $"File: {current ?? "(setup)"}\n{e}");
        }
    }
    private static FileStream Lock(string root)
    {
        try { return new(SafePaths.Child(root, "index.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new CacheException("CACHE_IN_USE", "Another worker is using this cache. Wait for it to finish or pause, then retry."); }
    }
    private static string Parent(string path) => path[..path.LastIndexOf('/')];
    private static string Key(CatalogTag tag) => tag.File + "|" + tag.Item.ToString("D10", System.Globalization.CultureInfo.InvariantCulture);
    private static CacheException Changed() => new("DUMP_CHANGED", "The dump changed after indexing. Reindex it before checking dependencies.");
    private static void Space(string root)
    {
        if (new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace < 256L * 1024 * 1024)
            throw new CacheException("CACHE_NEEDS_SPACE", "Free at least 256 MiB on the cache drive, then resume dependency analysis. Checkpoints were kept.");
    }
}
