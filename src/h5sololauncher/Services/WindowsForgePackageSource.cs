using Windows.Management.Deployment;
using H5SoloLauncher.Models;

namespace H5SoloLauncher.Services;

public sealed class WindowsForgePackageSource : IForgePackageSource
{
    // Stable product identity, independent of the user, drive and installed version.
    public const string PackageFamilyName = "Microsoft.Halo5Forge_8wekyb3d8bbwe";

    public IReadOnlyList<ForgeInstallation> FindForCurrentUser()
    {
        var manager = new PackageManager();
        // Empty SID queries this Windows account; it does not enumerate other users.
        return manager.FindPackagesForUser(string.Empty, PackageFamilyName)
            .Where(package => !package.IsFramework && !package.IsResourcePackage && !package.IsBundle)
            .Select(package =>
            {
                var version = package.Id.Version;
                return new ForgeInstallation(
                    package.Id.FullName,
                    new Version(version.Major, version.Minor, version.Build, version.Revision),
                    package.InstalledLocation?.Path ?? string.Empty,
                    package.Status.VerifyIsOK());
            })
            .ToArray();
    }
}
