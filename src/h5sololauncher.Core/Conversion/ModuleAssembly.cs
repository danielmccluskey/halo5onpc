using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Forge;
using H5SoloLauncher.Core.Planning;
using H5SoloLauncher.Core.Preparation;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Core.Conversion;

public sealed record ModuleAssemblyRequest(ShaderPreparationRequest Shaders, string ShaderManifestId);
public sealed record AssembledModule(string OriginalPath, string RelativePath, string InputId, ModuleWriteResult Verified, string[] Scenarios, bool SharedBanks = false);
public sealed record AssembledCampaign(int Format, string Rules, string EffectivePlanId, string AssetsId, string ShadersId, string PackageFullName, AssembledModule[] Modules, UnresolvedDependency[] Unresolved);
public sealed record ModuleAssemblyResult(string State, string? ManifestId = null, int Modules = 0, int Reused = 0, long Bytes = 0, string? Code = null, string? Message = null, string? Details = null);

public static class ModuleAssembly
{
    public const string Rules = "campaign-module-assembly-1";
    public static ModuleAssemblyResult Run(ModuleAssemblyRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation)
    {
        var count = 0; var reused = 0; long total = 0; string? current = null;
        try
        {
            var input = request.Shaders.Conversion.Inputs; var source = SourceDiscovery.Describe(input.SourceRoot); var cache = CacheFolders.Open(input.CacheRoot, source.Root, input.ForgeRoot);
            using var lease = new FileStream(SafePaths.Child(cache.Root, "index.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var catalog = new CatalogSnapshot(cache, source); var sourcePlan = BuildPlanStore.ReadVerified(cache, catalog, input.PlanId);
            var planId = request.Shaders.Conversion.EffectivePlanId;
            var plan = InputFiles.Read<EffectivePlan>(cache.Root, InputFiles.PathFor("effective-plans", planId), 128L * 1024 * 1024, planId);
            var assets = InputFiles.Read<ConvertedAssetManifest>(cache.Root, InputFiles.PathFor("asset-manifests", request.Shaders.AssetsId), 8 * 1024 * 1024, request.Shaders.AssetsId);
            var shaders = InputFiles.Read<ShaderManifest>(cache.Root, InputFiles.PathFor("shader-manifests", request.ShaderManifestId), 32 * 1024 * 1024, request.ShaderManifestId);
            if (plan.Rules != EffectivePlanBuilder.Rules || plan.SourcePlanId != input.PlanId || plan.InputFingerprint != catalog.Fingerprint ||
                assets.Format != 1 || assets.Rules != ConvertedAssets.Rules || assets.EffectivePlanId != planId ||
                shaders.Format != 1 || shaders.Rules != ShaderPreparation.Rules || shaders.EffectivePlanId != planId || shaders.AssetsId != request.Shaders.AssetsId || shaders.NativeModulesId != plan.NativeManifestId) throw InputFiles.Damaged();
            var native = InputFiles.Read<NativeModuleManifest>(cache.Root, InputFiles.PathFor("native-manifests", plan.NativeManifestId), 1024 * 1024, plan.NativeManifestId);
            if (native.PackageFullName != input.PackageFullName || native.Rules != NativeModuleCache.Rules) throw InputFiles.Damaged();
            var batches = assets.BatchIds.Select(id => InputFiles.Read<ConvertedAssetBatch>(cache.Root, InputFiles.PathFor("asset-batches", id), 32 * 1024 * 1024, id)).ToDictionary(x => x.SourceFile);
            var converted = batches.Values.SelectMany(b => b.Assets.Select(a => (Batch: b, Asset: a))).ToDictionary(x => (x.Batch.SourceFile, x.Asset.Item));
            var shaderTags = shaders.Definitions.ToDictionary(x => (x.File, x.Item)); var tags = plan.Tags.ToDictionary(x => (x.File, x.Item));
            var resolutions = plan.SourceDependencies.ToDictionary(x => x.Requested); var files = catalog.Files.ToDictionary(x => x.Path);
            var scopes = sourcePlan.Bundle.Scenarios.ToDictionary(s => s, s => plan.RootModules.Where(f => Belongs(f, s)).ToHashSet(StringComparer.Ordinal));
            var extras = scopes.ToDictionary(x => x.Key, x => Extras(plan, x.Value, tags, resolutions));
            List<AssembledModule> outputs = [];

            ModuleWritePayload? Asset(string file, ConvertedAsset asset)
            {
                if (asset.Length == 0) return null;
                var bytes = ConvertedAssets.Read(cache.Root, batches[file], asset, cancellation);
                if (shaderTags.TryGetValue((file, asset.Item), out var shader))
                {
                    if (asset.Sha256 != shader.SourceSha256) throw InputFiles.Damaged();
                    bytes = ShaderPreparation.ReadPayload(cache.Root, shader.OutputSha256);
                }
                return new(bytes);
            }
            AssembledModule Publish(string original, string[] scenarios, ModuleWriteLayout layout, bool shared = false)
            {
                var key = InputFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(new { Rules, Plan = planId, Assets = request.Shaders.AssetsId, Shaders = request.ShaderManifestId, Original = original,
                    Header = layout.Header, Entries = layout.Entries.Select(x => x.Raw).ToArray(), layout.Strings, layout.Resources, layout.ResourceBoundary }));
                var relative = "game/modules/" + key.ToLowerInvariant() + "/" + Path.GetFileName(original); var destination = SafePaths.Child(cache.Root, relative);
                var checkpoint = InputFiles.PathFor("module-checkpoints", key);
                if (File.Exists(destination) && File.Exists(SafePaths.Child(cache.Root, checkpoint)))
                {
                    try
                    {
                        var old = InputFiles.Read<AssembledModule>(cache.Root, checkpoint, 32768);
                        using var stream = File.OpenRead(destination);
                        if (old.InputId == key && old.OriginalPath == original && old.RelativePath == relative && old.Verified.Bytes == stream.Length &&
                            old.SharedBanks == shared && old.Scenarios.SequenceEqual(scenarios) && InputFiles.Digest(stream, cancellation) == old.Verified.Sha256)
                        { reused++; return old; }
                    }
                    catch (Exception e) when (e is IOException or CacheException or JsonException) { }
                }
                var written = ModuleWriter.Write(destination, layout, cancellation); var result = new AssembledModule(original, relative, key, written, scenarios, shared);
                InputFiles.Write(cache.Root, checkpoint, JsonSerializer.SerializeToUtf8Bytes(result)); return result;
            }

            foreach (var file in plan.RootModules.Order(StringComparer.Ordinal))
            {
                cancellation.ThrowIfCancellationRequested(); current = file;
                progress.Report(new("Writing verified campaign modules", count, plan.RootModules.Length + 1, file, reused));
                var path = SafePaths.Child(source.Root, file); var metadata = FileMetadata.Read(path, "Module", cancellation);
                if (metadata.Digest != files[file].Digest || metadata.FileLength != files[file].Length) throw new CacheException("DUMP_CHANGED", "A mission module changed after indexing.");
                var scenarios = scopes.Where(x => x.Value.Contains(file)).Select(x => x.Key).ToArray(); if (scenarios.Length != 1) throw Invalid("A mission module has an ambiguous campaign scope.");
                var platform = file.Contains("/x1/", StringComparison.Ordinal) ? "pc" : "any";
                var append = Path.GetFileName(file) == Path.GetFileName(scenarios[0]) + "-rtx-1.module" ? extras[scenarios[0]].Where(x => Platform(x.File) == platform).ToArray() : [];
                var originalEntries = Enumerable.Range(0, metadata.ItemCount).Select(metadata.Entry).ToArray();
                var boundary = checked((int)U(metadata.Header, 24)); var shift = append.Length;
                if (boundary > metadata.ItemCount) throw Invalid("The source resource boundary is invalid.");
                int Translate(int index) => index >= boundary ? checked(index + shift) : index;
                Dictionary<(string File, int Item), ConvertedAsset> additions = [];
                foreach (var tag in append)
                {
                    Queue<int> pending = new([tag.Item]);
                    while (pending.TryDequeue(out var index))
                    {
                        var key = (tag.File, index); if (additions.ContainsKey(key)) throw Invalid("Appended dependencies share a resource unexpectedly.");
                        if (!converted.TryGetValue(key, out var value)) throw Invalid("A required dependency has no converted resource payload.");
                        additions.Add(key, value.Asset); foreach (var child in value.Asset.Resources) pending.Enqueue(child);
                    }
                }
                var roots = additions.Where(x => I(x.Value.Entry, 4) == -1).ToArray(); var resources = additions.Where(x => I(x.Value.Entry, 4) != -1).ToArray();
                if (roots.Length != shift) throw Invalid("Dependency root counts changed during module assembly.");
                var mapping = roots.Select((x, i) => (x.Key, Index: boundary + i)).Concat(resources.Select((x, i) => (x.Key, Index: metadata.ItemCount + shift + i))).ToDictionary(x => x.Key, x => x.Index);
                var rows = originalEntries.Take(boundary).Select(x => (byte[])x.Raw.Clone()).Concat(roots.Select(x => (byte[])x.Value.Entry.Clone()))
                    .Concat(originalEntries.Skip(boundary).Select(x => (byte[])x.Raw.Clone())).Concat(resources.Select(x => (byte[])x.Value.Entry.Clone())).ToArray();
                var resourceTable = Enumerable.Range(0, metadata.ResourceCount).Select(i => Translate(metadata.Resource(i))).ToList();
                using var strings = new MemoryStream(); var oldTables = metadata.Tables;
                strings.Write(oldTables.AsSpan(metadata.HeaderSize + metadata.ItemCount * 88, checked((int)U(metadata.Header, 28))));
                Dictionary<int, Func<ModuleWritePayload?>> work = [];
                using var original = File.OpenRead(path);
                foreach (var entry in originalEntries)
                {
                    var index = Translate(entry.Index); if (entry.Parent >= 0) WI(rows[index], 4, Translate(entry.Parent));
                    if (converted.TryGetValue((file, entry.Index), out var asset))
                    {
                        if (!asset.Asset.Entry.AsSpan().SequenceEqual(entry.Raw)) throw InputFiles.Damaged();
                        work.Add(index, () => Asset(file, asset.Asset));
                    }
                    else if (entry.StoredSize > 0)
                    {
                        if (entry.Parent != -1 || entry.TagId != "ffffffff" || entry.Name != "loadmanifest" || entry.Flags != 5 || entry.BlockCount != 0) throw Invalid("A stored mission entry was omitted from conversion.");
                        work.Add(index, () =>
                        {
                            var logical = ModulePayloadReader.Read(original, metadata, entry, cancellation).Bytes;
                            if (entry.StoredSize > ModulePayloadReader.MaximumTagBytes) throw Invalid("The load manifest exceeds its size limit.");
                            original.Position = metadata.TableBytes + entry.DataOffset; var stored = new byte[(int)entry.StoredSize]; original.ReadExactly(stored); return new(logical, stored);
                        });
                    }
                }
                foreach (var added in roots.Concat(resources))
                {
                    var entry = rows[mapping[added.Key]]; var priorParent = I(added.Value.Entry, 4);
                    WI(entry, 0, checked((int)strings.Position)); WI(entry, 4, priorParent == -1 ? -1 : mapping[(added.Key.File, priorParent)]);
                    WI(entry, 8, added.Value.Resources.Length); WI(entry, 12, resourceTable.Count);
                    strings.Write(Encoding.UTF8.GetBytes(added.Value.Name)); strings.WriteByte(0);
                    resourceTable.AddRange(added.Value.Resources.Select(x => mapping[(added.Key.File, x)]));
                    work.Add(mapping[added.Key], () => Asset(added.Key.File, added.Value));
                }
                var layout = new ModuleWriteLayout(metadata.Header, rows.Select((raw, i) => new ModuleWriteEntry(raw, work.GetValueOrDefault(i) ?? (() => null))).ToArray(), strings.ToArray(), resourceTable.ToArray(), boundary + shift);
                var output = Publish(file.Replace("/x1/", "/pc/", StringComparison.Ordinal), scenarios, layout);
                outputs.Add(output); count++; total += output.Verified.Bytes;
            }
            current = "Shared campaign shaders"; progress.Report(new("Writing shared campaign shaders", count, plan.RootModules.Length + 1, current, reused));
            var bankCount = shaders.Banks.Length; List<ModuleWriteEntry> bankEntries = []; using var bankStrings = new MemoryStream();
            var nativeTables = native.Modules.ToDictionary(x => x.Path, x => FileMetadata.Read(SafePaths.Child(cache.Root, x.RelativePath), "Module", cancellation));
            foreach (var resource in new[] { false, true })
            for (var i = 0; i < bankCount; i++)
            {
                var bank = shaders.Banks[i]; var table = nativeTables[bank.NativeModule]; var control = table.Entry(resource ? bank.NativeResource : bank.NativeTag); var raw = (byte[])control.Raw.Clone();
                WI(raw, 0, checked((int)bankStrings.Position)); WI(raw, 4, resource ? i : -1); WI(raw, 8, resource ? 0 : 1); WI(raw, 12, resource ? bankCount : i);
                bankStrings.Write(Encoding.UTF8.GetBytes(control.Name)); bankStrings.WriteByte(0); var hash = resource ? bank.ResourceSha256 : bank.TagSha256;
                bankEntries.Add(new(raw, () => new(ShaderPreparation.ReadPayload(cache.Root, hash), RehashAsset: true)));
            }
            var header = new byte[56]; "mohd"u8.CopyTo(header); BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), BinaryPrimitives.ReadUInt64LittleEndian(Convert.FromHexString(request.ShaderManifestId)));
            var patch = native.Modules.Where(x => x.Path.Contains("/pc/", StringComparison.Ordinal)).Select(x => EffectivePlanBuilder.Rank(x.Path, 0).Patch).Max() + 1;
            var sharedPath = $"deploy/pc/levels/globals-rtx-{patch}-1.module";
            if (native.Modules.Any(x => x.Path == sharedPath)) throw Invalid("The shared shader layer collides with an installed module.");
            var shared = Publish(sharedPath, sourcePlan.Bundle.Scenarios, new(header, bankEntries.ToArray(), bankStrings.ToArray(), Enumerable.Range(bankCount, bankCount).ToArray(), bankCount), shared: true);
            outputs.Add(shared); count++; total += shared.Verified.Bytes;
            var manifest = new AssembledCampaign(1, Rules, planId, request.Shaders.AssetsId, request.ShaderManifestId, input.PackageFullName, outputs.ToArray(), plan.Unresolved);
            cancellation.ThrowIfCancellationRequested(); var id = InputFiles.Save(cache.Root, "game-manifests", manifest);
            CacheLog.Write(cache.Root, $"Assembled game modules {id}: {count} modules, {total} bytes, every payload and native integrity block verified. Audio and runtime preparation remain required.");
            return new("Assembled", id, count, reused, total, Message: "Game modules built and verified. Audio and runtime preparation are still required.");
        }
        catch (OperationCanceledException) { return new("Paused", Modules: count, Reused: reused, Bytes: total, Message: "Module assembly paused. Completed modules were kept."); }
        catch (Exception e) { return new("Failed", Modules: count, Reused: reused, Bytes: total, Code: e is CacheException known ? known.Code : "MODULE_ASSEMBLY_FAILED", Message: e.Message, Details: current + Environment.NewLine + e); }
    }
    private static EffectiveTag[] Extras(EffectivePlan plan, HashSet<string> files, Dictionary<(string File, int Item), EffectiveTag> tags, Dictionary<AssetReference, EffectiveDependency> resolutions)
    {
        var roots = plan.Tags.Where(x => files.Contains(x.File)).ToArray(); var existing = roots.Select(x => (x.Identity.Group, x.Identity.TagId)).ToHashSet();
        Queue<EffectiveTag> pending = new(roots); HashSet<(string File, int Item)> seen = []; List<EffectiveTag> extras = [];
        while (pending.TryDequeue(out var tag))
        {
            if (!seen.Add((tag.File, tag.Item))) continue;
            if (!existing.Contains((tag.Identity.Group, tag.Identity.TagId))) extras.Add(tag);
            foreach (var dependency in tag.Metadata.Dependencies)
                if (resolutions.TryGetValue(dependency.Identity, out var found)) pending.Enqueue(tags[(found.File, found.Item)]);
        }
        return extras.GroupBy(x => (Platform(x.File), x.Identity.Group, x.Identity.TagId)).Select(group =>
        {
            if (group.Select(x => x.Identity.AssetId).Distinct().Count() > 1) throw Invalid("Two appended dependencies use conflicting asset identities for the same tag ID.");
            return group.OrderBy(x => EffectivePlanBuilder.Rank(x.File, x.Item)).Last();
        }).OrderBy(x => x.Identity.Group, StringComparer.Ordinal).ThenBy(x => x.Identity.TagId, StringComparer.Ordinal).ToArray();
    }
    private static bool Belongs(string file, string scenario) => file.StartsWith("deploy/any/" + scenario + "-rtx-", StringComparison.Ordinal) || file.StartsWith("deploy/x1/" + scenario + "-rtx-", StringComparison.Ordinal);
    private static string Platform(string file) => file.Contains("/x1/", StringComparison.Ordinal) ? "pc" : "any";
    private static uint U(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
    private static int I(byte[] b, int at) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at));
    private static void WI(byte[] b, int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at), value);
    private static CacheException Invalid(string message) => new("MODULE_ASSEMBLY_UNSUPPORTED", message);
}
