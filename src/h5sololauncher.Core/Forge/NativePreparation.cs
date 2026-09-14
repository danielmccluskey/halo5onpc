using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Planning;
using H5SoloLauncher.Core.Preparation;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Core.Forge;

public sealed record NativeLayoutSummary(string Id, string RelativePath, string PackageFullName, string PlanId, int Structures,
    int ExactHeaders, int DifferentHeaders, int MissingHeaders, NativeLayoutComparison[] Comparisons);
public sealed record NativeLayoutComparison(SchemaHeader Source, NativeSchema? Native, string State);
public sealed record NativeLayoutResult(string State, NativeLayoutSummary? Summary = null, string? Code = null, string? Message = null, string? Details = null);

public static class NativePreparation
{
    public static NativeLayoutResult CaptureLayouts(InputRequest request, Func<IForgeMemory> memoryFactory, IProgress<IndexProgress> progress, CancellationToken cancellation)
    {
        try
        {
            var source = SourceDiscovery.Describe(request.SourceRoot);
            var cache = CacheFolders.Open(request.CacheRoot, source.Root, request.ForgeRoot);
            using var lease = new FileStream(SafePaths.Child(cache.Root, "index.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var catalog = new CatalogSnapshot(cache, source);
            BuildPlanStore.ReadVerified(cache, catalog, request.PlanId);
            var forge = ForgeReportStore.Read(cache, request.PackageFullName, request.PlanId) ?? throw new CacheException("FORGE_DATA_REQUIRED", "Prepare Forge data before checking native layouts.");
            var inputs = InputCacheStore.ReadSummary(cache, request.PlanId, request.PackageFullName, forge.CatalogId) ?? throw new CacheException("INPUTS_REQUIRED", "Prepare the campaign inputs before checking native layouts.");
            var manifest = InputCacheStore.ReadManifest(cache.Root, inputs);
            progress.Report(new("Reading Forge's native layouts", 0, 0));
            using var memory = memoryFactory();
            var snapshot = ForgeLegacySchemas.Capture(ForgeSchemaRegistry.Capture(request.PackageFullName, memory, cancellation), memory);
            var byGuid = snapshot.Structures.ToDictionary(x => x.Guid, StringComparer.Ordinal);
            var comparisons = manifest.SchemaHeaders.Select(x =>
            {
                byGuid.TryGetValue(x.Guid, out var native);
                return new NativeLayoutComparison(x, native, native is null ? "Missing" : native.Schema == x.Schema ? "Exact" : "ConversionRequired");
            }).ToArray();
            cancellation.ThrowIfCancellationRequested();
            var id = InputFiles.Save(cache.Root, "native-schemas", snapshot);
            var summary = new NativeLayoutSummary(id, InputFiles.PathFor("native-schemas", id), request.PackageFullName, request.PlanId,
                snapshot.Structures.Length, comparisons.Count(x => x.State == "Exact"), comparisons.Count(x => x.State == "ConversionRequired"),
                comparisons.Count(x => x.State == "Missing"), comparisons);
            InputFiles.Write(cache.Root, "inputs/native-layout-summary.json", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(summary));
            CacheLog.Write(cache.Root, $"Native schema snapshot {id}: {summary.Structures} structures, {summary.ExactHeaders} identical source headers, {summary.DifferentHeaders} require conversion, {summary.MissingHeaders} absent. Game conversion pending.");
            return new("Captured", summary, Message: "Native Forge layouts saved. Different layouts still require conversion.");
        }
        catch (OperationCanceledException) { return new("Paused", Message: "Native layout collection paused."); }
        catch (Exception e) { return new("Failed", Code: e is CacheException known ? known.Code : "NATIVE_LAYOUT_FAILED", Message: e.Message, Details: e.ToString()); }
    }
}
