namespace H5SoloLauncher.Models;

public enum ForgeCheckStatus
{
    Installed,
    NotInstalled,
    NeedsAttention,
    CheckFailed
}

public sealed record ForgeInstallation(
    string PackageFullName,
    Version Version,
    string InstallDirectory,
    bool IsHealthy);

public sealed record ForgeCheckResult(
    ForgeCheckStatus Status,
    ForgeInstallation? Installation = null,
    string? ErrorDetails = null);
