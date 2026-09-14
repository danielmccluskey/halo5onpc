using System.Text.Json;
using System.Text.RegularExpressions;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Forge;
using H5SoloLauncher.Core.Planning;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Core.Preparation;

public sealed record ConversionRequest(InputRequest Inputs, string NativeManifestId, string SchemaId);
public sealed record EffectiveTag(string File, int Item, string Name, AssetReference Identity, string Checksum, bool Root,
    string Sha256, int Bytes, TagMetadata Metadata, int[] Resources);
public sealed record NativeDependency(AssetReference Requested, string? Name, string File, int Item, AssetReference NativeIdentity, string Kind);
public sealed record EffectiveDependency(AssetReference Requested, string? Name, string File, int Item, string Kind);
public sealed record UnresolvedDependency(AssetReference Identity, string? Name, string Reason);
public sealed record EffectivePlan(int Format, string Rules, string SourcePlanId, string InputFingerprint, string NativeManifestId, string SchemaId,
    string Language, string[] RootModules, EffectiveTag[] Tags, EffectiveDependency[] SourceDependencies, NativeDependency[] NativeDependencies, UnresolvedDependency[] Unresolved);
public sealed record EffectivePlanResult(string State, string? ManifestId = null, int Tags = 0, int NativeDependencies = 0, int SourcePlatformMatches = 0,
    UnresolvedDependency[]? Unresolved = null, string? Code = null, string? Message = null, string? Details = null);

