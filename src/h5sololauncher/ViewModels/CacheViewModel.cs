using System.ComponentModel;
using System.IO;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Storage;
using H5SoloLauncher.Services;

namespace H5SoloLauncher.ViewModels;

public sealed partial class CacheViewModel(ILauncherSettingsStore settings, IIndexWorker worker) : INotifyPropertyChanged
{
    private string source = string.Empty, forge = string.Empty;
    private bool contextReady, cacheReady;
    private CancellationTokenSource? stop;
    private Task? active;
    private int pauseRevision;
    private IndexResult? result;
    private IndexSummary? summary;
    private IndexProgress? progress;
    private string notice = string.Empty;
    public string Directory { get; private set; } = string.Empty;
    public long FreeBytes { get; private set; }
    public bool IsBusy { get; private set; }
    public bool IsIndexing => stop is not null;
    public bool IsStopping { get; private set; }
    public bool HasDirectory => Directory.Length > 0;
    public bool CanChoose => cacheReady && !IsBusy;
    public bool CanIndex => contextReady && CanChoose && HasDirectory;
    public bool CanPause => IsIndexing && !IsStopping;
    public bool IsIndeterminate => IsBusy && (progress?.Total ?? 0) == 0;
    public double Percent => progress?.Total > 0 ? 100.0 * progress.Completed / progress.Total : 0;
    public string Space => HasDirectory ? $"{FreeBytes / (1024.0 * 1024 * 1024):N1} GiB available on the cache drive." : string.Empty;
    public string Status => IsStopping ? "Pausing after the current checkpoint…" : IsBusy ? progress?.Stage ?? "Checking cache folder…" : result?.State switch
    {
        "Failed" => "The index needs attention.",
        "Paused" => "Indexing paused.",
        _ => summary?.State switch
        {
            "Indexed" when preparedId is null && contextReady && !summary.SourceRoot.Equals(source, StringComparison.OrdinalIgnoreCase) => "Source folder changed. Index again to refresh the cache.",
            "Indexed" => preparedId is null?"Dump indexed.":"Campaign cache prepared.",
            "Paused" or "Indexing" => "Indexing was interrupted. Resume when ready.",
            "Failed" => "Indexing needs to resume.",
            _ => HasDirectory ? "Cache folder ready." : "Choose where to keep the cache."
        }
    };
    public string Message => notice.Length > 0 ? notice : IsIndexing
        ? progress?.Total > 0 ? $"{progress.Completed:N0} / {progress.Total:N0} {(IsBuildingInputs ? "items" : "files")}; {progress.Reused:N0} checkpoints reused.\n{progress.File}"
            : IsPlanning ? $"{progress?.Completed ?? 0:N0} tags checked; {progress?.Reused ?? 0:N0} analyses reused.\n{progress?.File}" : IsPreparingForge ? progress?.Stage ?? "Preparing Forge data…" : "Finding the files to index…"
        : result?.Message ?? (summary is null ? "The launcher keeps its index and generated game files in your chosen cache folder."
            : $"{summary.Modules:N0} modules, {summary.Entries:N0} item records and {summary.AudioEntries:N0} audio records.\n{summary.MetadataBytes / (1024.0 * 1024):N1} MiB of source metadata indexed." +
              (summary.UnresolvedPayloads > 0 ? $"\n{summary.UnresolvedPayloads:N0} source payload references were retained for dependency analysis." : string.Empty));
    public string Details => $"Cache folder: {Directory}\nCache state: {result?.State ?? summary?.State ?? "Not indexed"}\n" +
        (HasDirectory ? $"Log file: {Path.Combine(Directory, "logs", "index.log")}\n" : string.Empty) +
        $"Source: {source}\n{Space}\n{Message}\n" + (summary is null ? string.Empty : $"Index source: {summary.SourceRoot}\nPackage: {summary.PackageVersion}\nBlocks: {summary.Blocks:N0}\nCompleted: {summary.CompletedUtc}\n") +
        (result?.Code is null ? string.Empty : $"Error: {result.Code}\n{result.Details}") + "\n" + PreparationDetails + "\n" + PlayDetails + "\n" + PlanDetails + "\n" + ForgeDetails + "\n" + InputDetails;

