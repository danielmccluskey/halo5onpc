using System.IO;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Storage;
using H5SoloLauncher.Services;
using Xunit;

namespace H5SoloLauncher.Tests;

public sealed class IndexWorkerTests
{
    private static IndexWorkerClient Client() => new(Environment.GetEnvironmentVariable("H5SOLO_TEST_WORKER"));
    private sealed class Progress(Action<IndexProgress> callback) : IProgress<IndexProgress>
    { public void Report(IndexProgress value) => callback(value); }

    [Fact]
    public async Task RealWorkerIndexesOverPipeAndExitsBeforeReturning()
    {
        using var f = new IndexFixture(); CacheFolders.Select(f.Destination, f.Root, null);
        var events = new List<IndexProgress>();
        var client = Client();
        var result = await client.RunAsync(new(f.Root, f.Cache), new Progress(events.Add), default);
        Assert.Equal("Indexed", result.State); Assert.Equal(2, result.Summary!.Modules);
        Assert.NotEmpty(events);
        using var lease = new FileStream(Path.Combine(f.Cache, "index.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task RealWorkerHonoursPauseAndNextProcessResumes()
    {
        using var f = new IndexFixture(); CacheFolders.Select(f.Destination, f.Root, null);
        for (var i = 0; i < 200; i++) f.Write($"__cms__/pause-fixture/{i:D4}.mapinfo", [1, 2, 3]);
        using var stop = new CancellationTokenSource();
        var client = Client();
        var paused = await client.RunAsync(new(f.Root, f.Cache), new Progress(_ => stop.Cancel()), stop.Token);
        Assert.Equal("Paused", paused.State);
        var resumed = await client.RunAsync(new(f.Root, f.Cache), new Progress(_ => { }), default);
        Assert.Equal("Indexed", resumed.State);
    }

    [Fact]
    public async Task MissingWorkerProducesActionableError()
    {
        using var f = new IndexFixture();
        var result = await new IndexWorkerClient(Path.Combine(f.Destination, "missing.exe"))
            .RunAsync(new(f.Root, f.Cache), new Progress(_ => { }), default);
        Assert.Equal("WORKER_MISSING", result.Code); Assert.Contains("published launcher files", result.Message);
    }
}
