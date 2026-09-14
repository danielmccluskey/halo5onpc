namespace H5SoloLauncher.Core.Planning;

public sealed record ContentBundle(string Id, int Revision, string Title, string[] Scenarios);
public sealed record AssetReference(string Group, string TagId, string AssetId);
public sealed record PlanRequest(string SourceRoot, string CacheRoot, string? ForgeRoot = null,
    string BundleId = "first-two-missions", string Language = "English(US)");
public sealed record PlanResource(int Item, string State, long StoredBytes, long LogicalBytes);
public sealed record PlannedTag(string File, int Item, string Name, AssetReference Identity, string Checksum,
    bool Root, string PayloadSha256, long StoredBytesRead, long LogicalBytes,
    AssetReference[] Dependencies, PlanResource[] Resources);
public sealed record PlanIssue(string Code, string Message, string? File = null, int? Item = null, AssetReference? Identity = null);
public sealed record PlanInput(string Path, string Kind, long Length, string Digest);
public sealed record SourceBuildPlan(int Format, string Rules, string InputFingerprint, ContentBundle Bundle,
    string Language, string[] RootModules, PlanInput[] Inputs, PlannedTag[] Tags, PlanIssue[] Issues,
    string[] AudioPackages, string[] Movies, string[] RemainingSteps);
public sealed record PlanSummary(string PlanId, string InputFingerprint, string BundleId, string Title, string Language,
    int RootModules, int Tags, int DependencyReferences, int Resources, int Issues, int MissingReferences,
    long StoredBytesRead, long LogicalBytes, string RelativePath, int ReusedAnalyses,
    int PatchChoices = 0, int UnresolvedResources = 0, string RulesVersion = "", int BundleRevision = 0);
public sealed record PlanResult(string State, PlanSummary? Summary = null, string? Code = null, string? Message = null, string? Details = null);
public interface IPlanWorker
{
    Task<PlanResult> PlanAsync(PlanRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation);
}
