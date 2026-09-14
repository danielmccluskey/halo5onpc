using H5SoloLauncher.Models;

namespace H5SoloLauncher.Services;

public interface IForgeInstallationChecker
{
    Task<ForgeCheckResult> CheckAsync();
}

public interface IForgePackageSource
{
    IReadOnlyList<ForgeInstallation> FindForCurrentUser();
}
