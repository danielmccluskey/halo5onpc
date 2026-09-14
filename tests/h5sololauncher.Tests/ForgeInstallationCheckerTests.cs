using H5SoloLauncher.Models;
using H5SoloLauncher.Services;
using Xunit;

namespace H5SoloLauncher.Tests;

public sealed class ForgeInstallationCheckerTests
{
    [Fact]
    public async Task MissingRegistrationIsNotInstalled()
    {
        var result = await Check([]);
        Assert.Equal(ForgeCheckStatus.NotInstalled, result.Status);
        Assert.Null(result.Installation);
    }

    [Theory]
    [InlineData(@"D:\Games\Forge")]
    [InlineData(@"E:\Games with spaces\光环\Forge")]
    public async Task UsesDiscoveredFolderAndVersion(string folder)
    {
        var installation = new ForgeInstallation("test-package", new Version(3, 2, 1, 0), folder, true);
        var result = await Check([installation]);
        Assert.Equal(ForgeCheckStatus.Installed, result.Status);
        Assert.Equal(installation, result.Installation);
    }

    [Fact]
    public async Task UnhealthyRegistrationIsNotReportedAsMissingOrHealthy()
    {
        var result = await Check([new("test-package", new Version(1, 0), @"D:\Forge", false)]);
        Assert.Equal(ForgeCheckStatus.NeedsAttention, result.Status);
        Assert.NotNull(result.Installation);
    }

    [Fact]
    public async Task MissingLocationNeedsAttention()
    {
        var result = await Check([new("test-package", new Version(1, 0), "", true)]);
        Assert.Equal(ForgeCheckStatus.NeedsAttention, result.Status);
    }

    [Fact]
    public async Task NewerBrokenRegistrationIsNotHiddenByOlderHealthyOne()
    {
        var result = await Check([
            new("old", new Version(1, 0), @"D:\old", true),
            new("new", new Version(2, 0), @"D:\new", false)]);
        Assert.Equal(ForgeCheckStatus.NeedsAttention, result.Status);
        Assert.Equal("new", result.Installation!.PackageFullName);
    }

    [Fact]
    public async Task QueryFailurePreservesErrorAndDoesNotClaimForgeIsMissing()
    {
        var checker = new ForgeInstallationChecker(new ThrowingSource());
        var result = await checker.CheckAsync();
        Assert.Equal(ForgeCheckStatus.CheckFailed, result.Status);
        Assert.Contains("Access denied", result.ErrorDetails);
        Assert.Contains(nameof(UnauthorizedAccessException), result.ErrorDetails);
    }

    private static Task<ForgeCheckResult> Check(IReadOnlyList<ForgeInstallation> packages) =>
        new ForgeInstallationChecker(new Source(packages)).CheckAsync();

    private sealed class Source(IReadOnlyList<ForgeInstallation> packages) : IForgePackageSource
    {
        public IReadOnlyList<ForgeInstallation> FindForCurrentUser() => packages;
    }

    private sealed class ThrowingSource : IForgePackageSource
    {
        public IReadOnlyList<ForgeInstallation> FindForCurrentUser() =>
            throw new UnauthorizedAccessException("Access denied by test package source.");
    }
}
