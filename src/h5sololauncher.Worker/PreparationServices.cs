using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Forge;
using H5SoloLauncher.Core.Planning;
using H5SoloLauncher.Core.Preparation;

namespace H5SoloLauncher.Worker;

internal sealed class PreparationServices : IPreparationServices
{
    public IndexResult Index(IndexRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation) => new SourceIndexer().Run(request, progress, cancellation);
    public PlanResult Plan(PlanRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation) => new BuildPlanner().Run(request, progress, cancellation);
    public ForgeResult Forge(ForgeRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation) => new ForgePreparation().Run(request, () =>
    {
        var launched = WindowsForgeLaunch.Run(new(request.ForgeRoot, request.PackageFullName), progress, cancellation);
        if (launched.State == "Paused") throw new OperationCanceledException(cancellation);
        if (launched.State != "Running") throw new CacheException(launched.Code ?? "FORGE_LAUNCH_FAILED", launched.Message ?? "Forge could not start.");
        return new ForgeFiles(request, cancellation);
    }, progress, cancellation);
    public InputResult Inputs(InputRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation) => new InputCacheBuilder().Run(request, progress, cancellation);
    public NativeLayoutResult Layouts(InputRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation) => NativePreparation.CaptureLayouts(request,
        () => new ForgeMemory(request.ForgeRoot, request.PackageFullName), progress, cancellation);
    public NativeModuleResult NativeModules(InputRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation) => NativeModuleCache.Run(request,
        () => new ForgeFiles(new(request.SourceRoot, request.CacheRoot, request.ForgeRoot, request.PackageFullName, request.PlanId), cancellation), progress, cancellation);
    public EffectivePlanResult Resolve(ConversionRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation) => EffectivePlanBuilder.Run(request, progress, cancellation);
    public PreparationResult Complete(InputRequest request,string effectivePlanId,IProgress<IndexProgress> progress,CancellationToken cancellation)=>CampaignPreparation.Run(request,effectivePlanId,progress,cancellation);
}
