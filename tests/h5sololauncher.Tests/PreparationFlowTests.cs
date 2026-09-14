using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Preparation;
using H5SoloLauncher.Core.Runtime;
using H5SoloLauncher.Services;
using H5SoloLauncher.ViewModels;
using Xunit;

namespace H5SoloLauncher.Tests;

public sealed class PreparationFlowTests
{
    private static async Task<CacheViewModel> Model(IndexFixture fixture, Worker worker)
    {
        var model=new CacheViewModel(new LauncherSettingsStore(Path.Combine(fixture.Destination,"settings.json")),worker);
        model.Configure(fixture.Root,string.Empty,true,"installed-test-package");
        await model.ChooseAsync(fixture.Destination);
        return model;
    }

    [Fact]
    public async Task OneActionPreparesPlaysAndMonitorsWithoutDuplicateJobs()
    {
        using var fixture=new IndexFixture();var worker=new Worker();var model=await Model(fixture,worker);
        var job=model.PrimaryAsync();await model.PrimaryAsync();
        Assert.Equal(new[]{"prepare"},worker.Calls);Assert.True(model.IsWorking);Assert.False(model.CanChoose);
        worker.Prepared.SetResult(new("Ready","Ready","Prepared",PreparedId:new string('A',64)));
        await worker.StartedPlay.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(fixture.Root,worker.PlayRequest!.Locations.SourceRoot);Assert.Equal(fixture.Cache,worker.PlayRequest.Locations.CacheRoot);
        worker.Played.SetResult(new("Ready","Solo","Choose a mission",123,ProcessCreated:456));
        await worker.StartedWatch.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(model.IsWatching);Assert.False(model.IsWorking);Assert.Equal("Stop monitoring",model.PauseLabel);
        Assert.Equal(123,worker.WatchRequest!.ProcessId);Assert.Equal(456,worker.WatchRequest.ProcessCreated);
        worker.Watched.SetResult(new("Exited","Game","Forge closed"));await job;
        Assert.Equal(new[]{"prepare","play","watch"},worker.Calls);
        Assert.Equal("Play",model.PrimaryLabel);Assert.True(model.CanPrimary);Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task PauseDuringPreparationNeverStartsForgeEvenIfWorkerJustFinished()
    {
        using var fixture=new IndexFixture();var worker=new Worker();var model=await Model(fixture,worker);
        var job=model.PrimaryAsync();var closing=model.PauseAndWaitAsync();
        Assert.True(worker.Token.IsCancellationRequested);
        worker.Prepared.SetResult(new("Ready","Ready","Prepared",PreparedId:new string('B',64)));
        await closing;await job;
        Assert.Equal(new[]{"prepare"},worker.Calls);Assert.Equal("Play",model.PrimaryLabel);Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task ClosingDuringMonitoringCancelsOnlyTheMonitor()
    {
        using var fixture=new IndexFixture();var worker=new Worker();var model=await Model(fixture,worker);
        worker.Prepared.SetResult(new("Ready","Ready","Prepared",PreparedId:new string('C',64)));
        worker.Played.SetResult(new("Ready","Solo","Ready",123,ProcessCreated:456));
        var job=model.PrimaryAsync();await worker.StartedWatch.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var closing=model.PauseAndWaitAsync();Assert.True(worker.Token.IsCancellationRequested);Assert.False(closing.IsCompleted);
        worker.Watched.SetCanceled(worker.Token);await closing;await job;
        Assert.False(model.IsBusy);Assert.Contains("monitoring stopped",model.PreparationMessage);
        Assert.DoesNotContain("CAMPAIGN_MONITOR_FAILED",model.PlayDetails);
    }

    [Fact]
    public async Task PausingStartupDoesNotReportAFailureOrBeginMonitoring()
    {
        using var fixture=new IndexFixture();var worker=new Worker();var model=await Model(fixture,worker);
        worker.Prepared.SetResult(new("Ready","Ready","Prepared",PreparedId:new string('D',64)));
        var job=model.PrimaryAsync();await worker.StartedPlay.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var pausing=model.PauseAndWaitAsync();Assert.True(worker.Token.IsCancellationRequested);
        worker.Played.SetCanceled(worker.Token);await pausing;await job;
        Assert.Equal(new[]{"prepare","play"},worker.Calls);Assert.False(model.IsBusy);
        Assert.Contains("Startup paused",model.PreparationMessage);Assert.DoesNotContain("CAMPAIGN_PLAY_FAILED",model.PlayDetails);
    }

    [Fact]
    public async Task FailedPreparationKeepsDiagnosticAndDoesNotPlay()
    {
        using var fixture=new IndexFixture();var worker=new Worker();var model=await Model(fixture,worker);
        worker.Prepared.SetResult(new("Failed","Audio","Missing bank","SOURCE_BANK_MISSING","bank detail"));
        await model.PrimaryAsync();Assert.Equal(new[]{"prepare"},worker.Calls);
        Assert.Contains("SOURCE_BANK_MISSING",model.PreparationDetails);Assert.Contains("bank detail",model.Details);
        Assert.True(model.CanPrimary);Assert.False(model.IsBusy);
    }

    private sealed class Worker:IIndexWorker,IPreparationWorker,ICampaignPlayWorker,ICampaignWatchWorker
    {
        public List<string> Calls {get;}=[];
        public CancellationToken Token {get;private set;}
        public CampaignPlayRequest? PlayRequest {get;private set;}
        public CampaignWatchRequest? WatchRequest {get;private set;}
        public TaskCompletionSource<PreparationResult> Prepared {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<CampaignPlayResult> Played {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<CampaignPlayResult> Watched {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StartedPlay {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StartedWatch {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IndexResult> RunAsync(IndexRequest request,IProgress<IndexProgress> progress,CancellationToken cancellation)=>throw new NotSupportedException();
        public Task<PreparationResult> PrepareAsync(PrepareRequest request,IProgress<IndexProgress> progress,CancellationToken cancellation)
        {Calls.Add("prepare");Token=cancellation;return Prepared.Task;}
        public Task<CampaignPlayResult> PlayAsync(CampaignPlayRequest request,IProgress<IndexProgress> progress,CancellationToken cancellation)
        {Calls.Add("play");Token=cancellation;PlayRequest=request;StartedPlay.SetResult();return Played.Task;}
        public Task<CampaignPlayResult> WatchAsync(CampaignWatchRequest request,IProgress<IndexProgress> progress,CancellationToken cancellation)
        {Calls.Add("watch");Token=cancellation;WatchRequest=request;StartedWatch.SetResult();return Watched.Task;}
    }
}
