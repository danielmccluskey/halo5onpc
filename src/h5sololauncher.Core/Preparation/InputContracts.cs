using H5SoloLauncher.Core.Planning;

namespace H5SoloLauncher.Core.Preparation;

public sealed record InputRequest(string SourceRoot, string CacheRoot, string ForgeRoot, string PackageFullName, string PlanId, bool KeepRebuildData = true);
public sealed record NamedDependency(AssetReference Identity, string? Name);
public sealed record TagMetadata(string Schema, string[] RootGuids, NamedDependency[] Dependencies);
public sealed record InputRecord(string Sha256, int Length, long Offset, TagMetadata Metadata);
public sealed record InputBatch(string Rules, string Key, string PackId, long PackBytes, InputRecord[] Records);
public sealed record InputBatchReference(string Id, string Key);
public sealed record DependencyNameEvidence(AssetReference Identity, string[] Names, string State,
    Forge.ForgeIdentityAlternative[] Candidates);
public sealed record SchemaHeader(string Group, string Guid, string Schema, int Tags);
public sealed record InputManifest(int Format, string Rules, string PlanId, string InputFingerprint,
    string PackageFullName, string ForgeCatalogId, string BundleId, string Language,
    InputBatchReference[] Batches, SchemaHeader[] SchemaHeaders, DependencyNameEvidence[] MissingReferences,
    int Tags, int UniquePayloads, long PayloadBytes, int TagsWithoutSingleRoot, int PatchChoices,
    int UnresolvedResources, string[] RemainingSteps);
public sealed record InputSummary(string ManifestId, string RelativePath, string PlanId, string InputFingerprint,
    string PackageFullName, string ForgeCatalogId, string Language, int Tags, int UniquePayloads, long PayloadBytes,
    int Packs, int ReusedPacks, int NamedMissing, int NameMatches, int SchemaHeaders, int TagsWithoutSingleRoot,
    int PatchChoices, int UnresolvedResources, long SourceBytesRead, string Rules);
public sealed record InputResult(string State, InputSummary? Summary = null, string? Code = null, string? Message = null, string? Details = null);
public interface IInputWorker
{
    Task<InputResult> BuildInputsAsync(InputRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation);
}
