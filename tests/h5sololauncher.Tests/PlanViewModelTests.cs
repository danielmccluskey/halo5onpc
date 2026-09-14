using System.IO;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Planning;
using H5SoloLauncher.Services;
using H5SoloLauncher.ViewModels;
using Xunit;

namespace H5SoloLauncher.Tests;

public sealed class PlanViewModelTests
{
    [Fact]
    public async Task PlanningDisablesConflictingOperationsAndClosesThroughPause()
    {
        using var f = new PlanFixture(); var worker = new PendingWorker(); var model = await Model(f, worker);
        Assert.True(model.CanPlan); var job = model.PlanAsync(); await model.PlanAsync(); await model.IndexAsync();
        Assert.True(model.IsPlanning); Assert.False(model.CanIndex); Assert.False(model.CanChoose); Assert.Equal(1, worker.Calls);
        var language = model.SelectedLanguage; model.SelectedLanguage = "another language"; Assert.Equal(language, model.SelectedLanguage);
        var closing = model.PauseAndWaitAsync(); Assert.True(worker.Token.IsCancellationRequested); Assert.False(closing.IsCompleted);
        worker.Completion.SetResult(new("Paused", Message: "Paused")); await job; await closing;
        Assert.False(model.IsBusy); Assert.True(model.CanPlan); Assert.Equal("Dependency check paused.", model.PlanStatus);
    }

    [Fact]
    public async Task SavedPlanLoadsButLanguageAndSourceChangesInvalidateItsPresentation()
    {
        using var f = new PlanFixture(); var plan = f.Run(); var model = await Model(f, new PendingWorker());
        Assert.True(model.CanOpenPlan); Assert.EndsWith(plan.Summary!.RelativePath.Replace('/', Path.DirectorySeparatorChar), model.PlanPath);
        model.SelectedLanguage = "english"; Assert.False(model.CanOpenPlan);
        model.SelectedLanguage = "English(US)"; Assert.True(model.CanOpenPlan);
        model.Configure(Path.Combine(f.Files.Root, "different"), string.Empty, true);
        Assert.False(model.CanPlan); Assert.False(model.CanOpenPlan); Assert.Empty(model.Languages);
    }

    private static async Task<CacheViewModel> Model(PlanFixture f, PendingWorker worker)
    {
        var settings = new LauncherSettingsStore(Path.Combine(f.Files.Destination, "settings.json"));
        await settings.SaveCacheDirectoryAsync(f.Files.Cache);
        var model = new CacheViewModel(settings, worker); model.Configure(f.Files.Root, string.Empty, true);
        await model.RestoreAsync(); return model;
    }
    private sealed class PendingWorker : IIndexWorker, IPlanWorker
    {
        public int Calls { get; private set; }
        public CancellationToken Token { get; private set; }
        public TaskCompletionSource<PlanResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<PlanResult> PlanAsync(PlanRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation)
        { Calls++; Token = cancellation; return Completion.Task; }
        public Task<IndexResult> RunAsync(IndexRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation)
            => throw new InvalidOperationException("Indexing must be disabled while planning.");
    }
}
