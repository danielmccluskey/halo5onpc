using System.IO;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Preparation;
using H5SoloLauncher.Core.Storage;
using H5SoloLauncher.Core.Runtime;

namespace H5SoloLauncher.ViewModels;

public sealed partial class CacheViewModel
{
    private PreparationResult? preparation;
    private CampaignPlayResult? playResult;
    private string? preparedId;
    private int readyRevision;
    private Task readyRefresh=Task.CompletedTask;
    private void RefreshPrepared(){var next=LoadPreparedAsync();readyRefresh=readyRefresh.IsCompleted?next:Task.WhenAll(readyRefresh,next);}
    public bool IsPlaying { get; private set; }
    public bool IsWatching { get; private set; }
    public bool IsWorking => IsBusy && !IsWatching;
    public bool IsPreparing { get; private set; }
    public bool CanPrepare => contextReady && CanChoose && HasDirectory && forgePackage.Length > 0 && worker is IPreparationWorker;
    public bool CanPrimary => CanChoose && HasDirectory && forgePackage.Length > 0 && (preparedId is not null || CanPrepare) && worker is ICampaignPlayWorker;
    public string PrimaryLabel => IsWatching?"Running":preparedId is null ? "Prepare & play" : "Play";
    public string PauseLabel => IsWatching?"Stop monitoring":IsPlaying ? "Stop waiting" : "Pause";
    public string PreparationStatus => IsStopping ? "Stopping after the current operation…" : IsWatching?"Running in Forge.":IsPreparing || IsPlaying ? progress?.Stage ?? "Preparing campaign…" : playResult is not null ? playResult.State switch {"Ready"=>"Solo is ready.","Exited"=>"Ready to play again.","Detached"=>"Game monitoring stopped.",_=>"Startup needs attention."} : preparation?.State switch
    {
        "Paused" => "Preparation paused.", "Failed" => "Preparation needs attention.",
        "Ready" => "Ready to play.",
        _ => preparedId is null?"Prepare Osiris and Blue Team.":"Ready to play."
    };
    public string PreparationMessage => IsPreparing || IsPlaying
        ? (progress?.Total > 0 ? $"{progress.Completed:N0} / {progress.Total:N0}\n{progress.File}" : progress?.File ?? "Completed stages are kept if you pause.")
        : playResult?.Message ?? preparation?.Message ?? (preparedId is null ? "Choose an existing playable cache, or choose a dump and cache destination to build one." : "Your cache is ready. No dump is needed to play. Play starts Forge and opens Solo automatically.");
    public string PreparationDetails => preparation is null ? string.Empty : $"Preparation: {preparation.State}\nStage: {preparation.Phase}\n{preparation.Message}\n" +
        $"Plan: {preparation.PlanId}\nNative modules: {preparation.NativeModuleId}\nNative schemas: {preparation.NativeSchemaId}\nEffective plan: {preparation.EffectivePlanId}\n" +
        $"Selected tags: {preparation.Tags}\nUnresolved identities: {preparation.Unresolved}\nPrepared cache: {preparation.PreparedId}\nError: {preparation.Code}\n{preparation.Details}\n";
    public string PlayDetails => $"Prepared cache: {preparedId}\n"+(playResult is null ? "" : $"Campaign startup: {playResult.State}\nStage: {playResult.Phase}\n{playResult.Message}\nProcess: {playResult.ProcessId}\nError: {playResult.Code}\n{playResult.Details}\n");
    private PrepareRequest Locations()=>new(source,Directory,forge,forgePackage,string.IsNullOrEmpty(SelectedLanguage)?"English(US)":SelectedLanguage);
    private void ClearPrepared(){readyRevision++;preparation=null;playResult=null;preparedId=null;}
    private async Task LoadPreparedAsync()
    {
        if(!cacheReady || !HasDirectory || IsPreparing || IsPlaying || (!File.Exists(Path.Combine(Directory,"prepared.json")) && !File.Exists(Path.Combine(Directory,"playable.json"))))return;
        var revision=++readyRevision;var locations=Locations();
        try
        {
            var cache=await Task.Run(()=>PlayableCache.Open(locations.CacheRoot,locations.ForgeRoot,locations.PackageFullName));
            if(revision!=readyRevision || locations!=Locations() || IsPreparing || IsPlaying)return;
            preparedId=cache.Id;selectedLanguage=cache.Manifest.Language;Languages=[selectedLanguage];preparation=null;Refresh();
        }
        catch(Exception error)
        {
            if(revision!=readyRevision)return;
            preparedId=null;preparation=new("Failed","Checking cache",error.Message,error is CacheException known?known.Code:"CACHE_CHECK_FAILED",error.ToString());Refresh();
        }
    }

