namespace H5SoloLauncher.Core;

public sealed record SourceDescriptor(string Root, string PackageVersion, string Identity);
public sealed record CacheLocation(string Root, string Id, long FreeBytes);
public sealed record IndexRequest(string SourceRoot, string CacheRoot, string? ForgeRoot = null);
public sealed record IndexProgress(string Stage, int Completed, int Total, string? File = null, int Reused = 0);
public sealed record IndexSummary(string State, string SourceRoot, string PackageVersion,
    int Files, int Modules, long Entries, long Blocks, long AudioEntries, long SourceBytes,
    long MetadataBytes, string? CompletedUtc = null, long UnresolvedPayloads = 0);
public sealed record IndexResult(string State, IndexSummary? Summary = null, string? Code = null,
    string? Message = null, string? Details = null);

public interface IIndexWorker
{
    Task<IndexResult> RunAsync(IndexRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation);
}

public sealed class CacheException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record WorkerMessage(string Type, int Version = 1, IndexRequest? Request = null,
    IndexProgress? Progress = null, IndexResult? Result = null,
    Planning.PlanRequest? PlanRequest = null, Planning.PlanResult? PlanResult = null,
    Forge.ForgeRequest? ForgeRequest = null, Forge.ForgeResult? ForgeResult = null,
    Forge.ForgeLaunchRequest? LaunchRequest = null, Forge.ForgeLaunchResult? LaunchResult = null,
    Preparation.InputRequest? InputRequest = null, Preparation.InputResult? InputResult = null,
    Preparation.PrepareRequest? PrepareRequest = null, Preparation.PreparationResult? PreparationResult = null,
    Runtime.CampaignPlayRequest? PlayRequest = null,Runtime.CampaignPlayResult? PlayResult = null,Runtime.CampaignWatchRequest? WatchRequest=null);
