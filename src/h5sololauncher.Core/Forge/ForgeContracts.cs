using H5SoloLauncher.Core.Planning;

namespace H5SoloLauncher.Core.Forge;

public sealed record ForgeRequest(string SourceRoot, string CacheRoot, string ForgeRoot, string PackageFullName, string PlanId, bool AllowCacheRead = false);
public sealed record ForgeRead(byte[] Bytes, long FileLength, long LastWriteFileTime);
public sealed record CacheProbe(string State, string Message, string? Code = null, string? Details = null);
public interface IForgeFileReader : IDisposable
{
    string Mode { get; }
    ForgeRead Read(string relative, long offset, int count, CancellationToken cancellation);
    CacheProbe Probe(string path, byte[] expected, CancellationToken cancellation);
}
public interface IForgePayloadReader : IForgeFileReader
{
    ForgeRead ReadPayload(string relative, long offset, int count, CancellationToken cancellation);
}
public interface IForgeAudioReader : IDisposable
{
    ForgeRead ReadAudio(string relative, long offset, int count, CancellationToken cancellation);
}
public interface IForgeWorker
{
    Task<ForgeResult> PrepareForgeAsync(ForgeRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation);
}
public sealed record ForgeModule(string Path, long Length, string Digest, int Revision, int Tags, int TableBytes);
public sealed record ForgeCandidate(string File, int Item, string Name, string Checksum);
public sealed record ForgeIdentityAlternative(string File, int Item, string Name, string AssetId, string Checksum);
public sealed record ForgeMatch(AssetReference Identity, ForgeCandidate[] Candidates, ForgeIdentityAlternative[]? DifferentAssetIds = null);
public sealed record ForgeReport(int Format, string Rules, string CacheId, string PackageFullName, string PlanId,
    string CatalogId, ForgeModule[] Modules, ForgeMatch[] MissingReferences, CacheProbe Probe, string ReaderMode, string[] RemainingSteps);
public sealed record ForgeSummary(string ReportId, string RelativePath, string PackageFullName, string PlanId,
    string CatalogId, int Modules, int Tags, long TableBytes, int MissingIdentities, int NativeMatches, int Unmatched,
    int ReusedModules, CacheProbe Probe, string ReaderMode, int DifferentAssetIds = 0);
public sealed record ForgeResult(string State, ForgeSummary? Summary = null, string? Code = null, string? Message = null, string? Details = null);
