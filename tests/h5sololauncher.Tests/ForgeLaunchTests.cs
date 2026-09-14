using System.IO;
using System.Runtime.InteropServices;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Forge;
using H5SoloLauncher.Models;
using H5SoloLauncher.Services;
using H5SoloLauncher.ViewModels;
using Xunit;

namespace H5SoloLauncher.Tests;

public sealed class ForgeLaunchTests
{
    private static readonly ForgeLaunchRequest Request = new("D:\\Forge", ForgePreparationTests.Package);
    private sealed class Platform : IForgeLaunchPlatform
    {
        public Func<int, RunningForge?> Running { get; set; } = _ => null;
        public bool RecentAttempt { get; set; }
        public int LaunchCalls, Finds, Clears, Begins;
        public Exception? PreflightFailure, LaunchFailure;
        public Action? Waiting;
        public bool Released;
        public IDisposable Acquire() => new Lease(() => Released = true);
        public void Validate(ForgeLaunchRequest request) { }
        public RunningForge? FindForge(ForgeLaunchRequest request) => Running(Finds++);
        public void PrepareLaunch() { if (PreflightFailure is not null) throw PreflightFailure; }
        public void BeginAttempt() { Begins++; RecentAttempt = true; }
        public void EndAttempt() { Clears++; RecentAttempt = false; }
        public void LaunchThroughHalo(IProgress<IndexProgress> progress, CancellationToken cancellation) { LaunchCalls++; if (LaunchFailure is not null) throw LaunchFailure; }
        public void Delay(CancellationToken cancellation) { Waiting?.Invoke(); cancellation.ThrowIfCancellationRequested(); }
        private sealed class Lease(Action release) : IDisposable { public void Dispose() => release(); }
    }
    private static ForgeLaunchResult Run(Platform platform, CancellationToken cancellation = default) => new ForgeLaunchCoordinator(platform, 8, 3).Run(Request, new PlanFixture.Callback(_ => { }), cancellation);
    [Fact]
    public void ReusesExistingForgeWithoutHaloOrAnotherLaunch()
    {
        var platform = new Platform { Running = _ => new(42, 100), RecentAttempt = true, PreflightFailure = new Exception("Halo missing") };
        var result = Run(platform); Assert.Equal("Running", result.State); Assert.Equal(42, result.ProcessId);
        Assert.Equal(0, platform.LaunchCalls); Assert.Equal(0, platform.Begins); Assert.Equal(1, platform.Clears); Assert.True(platform.Released);
    }
    [Fact]
    public void WaitsForOneStableProcessAfterStartingThroughHalo()
    {
        var platform = new Platform { Running = call => call < 3 ? null : new(42, 100) };
        var result = Run(platform); Assert.Equal("Running", result.State); Assert.Equal(1, platform.LaunchCalls); Assert.Equal(1, platform.Begins);
        Assert.Equal(1, platform.Clears); Assert.Equal(6, platform.Finds); Assert.True(platform.Released);
    }
    [Fact]
    public void WindowsAcceptanceAloneDoesNotReportForgeRunning()
    {
        var platform = new Platform(); var result = Run(platform);
        Assert.Equal("FORGE_START_TIMEOUT", result.Code); Assert.True(platform.RecentAttempt); Assert.Equal(0, platform.Clears);
        var retry = Run(platform); Assert.Equal("FORGE_LAUNCH_PENDING", retry.Code); Assert.Equal(1, platform.LaunchCalls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExitOrPidReuseDuringStartupIsNotSuccess(bool reusedPid)
    {
        var platform = new Platform { Running = call => call == 0 ? null : call == 1 ? new(42, 100) : reusedPid ? new(42, 200) : null };
        Assert.Equal("FORGE_EXITED_DURING_STARTUP", Run(platform).Code); Assert.Equal(0, platform.Clears);
    }
    [Fact]
    public void MissingHaloIsActionableBeforeRecordingAnAttempt()
    {
        var platform = new Platform { PreflightFailure = new CacheException("HALO_APP_MISSING", "Install Halo") };
        Assert.Equal("HALO_APP_MISSING", Run(platform).Code); Assert.Equal(0, platform.Begins); Assert.Equal(0, platform.LaunchCalls);
    }
    [Fact]
    public void CancellationRetainsLateStartupGuardAndDoesNotLaunchAgain()
    {
        using var stop = new CancellationTokenSource(); var platform = new Platform { Waiting = stop.Cancel };
        var result = Run(platform, stop.Token); Assert.Equal("Paused", result.State); Assert.True(platform.RecentAttempt); Assert.True(platform.Released);
        Assert.Equal("FORGE_LAUNCH_PENDING", Run(platform).Code); Assert.Equal(1, platform.LaunchCalls);
    }
    [Fact]
    public void BridgeFailurePreservesTheCauseAndUncertainRequestGuard()
    {
        var platform = new Platform { LaunchFailure = new CacheException("HALO_BRIDGE_FAILED", "test HRESULT") };
        var result = Run(platform); Assert.Equal("HALO_BRIDGE_FAILED", result.Code); Assert.Contains("test HRESULT", result.Details); Assert.True(platform.RecentAttempt);
    }
    [Fact]
    public async Task StartButtonDisablesConflictsAndWindowCloseWaitsForWorkerCancellation()
    {
        var worker = new PendingLauncher(); var model = new MainWindowViewModel(new Checker(), CampaignDumpViewModelTests.Empty(), launcher: worker);
        await model.CheckAsync(); Assert.True(model.CanStartForge);
        var pending = model.StartForgeAsync(); await model.StartForgeAsync(); await model.CheckAsync();
        Assert.False(model.CanCheck); Assert.False(model.CanChooseCampaignFolder); Assert.True(model.CanCopy); Assert.Equal(1, worker.Calls);
        var closing = model.StopForgeStartupAndWaitAsync(); Assert.True(worker.Token.IsCancellationRequested); Assert.False(closing.IsCompleted);
        worker.Completion.SetResult(new("Paused", Message: "May start later")); await pending; await closing;
        Assert.False(model.IsStartingForge); Assert.True(model.CanStartForge); Assert.Contains("May start later", model.Details);
    }
    [Fact]
    public async Task MissingHaloShowsStoreRecoveryAndCopyableError()
    {
        var worker = new PendingLauncher(); worker.Completion.SetResult(new("Failed", Code: "HALO_APP_MISSING", Message: "Install Halo", Details: "package discovery"));
        var model = new MainWindowViewModel(new Checker(), CampaignDumpViewModelTests.Empty(), launcher: worker);
        await model.CheckAsync(); await model.StartForgeAsync(); Assert.True(model.NeedsHaloApp); Assert.Contains("package discovery", model.Details);
    }
    [Fact]
    public async Task WorkerLaunchProtocolRejectsInvalidIdentityWithoutOpeningAnApp()
    {
        var result = await new IndexWorkerClient(Environment.GetEnvironmentVariable("H5SOLO_TEST_WORKER"))
            .StartForgeAsync(Request with { PackageFullName = "invalid" }, new PlanFixture.Callback(_ => { }), default);
        Assert.Equal("Failed", result.State); Assert.Equal("FORGE_IDENTITY_INVALID", result.Code);
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint NativeLaunch(nint request);
    [Fact]
    public void PackagedHaloBridgeRejectsWrongProtocolAndNonHaloHost()
    {
        var library = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "h5sololauncher.HubBridge.dll")); var memory = Marshal.AllocHGlobal(20);
        try
        {
            var launch = Marshal.GetDelegateForFunctionPointer<NativeLaunch>(NativeLibrary.GetExport(library, "H5LaunchForge"));
            Marshal.Copy(new byte[20], 0, memory, 20); Assert.Equal(0u, launch(memory)); Assert.Equal(unchecked((int)0x80070057), Marshal.ReadInt32(memory, 8));
            Marshal.WriteInt32(memory, 0, 0x48354C48); Marshal.WriteInt32(memory, 4, 1); Assert.Equal(0u, launch(memory));
            Assert.Equal(unchecked((int)0x80070005), Marshal.ReadInt32(memory, 8)); Assert.Equal(0, Marshal.ReadInt32(memory, 12)); Assert.Equal(0, Marshal.ReadInt32(memory, 16));
        }
        finally { Marshal.FreeHGlobal(memory); NativeLibrary.Free(library); }
    }
    private sealed class Checker : IForgeInstallationChecker
    { public Task<ForgeCheckResult> CheckAsync() => Task.FromResult(new ForgeCheckResult(ForgeCheckStatus.Installed, new(Request.PackageFullName, new(1, 0), Request.ForgeRoot, true))); }
    private sealed class PendingLauncher : IForgeLaunchWorker
    {
        public int Calls; public CancellationToken Token;
        public TaskCompletionSource<ForgeLaunchResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ForgeLaunchResult> StartForgeAsync(ForgeLaunchRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation)
        { Calls++; Token = cancellation; return Completion.Task; }
    }
}
