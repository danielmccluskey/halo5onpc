using System.IO;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Forge;
using H5SoloLauncher.Services;
using H5SoloLauncher.ViewModels;
using Xunit;

namespace H5SoloLauncher.Tests;

public sealed class ForgeViewModelTests
{
    [Fact]
    public async Task ForgePreparationSharesPauseAndDisablesConflictingOperations()
    {
        using var f = new ForgePreparationTests.Fixture(); var worker = new PendingWorker(); var model = await Model(f, worker);
        Assert.True(model.CanPrepareForge); var job = model.PrepareForgeAsync(); await model.PrepareForgeAsync(); await model.IndexAsync();
        Assert.True(model.IsPreparingForge); Assert.True(model.IsIndexing); Assert.False(model.CanChoose); Assert.False(model.CanPrepareForge);
        Assert.False(model.CanIndex); Assert.False(model.CanPlan); Assert.Equal(1, worker.Calls);
        var closing = model.PauseAndWaitAsync(); Assert.True(worker.Token.IsCancellationRequested); Assert.False(closing.IsCompleted);
        worker.Completion.SetResult(new("Paused", Message: "Paused after checkpoint")); await job; await closing;
        Assert.False(model.IsBusy); Assert.True(model.CanPrepareForge); Assert.Equal("Forge preparation paused.", model.ForgeStatus);
    }
    [Fact]
    public async Task ReportRestoresAndIsHiddenForAnotherPackagePlanOrSource()
    {
        using var f = new ForgePreparationTests.Fixture(); var result = f.Run(); var model = await Model(f, new PendingWorker());
        Assert.True(model.CanOpenForgeReport); Assert.Contains(result.Summary!.ReportId, model.ForgeDetails);
        model.SelectedLanguage = "another"; Assert.False(model.CanPrepareForge); Assert.False(model.CanOpenForgeReport);
        model.SelectedLanguage = "English(US)"; Assert.True(model.CanOpenForgeReport);
        model.Configure(f.Request.SourceRoot, f.ForgeRoot, true, ForgePreparationTests.Package.Replace("6192", "6193")); Assert.False(model.CanOpenForgeReport);
        model.Configure(Path.Combine(f.Request.SourceRoot, "different"), f.ForgeRoot, true, ForgePreparationTests.Package);
        Assert.False(model.CanOpenForgeReport); Assert.False(model.CanPrepareForge);
    }
    [Fact]
    public async Task FailureIncludesCopyableCodeAndKeepsPreviousReport()
    {
        using var f = new ForgePreparationTests.Fixture(); f.Run(); var worker = new PendingWorker(); var model = await Model(f, worker);
        worker.Completion.SetResult(new("Failed", Code: "FORGE_NOT_RUNNING", Message: "Start Forge normally.", Details: "reader detail"));
        await model.PrepareForgeAsync(); Assert.True(model.CanOpenForgeReport); Assert.Contains("FORGE_NOT_RUNNING", model.Details); Assert.Contains("reader detail", model.Details);
    }
    [Fact]
    public async Task DeniedProbeOffersExplicitReadGrantAndNormalPrepareDoesNotRequestIt()
    {
        using var f = new ForgePreparationTests.Fixture();
        f.Run(new(f) { ProbeResult = new("Denied", "Access denied", "FORGE_READ_DENIED", "Windows 5") });
        var worker = new PendingWorker(); var model = await Model(f, worker);
        Assert.True(model.NeedsForgeCacheAccess); Assert.True(model.CanAllowForgeCacheRead); Assert.Contains("needs attention", model.ForgeStatus);
        Assert.Contains("Windows 5", model.Details);
        var pending = model.AllowForgeCacheReadAsync(); Assert.True(worker.LastRequest!.AllowCacheRead);
        Assert.False(model.CanAllowForgeCacheRead); Assert.False(model.CanIndex);
        worker.Completion.SetResult(new("Paused")); await pending;
        await model.PrepareForgeAsync(); Assert.False(worker.LastRequest.AllowCacheRead);
        model.SelectedLanguage = "different"; Assert.False(model.CanAllowForgeCacheRead);
    }
    private static async Task<CacheViewModel> Model(ForgePreparationTests.Fixture f, PendingWorker worker)
    {
        var settings = new LauncherSettingsStore(Path.Combine(f.Plan.Files.Destination, "settings.json"));
        await settings.SaveCacheDirectoryAsync(f.Request.CacheRoot);
        var model = new CacheViewModel(settings, worker); model.Configure(f.Request.SourceRoot, f.ForgeRoot, true, ForgePreparationTests.Package);
        await model.RestoreAsync(); return model;
    }
    private sealed class PendingWorker : IIndexWorker, IForgeWorker
    {
        public int Calls { get; private set; }
        public CancellationToken Token { get; private set; }
        public ForgeRequest? LastRequest { get; private set; }
        public TaskCompletionSource<ForgeResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ForgeResult> PrepareForgeAsync(ForgeRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation)
        { Calls++; Token = cancellation; LastRequest = request; return Completion.Task; }
        public Task<IndexResult> RunAsync(IndexRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation) => throw new InvalidOperationException("Indexing should be disabled.");
    }
}
