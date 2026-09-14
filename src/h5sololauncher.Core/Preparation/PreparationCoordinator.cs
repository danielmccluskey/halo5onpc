using H5SoloLauncher.Core.Forge;
using H5SoloLauncher.Core.Planning;

namespace H5SoloLauncher.Core.Preparation;

public sealed record PrepareRequest(string SourceRoot, string CacheRoot, string ForgeRoot, string PackageFullName, string Language = "English(US)");
public sealed record PreparationResult(string State, string Phase, string Message, string? Code = null, string? Details = null,
    string? PlanId = null, string? NativeModuleId = null, string? NativeSchemaId = null, string? EffectivePlanId = null,
    int Tags = 0, int Unresolved = 0, string? PreparedId = null);
public interface IPreparationWorker
{
    Task<PreparationResult> PrepareAsync(PrepareRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation);
}
public interface IPreparationServices
{
    IndexResult Index(IndexRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation);
    PlanResult Plan(PlanRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation);
    ForgeResult Forge(ForgeRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation);
    InputResult Inputs(InputRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation);
    NativeLayoutResult Layouts(InputRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation);
    NativeModuleResult NativeModules(InputRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation);
    EffectivePlanResult Resolve(ConversionRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation);
    PreparationResult Complete(InputRequest request,string effectivePlanId,IProgress<IndexProgress> progress,CancellationToken cancellation);
}

/// <summary>One cancelable user operation, with independently resumable and verified stages.</summary>
public sealed class PreparationCoordinator(IPreparationServices services)
{
    public PreparationResult Run(PrepareRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation)
    {
        string phase = "Checking dump", planId = "", modulesId = "", schemaId = "";
        void Begin(string value) { cancellation.ThrowIfCancellationRequested(); phase = value; progress.Report(new(value, 0, 0)); }
        void Require(string actual, string expected, string? code, string? message, string? details)
        {
            if (actual == "Paused") throw new OperationCanceledException(cancellation);
            if (actual != expected) throw new StageException(code ?? "PREPARATION_STAGE_FAILED", message ?? "Preparation couldn't complete this stage.", details);
        }
        try
        {
            if(!string.IsNullOrWhiteSpace(request.Language) && request.Language!="English(US)")
                throw new CacheException("CAMPAIGN_LANGUAGE_UNSUPPORTED","This version prepares English (US) campaign audio and menus. Select English (US) before preparing.");
            Begin("Checking and indexing the dump");
            var index = services.Index(new(request.SourceRoot, request.CacheRoot, request.ForgeRoot), progress, cancellation);
            Require(index.State, "Indexed", index.Code, index.Message, index.Details);
            Begin("Finding Osiris and Blue Team dependencies");
            var plan = services.Plan(new(request.SourceRoot, request.CacheRoot, request.ForgeRoot, Language: string.IsNullOrWhiteSpace(request.Language) ? "English(US)" : request.Language), progress, cancellation);
            Require(plan.State, "Planned", plan.Code, plan.Message, plan.Details);
            planId = plan.Summary?.PlanId ?? throw new CacheException("PLAN_RESULT_MISSING", "The dependency stage did not return a saved plan.");
            Begin("Starting Forge and preparing cache access");
            // Preparing a playable cache includes granting the target game read-only access to that owned cache.
            var forge = services.Forge(new(request.SourceRoot, request.CacheRoot, request.ForgeRoot, request.PackageFullName, planId, AllowCacheRead: true), progress, cancellation);
            Require(forge.State, "Prepared", forge.Code, forge.Message, forge.Details);
            if (forge.Summary?.Probe.State != "Passed") throw new StageException(forge.Summary?.Probe.Code ?? "FORGE_CACHE_ACCESS", forge.Summary?.Probe.Message ?? "Forge couldn't verify access to this cache.", forge.Summary?.Probe.Details);
            var input = new InputRequest(request.SourceRoot, request.CacheRoot, request.ForgeRoot, request.PackageFullName, planId);
            Begin("Caching campaign tags");
            var inputs = services.Inputs(input, progress, cancellation); Require(inputs.State, "Cached", inputs.Code, inputs.Message, inputs.Details);
            Begin("Checking native Forge layouts");
            var layouts = services.Layouts(input, progress, cancellation); Require(layouts.State, "Captured", layouts.Code, layouts.Message, layouts.Details);
            schemaId = layouts.Summary?.Id ?? throw new CacheException("NATIVE_LAYOUT_RESULT_MISSING", "Native layout collection did not return a snapshot.");
            Begin("Collecting native conversion controls");
            var modules = services.NativeModules(input, progress, cancellation); Require(modules.State, "Cached", modules.Code, modules.Message, modules.Details);
            modulesId = modules.ManifestId ?? throw new CacheException("NATIVE_MODULE_RESULT_MISSING", "Native collection did not return a manifest.");
            Begin("Selecting campaign layers");
            var effective = services.Resolve(new(input, modulesId, schemaId), progress, cancellation); Require(effective.State, "Planned", effective.Code, effective.Message, effective.Details);
            Begin("Building the playable campaign cache");
            var complete=services.Complete(input,effective.ManifestId??throw new CacheException("EFFECTIVE_PLAN_MISSING","Campaign selection returned no saved plan."),progress,cancellation);
            return complete with {PlanId=planId,NativeModuleId=modulesId,NativeSchemaId=schemaId,EffectivePlanId=effective.ManifestId,Tags=effective.Tags,Unresolved=effective.Unresolved?.Length??0};
        }
        catch (OperationCanceledException) { return new("Paused", phase, "Preparation paused. Completed cache stages were kept. Use Prepare / resume to continue.", PlanId: planId, NativeModuleId: modulesId, NativeSchemaId: schemaId); }
        catch (StageException e) { return new("Failed", phase, e.Message, e.Code, e.Details, planId, modulesId, schemaId); }
        catch (Exception e) { return new("Failed", phase, e.Message, e is CacheException known ? known.Code : "PREPARATION_FAILED", e.ToString(), planId, modulesId, schemaId); }
    }
    private sealed class StageException(string code, string message, string? details) : Exception(message)
    { public string Code { get; } = code; public string? Details { get; } = details; }
}
