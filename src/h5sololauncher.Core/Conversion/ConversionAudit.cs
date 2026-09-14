using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Forge;
using H5SoloLauncher.Core.Planning;
using H5SoloLauncher.Core.Preparation;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Core.Conversion;

public sealed record ConversionAuditRequest(InputRequest Inputs, string EffectivePlanId);
public sealed record ConversionProblem(string File, int Item, string Group, string Code, string Message);
public sealed record ConversionHeader(string Group, string Guid, string SourceSchema, string? NativeSchema, int Tags);
public sealed record ConversionAuditReport(string EffectivePlanId, int Tags, int Resources, ConversionHeader[] Headers, BitmapDescriptor[] Bitmaps, ConversionProblem[] Problems);
public sealed record ConversionAuditResult(string State, string? ManifestId = null, int Tags = 0, int Resources = 0, int BitmapShapes = 0, int Problems = 0, string? Code = null, string? Message = null, string? Details = null);

public static class ConversionAudit
{
    public static ConversionAuditResult Run(ConversionAuditRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation)
    {
        var input = request.Inputs;
        try
        {
            var source = SourceDiscovery.Describe(input.SourceRoot); var cache = CacheFolders.Open(input.CacheRoot, source.Root, input.ForgeRoot);
            using var lease = new FileStream(SafePaths.Child(cache.Root, "index.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var catalog = new CatalogSnapshot(cache, source); BuildPlanStore.ReadVerified(cache, catalog, input.PlanId);
            var plan = InputFiles.Read<EffectivePlan>(cache.Root, InputFiles.PathFor("effective-plans", request.EffectivePlanId), 128L * 1024 * 1024, request.EffectivePlanId);
            if (plan.SourcePlanId != input.PlanId || plan.InputFingerprint != catalog.Fingerprint || plan.Rules != EffectivePlanBuilder.Rules) throw InputFiles.Damaged();
            var schemas = InputFiles.Read<NativeSchemaSnapshot>(cache.Root, InputFiles.PathFor("native-schemas", plan.SchemaId), 4 * 1024 * 1024, plan.SchemaId).Structures.ToDictionary(x => x.Guid);
            var files = catalog.Files.ToDictionary(x => x.Path);
            List<ConversionProblem> problems = []; Dictionary<string, BitmapDescriptor> bitmaps = [];
            List<(string Group, string Guid, string Source, string? Native)> headers = []; int tags = 0, resources = 0;
            foreach (var group in plan.Tags.GroupBy(x => x.File).OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                cancellation.ThrowIfCancellationRequested();
                var path = SafePaths.Child(source.Root, group.Key); var metadata = FileMetadata.Read(path, "Module", cancellation);
                if (metadata.Digest != files[group.Key].Digest || metadata.FileLength != files[group.Key].Length) throw new CacheException("DUMP_CHANGED", "A source module changed after indexing.");
                using var stream = File.OpenRead(path);
                foreach (var tag in group)
                {
                    cancellation.ThrowIfCancellationRequested();
                    try
                    {
                        var entry = metadata.Entry(tag.Item); var payload = ModulePayloadReader.Read(stream, metadata, entry, cancellation);
                        if (payload.Sha256 != tag.Sha256) throw new CacheException("DUMP_CHANGED", "A source tag changed after layer selection.");
                        var document = new TagDocument(payload.Bytes);
                        foreach (var guid in document.Metadata.RootGuids)
                            headers.Add((tag.Identity.Group, guid, document.Metadata.Schema, schemas.GetValueOrDefault(guid)?.Schema));
                        var children = tag.Resources;
                        for (var j = 0; j < children.Length; j++)
                        {
                            var child = metadata.Entry(children[j]);
                            if (child.Parent != entry.Index) throw new CacheException("RESOURCE_PARENT", "A resource belongs to a different tag.");
                            if (child.StoredSize == 0) { problems.Add(new(tag.File, child.Index, tag.Identity.Group, "RESOURCE_STRIPPED", "A selected resource has no stored payload.")); continue; }
                            var content = ModulePayloadReader.ReadResource(stream, metadata, child, cancellation); resources++;
                            TagDocument? resource = content.Bytes.AsSpan().StartsWith("ucsh"u8) ? new TagDocument(content.Bytes, resource: true) : null;
                            if (resource is not null)
                                foreach (var guid in resource.Metadata.RootGuids)
                                    headers.Add((tag.Identity.Group + "/resource", guid, resource.Metadata.Schema, schemas.GetValueOrDefault(guid)?.Schema));
                            if (tag.Identity.Group != "bitm") continue;
                            if (resource is null) throw new CacheException("BITMAP_RESOURCE", "A bitmap resource has no metadata header.");
                            var imageRef = document.Structures.Single(x => x.FieldBlock == 0 && x.FieldOffset == 240);
                            var images = document.Block(imageRef.Target);
                            if (images.Length != children.Length * 40) throw new CacheException("BITMAP_IMAGES", "The bitmap image count differs from its resource count.");
                            var descriptor = BitmapDescriptor.Read(images.Slice(j * 40, 40), resource); bitmaps.TryAdd(descriptor.Key, descriptor);
                        }
                    }
                    catch (CacheException e) when (e.Code != "DUMP_CHANGED") { problems.Add(new(tag.File, tag.Item, tag.Identity.Group, e.Code, e.Message)); }
                    tags++; progress.Report(new("Checking conversion layouts and texture shapes", tags, plan.Tags.Length, tag.File + " / item " + tag.Item));
                }
            }
            var report = new ConversionAuditReport(request.EffectivePlanId, tags, resources, headers.GroupBy(x => x)
                .Select(x => new ConversionHeader(x.Key.Group, x.Key.Guid, x.Key.Source, x.Key.Native, x.Count())).OrderBy(x => x.Group, StringComparer.Ordinal).ThenBy(x => x.Guid, StringComparer.Ordinal).ThenBy(x => x.SourceSchema, StringComparer.Ordinal).ToArray(),
                bitmaps.Values.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray(), problems.ToArray());
            cancellation.ThrowIfCancellationRequested(); var id = InputFiles.Save(cache.Root, "conversion-audits", report);
            return new("Audited", id, tags, resources, bitmaps.Count, problems.Count, Message: "Conversion layout evidence saved.");
        }
        catch (OperationCanceledException) { return new("Paused", Message: "Conversion audit paused."); }
        catch (Exception e) { return new("Failed", Code: e is CacheException known ? known.Code : "CONVERSION_AUDIT_FAILED", Message: e.Message, Details: e.ToString()); }
    }
}
