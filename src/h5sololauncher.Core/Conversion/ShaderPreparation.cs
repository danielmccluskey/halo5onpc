using System.Buffers.Binary;
using System.Text.Json;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Forge;
using H5SoloLauncher.Core.Planning;
using H5SoloLauncher.Core.Preparation;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Core.Conversion;

public sealed record ShaderPreparationRequest(ConversionAuditRequest Conversion, string AssetsId);
public sealed record ShaderDefinitionOutput(string File, int Item, string Bank, string SourceSha256, string OutputSha256, int Fields);
public sealed record ShaderBankOutput(string Id, string NativeModule, int NativeTag, int NativeResource, string TagSha256, string ResourceSha256, int[] Counts, int Added);
public sealed record ShaderProgramProof(string Bank, uint Key, int Stage, string SourceSha256, string OutputSha256, uint Version, ShaderBinding[] Bindings, ShaderChange[] Changes,
    string PcInstructionIdentity, string[] EquivalentSourceSha256);
public sealed record ShaderManifest(int Format, string Rules, string EffectivePlanId, string AssetsId, string NativeModulesId, ShaderDefinitionOutput[] Definitions, ShaderBankOutput[] Banks, ShaderProgramProof[] Programs);
public sealed record ShaderPreparationResult(string State, string? ManifestId = null, int Definitions = 0, int Banks = 0, int Programs = 0, string? Code = null, string? Message = null, string? Details = null);