    public Task PrimaryAsync()
    {
        if(!CanPrimary)return Task.CompletedTask;
        active=PrepareAndPlayAsync();return active;
    }
    private async Task PrepareAndPlayAsync()
    {
        var revision=pauseRevision;
        if(preparedId is null)await PrepareCoreAsync();
        if(revision!=pauseRevision)return;
        if(preparedId is not null && CanPrimary)await PlayCoreAsync();
        if(revision!=pauseRevision)return;
        if(preparedId is not null && playResult is {State:"Ready",ProcessId:not null,ProcessCreated:not null} && worker is ICampaignWatchWorker watcher)
            await WatchCoreAsync(watcher,new(new(Locations(),preparedId),playResult.ProcessId.Value,playResult.ProcessCreated.Value));
    }
    private async Task WatchCoreAsync(ICampaignWatchWorker watcher,CampaignWatchRequest request)
    {
        stop=new();IsBusy=true;IsWatching=true;Refresh();
        try{
            var result=await watcher.WatchAsync(request,new Progress<IndexProgress>(_=>{}),stop.Token);
            playResult=result with {Details=PlayDetails+"\n"+result.Details};
        }catch(OperationCanceledException){playResult=new("Detached","Game","Game monitoring stopped. Forge can keep running.",Details:PlayDetails);}
        catch(Exception error){playResult=new("Failed","Game",error.Message,Code:"CAMPAIGN_MONITOR_FAILED",Details:PlayDetails+"\n"+error);}
        finally{stop.Dispose();stop=null;IsBusy=false;IsWatching=false;IsStopping=false;Refresh();}
    }
    private async Task PlayCoreAsync()
    {
        if(preparedId is null || worker is not ICampaignPlayWorker player)return;
        stop=new();IsBusy=true;IsPlaying=true;IsStopping=false;playResult=null;progress=null;Refresh();
        try{playResult=await player.PlayAsync(new(Locations(),preparedId),new Progress<IndexProgress>(value=>{progress=value;Refresh();}),stop.Token);}
        catch(OperationCanceledException){playResult=new("Paused","Startup","Startup paused. Close Forge before trying again if setup was interrupted.");}
        catch(Exception error){playResult=new("Failed","Startup",error.Message,Code:error is CacheException known?known.Code:"CAMPAIGN_PLAY_FAILED",Details:error.ToString());}
        finally{stop.Dispose();stop=null;IsBusy=false;IsPlaying=false;IsStopping=false;progress=null;Refresh();}
    }
    public Task PrepareAsync()
    {
        if (!CanPrepare) return Task.CompletedTask;
        active = PrepareCoreAsync(); return active;
    }
    private async Task PrepareCoreAsync()
    {
        ClearPrepared();stop = new(); IsBusy = true; IsPreparing = true; IsStopping = false; notice = string.Empty; preparation = null; progress = null; Refresh();
        try
        {
            preparation = await ((IPreparationWorker)worker).PrepareAsync(Locations(),
                new Progress<IndexProgress>(value => { progress = value; Refresh(); }), stop.Token);
            if(preparation.State=="Ready")preparedId=preparation.PreparedId;
            summary = await Task.Run(() => IndexCatalog.ReadSummary(Directory)); await LoadPlanAsync();
            FreeBytes = new DriveInfo(Path.GetPathRoot(Directory)!).AvailableFreeSpace;
        }
        catch (Exception e) { preparedId=null;preparation = new("Failed", "Preparation", e.Message, e is CacheException known ? known.Code : "PREPARATION_FAILED", e.ToString()); }
        finally { stop.Dispose(); stop = null; IsBusy = false; IsPreparing = false; IsStopping = false; progress = null; Refresh(); }
    }
}
