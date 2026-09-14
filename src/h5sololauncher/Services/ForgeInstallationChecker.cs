using H5SoloLauncher.Models;

namespace H5SoloLauncher.Services;

public sealed class ForgeInstallationChecker(IForgePackageSource packages) : IForgeInstallationChecker
{
    public Task<ForgeCheckResult> CheckAsync() => Task.Run(() =>
    {
        try
        {
            var installation = packages.FindForCurrentUser()
                .OrderByDescending(package => package.Version)
                .FirstOrDefault();

            if (installation is null)
                return new ForgeCheckResult(ForgeCheckStatus.NotInstalled);

            // A registered package with a Windows health problem is still installed.
            // Do not confuse that condition (or a query failure) with absence.
            var status = installation.IsHealthy && !string.IsNullOrWhiteSpace(installation.InstallDirectory)
                ? ForgeCheckStatus.Installed
                : ForgeCheckStatus.NeedsAttention;
            return new ForgeCheckResult(status, installation);
        }
        catch (Exception exception)
        {
            return new ForgeCheckResult(ForgeCheckStatus.CheckFailed, ErrorDetails: exception.ToString());
        }
    });
}