public static class ShaderPreparation
{
    public const string Rules = "campaign-shaders-1";
    private sealed record NativeBank(CachedNativeModule Module, ModuleEntry Tag, ModuleEntry Resource, byte[] TagBytes, TagDocument Document);
    private sealed record Definition(EffectiveTag Tag, TagDocument Document, string Bank, ShaderField[] Fields, uint?[] NativeKeys);
    public static ShaderPreparationResult Run(ShaderPreparationRequest request, Func<IShaderValidator> validatorFactory, IProgress<IndexProgress> progress, CancellationToken cancellation)
    {
        string? current = null;
        try
        {
            var input = request.Conversion.Inputs; var source = SourceDiscovery.Describe(input.SourceRoot); var cache = CacheFolders.Open(input.CacheRoot, source.Root, input.ForgeRoot);
            using var lease = new FileStream(SafePaths.Child(cache.Root, "index.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var catalog = new CatalogSnapshot(cache, source); BuildPlanStore.ReadVerified(cache, catalog, input.PlanId);
            var plan = InputFiles.Read<EffectivePlan>(cache.Root, InputFiles.PathFor("effective-plans", request.Conversion.EffectivePlanId), 128L * 1024 * 1024, request.Conversion.EffectivePlanId);
            var assets = InputFiles.Read<ConvertedAssetManifest>(cache.Root, InputFiles.PathFor("asset-manifests", request.AssetsId), 8 * 1024 * 1024, request.AssetsId);
            if (plan.SourcePlanId != input.PlanId || plan.InputFingerprint != catalog.Fingerprint || plan.Rules != EffectivePlanBuilder.Rules ||
                assets.EffectivePlanId != request.Conversion.EffectivePlanId || assets.Rules != ConvertedAssets.Rules || assets.Format != 1) throw InputFiles.Damaged();
            var native = InputFiles.Read<NativeModuleManifest>(cache.Root, InputFiles.PathFor("native-manifests", plan.NativeManifestId), 1024 * 1024, plan.NativeManifestId);
            if (native.Format != 1 || native.Rules != NativeModuleCache.Rules || native.PackageFullName != input.PackageFullName) throw InputFiles.Damaged();
            Dictionary<(string Bank, int Stage, uint High), HashSet<uint>> mappings = []; Dictionary<string, NativeBank> banks = [];
            var sourceTags = plan.Tags.Where(x => x.Identity.Group == "mats").ToArray();
            var batches = assets.BatchIds.Select(id => InputFiles.Read<ConvertedAssetBatch>(cache.Root, InputFiles.PathFor("asset-batches", id), 32 * 1024 * 1024, id)).ToDictionary(x => x.SourceFile);
            var sourceDocuments = sourceTags.ToDictionary(x => (x.File, x.Item), x => new TagDocument(ConvertedAssets.Read(cache.Root, batches[x.File], batches[x.File].Assets.Single(a => a.Item == x.Item), cancellation)));
            foreach (var module in native.Modules.OrderBy(x => EffectivePlanBuilder.Rank(x.Path, 0)))
            {
                current = module.Path; progress.Report(new("Reading native shader controls", 0, 0, current));
                var path = SafePaths.Child(cache.Root, module.RelativePath); var metadata = FileMetadata.Read(path, "Module", cancellation);
                if (metadata.Digest != module.TableDigest || metadata.FileLength != module.Length) throw InputFiles.Damaged();
                using var stream = File.OpenRead(path); if (InputFiles.Digest(stream, cancellation) != module.Sha256) throw InputFiles.Damaged();
                foreach (var entry in Enumerable.Range(0, metadata.ItemCount).Select(metadata.Entry).Where(x => x.StoredSize > 0 && x.Group is "mats" or "mtsb").OrderBy(x => x.DataOffset))
                {
                    cancellation.ThrowIfCancellationRequested(); var payload = ModulePayloadReader.Read(stream, metadata, entry, cancellation);
                    var document = new TagDocument(payload.Bytes);
                    if (entry.Group == "mats")
                    {
                        var bank = ShaderBank.BankId(document);
                        foreach (var field in ShaderBank.Fields(document))
                        {
                            var key = (bank, field.Stage, (uint)(field.Key >> 32));
                            if (!mappings.TryGetValue(key, out var values)) mappings.Add(key, values = []); values.Add((uint)field.Key);
                        }
                    }
                    else
                    {
                        if (entry.ResourceCount != 1) throw Invalid("A native shader bank has an unsupported resource graph.");
                        var resource = metadata.Entry(metadata.Resource(entry.ResourceIndex));
                        if (resource.Parent != entry.Index || resource.ResourceCount != 0) throw Invalid("A native shader bank resource has unexpected children.");
                        var resourceBytes = ModulePayloadReader.ReadResource(stream, metadata, resource, cancellation).Bytes;
                        var bank = new NativeBank(module, entry, resource, payload.Bytes, new(resourceBytes, resource: true));
                        _ = ShaderBank.Merge(bank.Document, []); banks[entry.TagId] = bank;
                    }
                }
            }
            var known = banks.ToDictionary(x => x.Key, x => ShaderBank.Stages(x.Value.Document).Select(s => s.Keys.ToHashSet()).ToArray());
            List<Definition> definitions = [];
            foreach (var tag in sourceTags)
            {
                var document = sourceDocuments[(tag.File, tag.Item)]; var bank = ShaderBank.BankId(document);
                if (!known.TryGetValue(bank, out var stageKeys)) throw Invalid("The installed Forge doesn't contain required native bank " + bank + ".");
                var fields = ShaderBank.Fields(document); var nativeKeys = new uint?[fields.Length];
                for (var i = 0; i < fields.Length; i++)
                {
                    var field = fields[i]; var high = (uint)(field.Key >> 32); mappings.TryGetValue((bank, field.Stage, high), out var candidates);
                    if (candidates?.Count > 1) throw Invalid("Native shader controls disagree on a platform key.");
                    var key = candidates?.Count == 1 ? candidates.Single() : high == uint.MaxValue || stageKeys[field.Stage].Contains(high) ? high : (uint?)null;
                    if (key is not null && key != uint.MaxValue && !stageKeys[field.Stage].Contains(key.Value)) throw Invalid("A mapped native shader key is absent from its stage.");
                    nativeKeys[i] = key;
                }
                definitions.Add(new(tag, document, bank, fields, nativeKeys));
            }
            using var validator = validatorFactory(); var files = catalog.Files.ToDictionary(x => x.Path);
            var bankSources = catalog.AllTags().Where(x => x.Identity.Group == "mtsb").ToLookup(x => x.Identity.TagId);
            Dictionary<(string Bank, uint Key), (ShaderBankProgram Program, ShaderProgramProof Proof)> programs = [];
            foreach (var bankDefinitions in definitions.GroupBy(x => x.Bank))
            {
                cancellation.ThrowIfCancellationRequested(); var bankId = bankDefinitions.Key;
                var wanted = bankDefinitions.SelectMany(d => d.Fields.Where((_, i) => d.NativeKeys[i] is null)).Select(x => (uint)(x.Key >> 32)).ToHashSet();
                if (wanted.Count == 0) continue;
                // Banks in an exported dump can be trimmed per layer. Resolve required keys across the
                // original shared bank layers and reject conflicting bytecode under the same bank/key.
                var candidates = bankSources[bankId].Where(x => x.File.StartsWith("deploy/x1/levels/globals-rtx-", StringComparison.Ordinal) ||
                    x.File.StartsWith("deploy/x1/globals/all_shaders-rtx-", StringComparison.Ordinal)).OrderByDescending(x => EffectivePlanBuilder.Rank(x.File, x.Item)).ToArray();
                Dictionary<uint, ShaderBankProgram> found = []; Dictionary<uint, HashSet<string>> variants = []; HashSet<string> resourceChecksums = [];
                foreach (var candidate in candidates)
                {
                    current = candidate.File + " / bank " + bankId; progress.Report(new("Resolving source shader programs", found.Count, wanted.Count, current));
                    var path = SafePaths.Child(source.Root, candidate.File); var metadata = FileMetadata.Read(path, "Module", cancellation);
                    if (!files.TryGetValue(candidate.File, out var expected) || expected.Digest != metadata.Digest || expected.Length != metadata.FileLength) throw new CacheException("DUMP_CHANGED", "The source shader bank changed after indexing.");
                    var entry = metadata.Entry(candidate.Item); if (entry.ResourceCount != 1) throw Invalid("A source bank has an unsupported resource graph.");
                    var resource = metadata.Entry(metadata.Resource(entry.ResourceIndex)); if (resource.Parent != entry.Index) throw Invalid("Source shader bank resource ownership is invalid.");
                    if (resource.StoredSize == 0) continue;
                    using var stream = File.OpenRead(path); var content = ModulePayloadReader.ReadResource(stream, metadata, resource, cancellation);
                    if (!resourceChecksums.Add(content.Sha256)) continue;
                    var bank = new TagDocument(content.Bytes, resource: true);
                    var present = ShaderBank.Stages(bank).SelectMany(x => x.Keys).Where(wanted.Contains).ToHashSet();
                    foreach (var selected in ShaderBank.Select(bank, present))
                    {
                        validator.VerifyHash(selected.Code);
                        if (!variants.TryGetValue(selected.Key, out var sourceVariants)) variants.Add(selected.Key, sourceVariants = []);
                        sourceVariants.Add(InputFiles.Hash(selected.Code));
                        if (found.TryGetValue(selected.Key, out var prior))
                        {
                            if (prior.Stage != selected.Stage || ShaderProgram.PcInstructionIdentity(prior.Code) != ShaderProgram.PcInstructionIdentity(selected.Code))
                            {
                                var firstHash = StorePayload(cache.Root, prior.Code); var secondHash = StorePayload(cache.Root, selected.Code);
                                var firstChunks = ShaderProgram.Chunks(prior.Code).Select(x => x.Kind + "=" + InputFiles.Hash(x.Bytes));
                                var secondChunks = ShaderProgram.Chunks(selected.Code).Select(x => x.Kind + "=" + InputFiles.Hash(x.Bytes));
                                throw Invalid($"Source bank {bankId} key {selected.Key:x8} has conflicting programs {firstHash} and {secondHash}. " + string.Join("; ", firstChunks) + " / " + string.Join("; ", secondChunks));
                            }
                        }
                        else found.Add(selected.Key, selected);
                    }
                }
                if (!found.Keys.ToHashSet().SetEquals(wanted)) throw Invalid("The dump's shared shader banks are missing required keys: " + string.Join(", ", wanted.Except(found.Keys).Select(x => x.ToString("x8"))));
                foreach (var selected in found.Values.OrderBy(x => x.Key))
                {
                    var key = (bankId, selected.Key); var sourceHash = InputFiles.Hash(selected.Code);
                    progress.Report(new("Converting and checking shader programs", programs.Count, 0, current));
                    validator.VerifyHash(selected.Code); var translated = ShaderProgram.Translate(selected.Code); var code = validator.FinalizeAndValidate(translated);
                    var row = (byte[])selected.Row.Clone(); BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(24), (uint)code.Length);
                    var hash = StorePayload(cache.Root, code); var proof = new ShaderProgramProof(bankId, selected.Key, selected.Stage, sourceHash, hash, translated.Version, translated.Bindings, translated.Changes,
                        ShaderProgram.PcInstructionIdentity(selected.Code), variants[selected.Key].Order(StringComparer.Ordinal).ToArray());
                    programs.Add(key, (selected with { Row = row, Code = code }, proof));
                }
            }
            List<ShaderBankOutput> outputs = [];
            var requiredBanks = definitions.Select(x => x.Bank).ToHashSet();
            foreach (var pair in banks.Where(x => requiredBanks.Contains(x.Key)).OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                cancellation.ThrowIfCancellationRequested(); current = pair.Key; progress.Report(new("Assembling shared shader banks", outputs.Count, banks.Count, current));
                var nativeBank = pair.Value; var added = programs.Where(x => x.Key.Bank == pair.Key).Select(x => x.Value.Program).ToArray();
                var merged = ShaderBank.Merge(nativeBank.Document, added); var tag = (byte[])nativeBank.TagBytes.Clone(); var tagDocument = new TagDocument(tag); tagDocument.Field(0, 40, 12);
                for (var i = 0; i < 6; i++) BinaryPrimitives.WriteUInt16LittleEndian(tag.AsSpan(tagDocument.BlockOffset(0) + 40 + i * 2), checked((ushort)merged.Counts[i]));
                outputs.Add(new(pair.Key, nativeBank.Module.Path, nativeBank.Tag.Index, nativeBank.Resource.Index, StorePayload(cache.Root, tag), StorePayload(cache.Root, merged.Resource), merged.Counts, added.Length));
                known[pair.Key] = ShaderBank.Stages(new(merged.Resource, resource: true)).Select(x => x.Keys.ToHashSet()).ToArray();
            }
            List<ShaderDefinitionOutput> converted = [];
            foreach (var definition in definitions)
            {
                var bytes = (byte[])definition.Document.Bytes.Clone();
                for (var i = 0; i < definition.Fields.Length; i++)
                {
                    var field = definition.Fields[i]; var key = definition.NativeKeys[i] ?? (uint)(field.Key >> 32);
                    if (key != uint.MaxValue && !known[definition.Bank][field.Stage].Contains(key)) throw Invalid("A converted shader definition refers to a missing bank program.");
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(field.Offset), key);
                }
                converted.Add(new(definition.Tag.File, definition.Tag.Item, definition.Bank, InputFiles.Hash(definition.Document.Bytes), StorePayload(cache.Root, bytes), definition.Fields.Length));
            }
            var manifest = new ShaderManifest(1, Rules, request.Conversion.EffectivePlanId, request.AssetsId, plan.NativeManifestId, converted.ToArray(), outputs.ToArray(), programs.Values.Select(x => x.Proof).OrderBy(x => x.Bank, StringComparer.Ordinal).ThenBy(x => x.Key).ToArray());
            cancellation.ThrowIfCancellationRequested(); var id = InputFiles.Save(cache.Root, "shader-manifests", manifest);
            CacheLog.Write(cache.Root, $"Shader preparation {id}: {converted.Count} definitions, {outputs.Count} native banks, {programs.Count} programs reflected, disassembled and accepted by D3D11 creation.");
            return new("Prepared", id, converted.Count, outputs.Count, programs.Count, Message: "Shader programs converted and checked with the graphics driver.");
        }
        catch (OperationCanceledException) { return new("Paused", Message: "Shader preparation paused. Verified asset packs were kept."); }
        catch (Exception e) { return new("Failed", Code: e is CacheException known ? known.Code : "SHADER_PREPARATION_FAILED", Message: e.Message, Details: current + Environment.NewLine + e); }
    }
    public static byte[] ReadPayload(string root, string hash)
    {
        var path = SafePaths.Child(root, InputFiles.PathFor("shader-payloads", hash, ".bin"));
        using var stream = File.OpenRead(path); if (stream.Length > ModulePayloadReader.MaximumResourceBytes) throw InputFiles.Damaged();
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); if (InputFiles.Hash(bytes) != hash) throw InputFiles.Damaged(); return bytes;
    }
    private static string StorePayload(string root, byte[] bytes)
    { var hash = InputFiles.Hash(bytes); InputFiles.Write(root, InputFiles.PathFor("shader-payloads", hash, ".bin"), bytes); return hash; }
    private static CacheException Invalid(string message) => new("SHADER_PREPARATION_UNSUPPORTED", message);
}
