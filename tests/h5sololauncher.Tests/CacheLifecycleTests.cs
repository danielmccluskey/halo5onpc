using System.IO;
using System.Text.Json;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Storage;
using H5SoloLauncher.Services;
using H5SoloLauncher.ViewModels;
using Xunit;

namespace H5SoloLauncher.Tests;

public sealed class CacheLifecycleTests
{
    [Fact]
    public void SelectionOwnsOnlyItsChildAndCanReopenIt()
    {
        using var f = new IndexFixture();
        var unrelated = Path.Combine(f.Destination, "keep.txt"); File.WriteAllText(unrelated, "keep");
        var cache = CacheFolders.Select(f.Destination, f.Root, null);
        Assert.Equal(f.Cache, cache.Root); Assert.True(cache.FreeBytes > 0);
        Assert.Equal(cache.Id, CacheFolders.Select(f.Cache, f.Root, null).Id);
        Assert.Equal("keep", File.ReadAllText(unrelated));
        Assert.Empty(Directory.GetFiles(f.Cache, ".probe-*"));
    }

    [Fact]
    public void SelectionRefusesUnownedContentAndSourceOverlap()
    {
        using var f = new IndexFixture(); Directory.CreateDirectory(f.Cache);
        var existing = Path.Combine(f.Cache, "existing.txt"); File.WriteAllText(existing, "keep");
        Assert.Equal("CACHE_NOT_OWNED", Assert.Throws<CacheException>(() => CacheFolders.Select(f.Destination, f.Root, null)).Code);
        Assert.False(File.Exists(Path.Combine(f.Cache, "cache.json")));
        Assert.Throws<CacheException>(() => CacheFolders.Select(f.Root, f.Root, null));
        Assert.Equal("keep", File.ReadAllText(existing));
    }

    [Fact]
    public void CacheCannotContainTheSourceOrBePlacedInsideForge()
    {
        using var f = new IndexFixture();
        Assert.Throws<CacheException>(() => SafePaths.Separate(Path.GetDirectoryName(f.Root)!, f.Root, null));
        Assert.Throws<CacheException>(() => CacheFolders.Select(f.Destination, f.Root, f.Destination));
        Assert.Throws<CacheException>(() => SafePaths.Child(f.Destination, "../escape"));
    }

    [Fact]
    public void RootDiscoveryAcceptsPackageAndSinglePackageParentButRejectsAmbiguity()
    {
        using var f = new IndexFixture(); var package = Path.GetDirectoryName(f.Root)!;
        var parent = Path.GetDirectoryName(package)!;
        Assert.Equal(f.Root, SourceDiscovery.ResolveRoot(package));
        Assert.Equal(f.Root, SourceDiscovery.ResolveRoot(parent));
        var second = Path.Combine(parent, "another package", "Mount"); Directory.CreateDirectory(second);
        File.Copy(Path.Combine(f.Root, "AppxManifest.xml"), Path.Combine(second, "AppxManifest.xml"));
        Assert.Equal("DUMP_AMBIGUOUS", Assert.Throws<CacheException>(() => SourceDiscovery.ResolveRoot(parent)).Code);
    }

    [Fact]
    public async Task SettingsMigrationPreservesBothIndependentChoices()
    {
        using var f = new IndexFixture(); var path = Path.Combine(f.Destination, "settings.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { SchemaVersion = 1, CampaignDirectory = f.Root }));
        var store = new LauncherSettingsStore(path);
        Assert.Null(await store.LoadCacheDirectoryAsync());
        await store.SaveCacheDirectoryAsync(f.Cache);
        Assert.Equal(f.Root, await store.LoadCampaignDirectoryAsync());
        await store.SaveCampaignDirectoryAsync(f.Destination);
        Assert.Equal(f.Cache, await store.LoadCacheDirectoryAsync());
        Assert.Equal(f.Destination, await store.LoadCampaignDirectoryAsync());
    }

    [Fact]
    public async Task ViewModelPreventsDuplicateJobsAndWaitsForPause()
    {
        using var f = new IndexFixture(); var worker = new PendingWorker();
        var store = new LauncherSettingsStore(Path.Combine(f.Destination, "settings.json"));
        var model = new CacheViewModel(store, worker);
        Assert.False(model.CanChoose); model.Configure(f.Root, string.Empty, true);
        await model.ChooseAsync(f.Destination); Assert.True(model.CanIndex);
        var job = model.IndexAsync(); await model.IndexAsync();
        Assert.Equal(1, worker.Calls); Assert.True(model.IsBusy); Assert.False(model.CanChoose);
        var pausing = model.PauseAndWaitAsync(); Assert.True(worker.Token.IsCancellationRequested);
        Assert.False(pausing.IsCompleted); Assert.False(model.CanPause);
        worker.Done.SetResult(new("Paused", Message: "Paused")); await pausing; await job;
        Assert.False(model.IsBusy); Assert.True(model.CanIndex); Assert.Equal("Indexing paused.", model.Status);
    }

    [Fact]
    public async Task RestoringMissingCacheKeepsItsPathAndOffersRecovery()
    {
        using var f = new IndexFixture(); var store = new LauncherSettingsStore(Path.Combine(f.Destination, "settings.json"));
        await store.SaveCacheDirectoryAsync(f.Cache);
        var worker = new PendingWorker(); var model = new CacheViewModel(store, worker);
        model.Configure(f.Root, string.Empty, true); await model.RestoreAsync();
        Assert.Equal(f.Cache, model.Directory); Assert.True(model.CanChoose);
        Assert.Equal("The index needs attention.", model.Status); Assert.Equal(0, worker.Calls);
    }

    private sealed class PendingWorker : IIndexWorker
    {
        public int Calls { get; private set; }
        public CancellationToken Token { get; private set; }
        public TaskCompletionSource<IndexResult> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IndexResult> RunAsync(IndexRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation)
        { Calls++; Token = cancellation; return Done.Task; }
    }
}