public static class EffectivePlanBuilder
{
    public const string Rules = "campaign-effective-layers-1";
    private sealed record NativeTag(string File, ModuleEntry Entry);
    public static EffectivePlanResult Run(ConversionRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation)
    {
        var input = request.Inputs; string? current = null;
        try
        {
            var source = SourceDiscovery.Describe(input.SourceRoot); var cache = CacheFolders.Open(input.CacheRoot, source.Root, input.ForgeRoot);
            using var lease = new FileStream(SafePaths.Child(cache.Root, "index.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var catalog = new CatalogSnapshot(cache, source); var plan = BuildPlanStore.ReadVerified(cache, catalog, input.PlanId);
            var forge = ForgeReportStore.Read(cache, input.PackageFullName, input.PlanId) ?? throw new CacheException("FORGE_DATA_REQUIRED", "Prepare Forge data before resolving campaign layers.");
            var native = InputFiles.Read<NativeModuleManifest>(cache.Root, InputFiles.PathFor("native-manifests", request.NativeManifestId), 1024 * 1024, request.NativeManifestId);
            var schemas = InputFiles.Read<NativeSchemaSnapshot>(cache.Root, InputFiles.PathFor("native-schemas", request.SchemaId), 4 * 1024 * 1024, request.SchemaId);
            if (native.Format != 1 || native.Rules != NativeModuleCache.Rules || native.PackageFullName != input.PackageFullName || native.CatalogId != forge.CatalogId ||
                schemas.Format != 1 || schemas.Profile != ForgeSchemaRegistry.Profile || schemas.PackageFullName != input.PackageFullName) throw InputFiles.Damaged();
            var saved = InputCacheStore.ReadSummary(cache, input.PlanId, input.PackageFullName, forge.CatalogId) ?? throw new CacheException("INPUTS_REQUIRED", "Prepare source inputs before resolving campaign layers.");
            var inputManifest = InputCacheStore.ReadManifest(cache.Root, saved);
            var packs = inputManifest.Batches.Select(x => InputCacheStore.ReadBatch(cache.Root, x)).ToArray();
            var records = packs.SelectMany(b => b.Records.Select(r => (Batch: b, Record: r))).ToDictionary(x => x.Record.Sha256);
            var planned = plan.Tags.ToDictionary(x => (x.File, x.Item));
            progress.Report(new("Reading source identities", 0, 0));
            var all = catalog.AllTags(); var exact = all.ToLookup(x => x.Identity); var byId = all.ToLookup(x => (x.Identity.Group, x.Identity.TagId));
            List<NativeTag> nativeTags = [];
            foreach (var module in native.Modules)
            {
                cancellation.ThrowIfCancellationRequested(); current = module.Path;
                var metadata = FileMetadata.Read(SafePaths.Child(cache.Root, module.RelativePath), "Module", cancellation);
                if (metadata.FileLength != module.Length || metadata.Digest != module.TableDigest) throw InputFiles.Damaged();
                var tables = metadata.Tables;
                if (ModuleChecksum.Metadata(tables) != System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(tables.AsSpan(48)))
                    throw new CacheException("NATIVE_METADATA_CHECKSUM", "A native module's checksum does not match its tables.");
                for (var i = 0; i < metadata.ItemCount; i++)
                {
                    var entry = metadata.Entry(i);
                    if (entry.StoredSize > 0 && entry.TagId != "ffffffff") nativeTags.Add(new(module.Path, entry));
                }
            }
            var nativeById = nativeTags.ToLookup(x => (x.Entry.Group, x.Entry.TagId));
            var metadataCache = new Dictionary<string, FileMetadata>(StringComparer.Ordinal);
            FileMetadata Module(string path)
            {
                if (metadataCache.TryGetValue(path, out var found)) return found;
                var expected = catalog.Files.Single(x => x.Path == path);
                var loaded = FileMetadata.Read(SafePaths.Child(source.Root, path), "Module", cancellation);
                if (expected.Digest != loaded.Digest || expected.Length != loaded.FileLength) throw new CacheException("DUMP_CHANGED", "A source module changed after indexing. Reindex the dump and resume.");
                if (metadataCache.Count >= 16) metadataCache.Clear(); metadataCache.Add(path, loaded); return loaded;
            }
            Dictionary<(string File, int Item), EffectiveTag> selected = [];
            Dictionary<AssetReference, HashSet<string>> names = [];
            Dictionary<AssetReference, int> processedNames = [];
            Dictionary<AssetReference, EffectiveDependency> resolved = [];
            Dictionary<AssetReference, NativeDependency> available = [];
            Dictionary<AssetReference, UnresolvedDependency> missing = [];
            Queue<AssetReference> pending = [];
            var rootFiles = plan.RootModules.ToHashSet(StringComparer.Ordinal);
            void Add(CatalogTag tag, bool root)
            {
                if (selected.ContainsKey((tag.File, tag.Item))) return;
                cancellation.ThrowIfCancellationRequested(); current = tag.File + " / item " + tag.Item;
                var module = Module(tag.File); var entry = module.Entry(tag.Item);
                byte[] bytes; string hash;
                if (planned.TryGetValue((tag.File, tag.Item), out var existing) && records.TryGetValue(existing.PayloadSha256, out var record))
                { bytes = InputPack.Read(cache.Root, record.Batch, record.Record, cancellation); hash = existing.PayloadSha256; }
                else
                {
                    using var stream = File.OpenRead(SafePaths.Child(source.Root, tag.File));
                    var payload = ModulePayloadReader.Read(stream, module, entry, cancellation); bytes = payload.Bytes; hash = payload.Sha256;
                }
                var meta = TagMetadataReader.Read(bytes);
                selected.Add((tag.File, tag.Item), new(tag.File, tag.Item, tag.Name, tag.Identity, tag.Checksum, root, hash, bytes.Length, meta,
                    Enumerable.Range(entry.ResourceIndex, entry.ResourceCount).Select(module.Resource).ToArray()));
                foreach (var dep in meta.Dependencies)
                {
                    var name = dep.Name is null ? null : DependencyNames.Canonical(dep.Name);
                    if (!names.TryGetValue(dep.Identity, out var known)) names.Add(dep.Identity, known = new(StringComparer.Ordinal));
                    if (name is not null) known.Add(name);
                    pending.Enqueue(dep.Identity);
                }
                progress.Report(new("Resolving campaign layers", selected.Count, 0, current));
            }
            foreach (var root in all.Where(x => rootFiles.Contains(x.File)).OrderBy(x => x.File, StringComparer.Ordinal).ThenBy(x => x.Item)) Add(root, true);
            while (pending.TryDequeue(out var identity))
            {
                cancellation.ThrowIfCancellationRequested();
                var aliases = names[identity];
                if (processedNames.TryGetValue(identity, out var count) && count == aliases.Count) continue;
                processedNames[identity] = aliases.Count; resolved.Remove(identity); available.Remove(identity); missing.Remove(identity);
                var name = aliases.Count == 1 ? aliases.Single() : null;
                var nativeChoices = nativeById[(identity.Group, identity.TagId)].Where(x => x.Entry.AssetId == identity.AssetId ||
                    (name is not null && DependencyNames.IsGenerated(x.Entry.Name) && DependencyNames.Canonical(x.Entry.Name) == name)).ToArray();
                // Source roots remain present in every original layer, even when a native global shares their ID.
                var sourceChoices = exact[identity].ToArray();
                if (!sourceChoices.Any(x => rootFiles.Contains(x.File)) && nativeChoices.Length > 0)
                {
                    var chosen = nativeChoices.OrderBy(x => Rank(x.File, x.Entry.Index)).Last();
                    available.Add(identity, new(identity, name, chosen.File, chosen.Entry.Index,
                        new(chosen.Entry.Group, chosen.Entry.TagId, chosen.Entry.AssetId), chosen.Entry.AssetId == identity.AssetId ? "ExactIdentity" : "GeneratedPlatformIdentity"));
                    continue;
                }
                var kind = "ExactIdentity";
                if (sourceChoices.Length == 0 && name is not null)
                {
                    sourceChoices = byId[(identity.Group, identity.TagId)].Where(x => DependencyNames.IsGenerated(x.Name) && DependencyNames.Canonical(x.Name) == name).ToArray();
                    kind = "GeneratedPlatformIdentity";
                }
                if (sourceChoices.Length == 0)
                { missing.Add(identity, new(identity, name, aliases.Count > 1 ? "Conflicting dependency names prevent a generated platform match." : "No stored source or compatible native identity was found.")); continue; }
                var preferred = sourceChoices.Where(x => rootFiles.Contains(x.File) || ForgePaths.IsGlobal(x.File.Replace("/x1/", "/pc/", StringComparison.Ordinal))).ToArray();
                var selectedTag = (preferred.Length > 0 ? preferred : sourceChoices).OrderBy(x => Rank(x.File, x.Item)).Last();
                resolved.Add(identity, new(identity, name, selectedTag.File, selectedTag.Item, kind)); Add(selectedTag, rootFiles.Contains(selectedTag.File));
            }
            var effective = new EffectivePlan(1, Rules, input.PlanId, plan.InputFingerprint, request.NativeManifestId, request.SchemaId, plan.Language,
                plan.RootModules, selected.Values.OrderBy(x => x.File, StringComparer.Ordinal).ThenBy(x => x.Item).ToArray(),
                resolved.Values.OrderBy(x => x.Requested.Group, StringComparer.Ordinal).ThenBy(x => x.Requested.TagId, StringComparer.Ordinal).ThenBy(x => x.Requested.AssetId, StringComparer.Ordinal).ToArray(),
                available.Values.OrderBy(x => x.Requested.Group, StringComparer.Ordinal).ThenBy(x => x.Requested.TagId, StringComparer.Ordinal).ThenBy(x => x.Requested.AssetId, StringComparer.Ordinal).ToArray(),
                missing.Values.OrderBy(x => x.Identity.Group, StringComparer.Ordinal).ThenBy(x => x.Identity.TagId, StringComparer.Ordinal).ThenBy(x => x.Identity.AssetId, StringComparer.Ordinal).ToArray());
            cancellation.ThrowIfCancellationRequested(); var id = InputFiles.Save(cache.Root, "effective-plans", effective);
            CacheLog.Write(cache.Root, $"Effective source plan {id}: {selected.Count} physical tags, {available.Count} native dependencies, {missing.Count} unresolved. Conversion and runtime validation pending.");
            return new("Planned", id, selected.Count, available.Count, resolved.Values.Count(x => x.Kind == "GeneratedPlatformIdentity"), effective.Unresolved,
                Message: "Effective campaign layers saved. Platform identities still require native loader validation; no asset IDs were rewritten.");
        }
        catch (OperationCanceledException) { return new("Paused", Message: "Layer selection paused. Completed source inputs were kept."); }
        catch (Exception e) { return new("Failed", Code: e is CacheException known ? known.Code : "EFFECTIVE_PLAN_FAILED", Message: e.Message, Details: $"Tag: {current}\n{e}"); }
    }
    public static (int Patch, bool Platform, int Item, string Path) Rank(string path, int item)
    {
        var match = Regex.Match(Path.GetFileName(path), @"-rtx-(\d+)-", RegexOptions.CultureInvariant);
        var patch = match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        return (patch, path.Contains("/x1/", StringComparison.Ordinal) || path.Contains("/pc/", StringComparison.Ordinal), item, path);
    }
}
