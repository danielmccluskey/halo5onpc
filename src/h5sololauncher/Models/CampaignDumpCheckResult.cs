namespace H5SoloLauncher.Models;

public enum CampaignDumpStatus
{
    Recognized,
    MissingFolder,
    WrongFolder,
    Incomplete,
    UnsupportedFormat,
    CheckFailed
}

public sealed record CampaignDumpInfo(
    string RootDirectory,
    string PackageVersion,
    int ModuleCount,
    int CampaignMetadataCount,
    int AudioPackageCount,
    int MovieCount);

public sealed record CampaignDumpCheckResult(
    CampaignDumpStatus Status,
    string SelectedDirectory,
    CampaignDumpInfo? Dump = null,
    IReadOnlyList<string>? Problems = null,
    string? ErrorDetails = null);