    public void Configure(string sourceDirectory, string forgeDirectory, bool ready, string packageFullName = "", bool? canUseCache = null)
    {
        var changed=!source.Equals(sourceDirectory,StringComparison.OrdinalIgnoreCase) || !forge.Equals(forgeDirectory,StringComparison.OrdinalIgnoreCase) || forgePackage!=packageFullName;
        var playableReady=canUseCache ?? ready;
        var becameReady=playableReady && !cacheReady;
        if(changed){ClearPlan();ClearPrepared();}
        source = sourceDirectory; forge = forgeDirectory; forgePackage = packageFullName; contextReady = ready; cacheReady=playableReady; Refresh();
        if(playableReady && (changed || becameReady))RefreshPrepared();
    }

    public async Task RestoreAsync()
    {
        if (IsBusy) return;
        IsBusy = true; Refresh();
        try
        {
            KeepRebuildData = await settings.LoadKeepRebuildDataAsync();
            Directory = await settings.LoadCacheDirectoryAsync() ?? string.Empty;
            if (HasDirectory)
            {
                var location = await Task.Run(() => CacheFolders.OpenExisting(Directory, forge));
                FreeBytes = location.FreeBytes;
                summary = await ReadOptionalSummaryAsync(Directory);
                await LoadPlanAsync();
                await LoadPreparedAsync();
            }
        }
        catch (Exception exception) { Fail(exception); }
        finally { IsBusy = false; Refresh(); }
    }

    public async Task ChooseAsync(string selected)
    {
        if (!CanChoose) return;
        IsBusy = true; notice = string.Empty; result = null; Refresh();
        try
        {
            var location = await Task.Run(() => contextReady && !File.Exists(Path.Combine(selected,"cache.json")) && !File.Exists(Path.Combine(selected,CacheFolders.FolderName,"cache.json"))
                ? CacheFolders.Select(selected, source, forge) : CacheFolders.OpenExisting(selected, forge));
            var indexed = await ReadOptionalSummaryAsync(location.Root);
            Directory = location.Root; FreeBytes = location.FreeBytes; summary = indexed;
            ClearPlan();ClearPrepared();await LoadPlanAsync();await LoadPreparedAsync();
            try { await settings.SaveCacheDirectoryAsync(Directory); }
            catch (Exception exception) { notice = "The cache folder is usable, but its setting could not be saved. " + exception.Message; }
        }
        catch (Exception exception) { Fail(exception); }
        finally { IsBusy = false; Refresh(); }
    }

    public Task IndexAsync()
    {
        if (!CanIndex) return Task.CompletedTask;
        active = IndexCoreAsync(); return active;
    }

    private async Task<IndexSummary?> ReadOptionalSummaryAsync(string root)
    {
        if (!contextReady) return null;
        try { return await Task.Run(() => IndexCatalog.ReadSummary(root)); }
        catch (Exception error)
        {
            // Conversion diagnostics are optional for an existing playable cache.
            notice = "The build index could not be read. Playback will check the game cache separately. " + error.Message;
            return null;
        }
    }

    private async Task IndexCoreAsync()
    {
        stop = new(); IsBusy = true; IsStopping = false; notice = string.Empty; result = null; progress = null; ClearPlan(); Refresh();
        try
        {
            result = await worker.RunAsync(new(source, Directory, forge), new Progress<IndexProgress>(value => { progress = value; Refresh(); }), stop.Token);
            summary = result.Summary ?? await Task.Run(() => IndexCatalog.ReadSummary(Directory));
            await LoadPlanAsync();
            FreeBytes = new DriveInfo(Path.GetPathRoot(Directory)!).AvailableFreeSpace;
        }
        catch (Exception exception) { Fail(exception); }
        finally { stop.Dispose(); stop = null; IsBusy = false; IsStopping = false; progress = null; Refresh(); }
    }

    public void Pause() { if (stop is null) return; pauseRevision++; IsStopping = true; stop.Cancel(); Refresh(); }
    public async Task PauseAndWaitAsync() { Pause(); if (active is not null) await active; await readyRefresh; }
    private void Fail(Exception exception) => result = new("Failed", Code: exception is CacheException known ? known.Code : "CACHE_FAILED",
        Message: exception.Message, Details: exception.ToString());
    private void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    public event PropertyChangedEventHandler? PropertyChanged;
}
