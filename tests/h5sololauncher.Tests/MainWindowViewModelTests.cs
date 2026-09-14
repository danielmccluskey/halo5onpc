using H5SoloLauncher.Models;
using H5SoloLauncher.Services;
using H5SoloLauncher.ViewModels;
using Xunit;

namespace H5SoloLauncher.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public async Task DoesNotStartDuplicateChecksAndClearsStaleInstallationOnRecheck()
    {
        var checker = new ControlledChecker();
        var model = new MainWindowViewModel(checker, CampaignDumpViewModelTests.Empty());
        var first = model.CheckAsync();
        Assert.True(model.IsChecking);
        Assert.False(model.CanCopy);
        await model.CheckAsync();
        Assert.Equal(1, checker.Calls);

        checker.Next.SetResult(new(ForgeCheckStatus.Installed,
            new("test", new Version(1, 0), @"D:\Forge", true)));
        await first;
        Assert.True(model.HasInstallation);
        Assert.True(model.CanCopy);
        Assert.True(model.CanChooseCampaignFolder);

        checker.Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = model.CheckAsync();
        Assert.False(model.HasInstallation);
        Assert.False(model.CanChooseCampaignFolder);
        Assert.Equal(string.Empty, model.Details);
        checker.Next.SetResult(new(ForgeCheckStatus.NotInstalled));
        await second;
        Assert.False(model.HasInstallation);
        Assert.True(model.CanCheck);
        Assert.Contains("NotInstalled", model.Details);
    }

    [Fact]
    public async Task UnexpectedCheckerFailureLeavesRetryAndCopyAvailable()
    {
        var model = new MainWindowViewModel(new FailingChecker(), CampaignDumpViewModelTests.Empty());
        await model.CheckAsync();
        Assert.False(model.IsChecking);
        Assert.True(model.CanCheck);
        Assert.True(model.CanCopy);
        Assert.False(model.CanChooseCampaignFolder);
        Assert.Contains("CheckFailed", model.Details);
        Assert.Contains("test failure", model.Details);
    }

    private sealed class ControlledChecker : IForgeInstallationChecker
    {
        public int Calls { get; private set; }
        public TaskCompletionSource<ForgeCheckResult> Next { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ForgeCheckResult> CheckAsync() { Calls++; return Next.Task; }
    }

    private sealed class FailingChecker : IForgeInstallationChecker
    {
        public Task<ForgeCheckResult> CheckAsync() => throw new InvalidOperationException("test failure");
    }
}
