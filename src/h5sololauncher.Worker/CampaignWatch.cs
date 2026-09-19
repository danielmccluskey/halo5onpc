using System.Diagnostics;
using System.Text.Json;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Runtime;
namespace H5SoloLauncher.Worker;
internal static class CampaignWatch
{
    internal static string Check(CampaignPlayRequest request,PreparedCampaign ready,CancellationToken cancellation)
    {
        var input=request.Locations;
        string Config(string category,string id)=>PreparedCampaignStore.ConfigPath(input.CacheRoot,category,id);
        var content=CampaignContentRuntime.Check(input.ForgeRoot,input.PackageFullName,cancellation);
        var renderer=CampaignRendererRuntime.Run(input.ForgeRoot,input.PackageFullName,false,cancellation);
        var deformation=CampaignDeformationRuntime.Run(input.ForgeRoot,input.PackageFullName,false,cancellation);
        var ui=CampaignUiRuntime.Run(input.ForgeRoot,input.PackageFullName,Config("ui-residency",ready.UiConfigId),ready.UiConfigId,false,cancellation);
        var movie=CampaignMovieRuntime.Run(input.ForgeRoot,input.PackageFullName,Config("movie-config",ready.MovieConfigId),ready.MovieConfigId,false,cancellation);
        var completion=CampaignCompletionRuntime.Run(input.ForgeRoot,input.PackageFullName,Config("completion-config",ready.CompletionConfigId),ready.CompletionConfigId,0,cancellation);
        var audio=CampaignAudioRuntime.Run(input.ForgeRoot,input.PackageFullName,false,cancellation);
        var controls=CampaignControlsRuntime.Run(input.ForgeRoot,input.PackageFullName,false,cancellation);
        var display=CampaignDisplayRuntime.Run(input.ForgeRoot,input.PackageFullName,Config("display-config",ready.DisplayConfigId),ready.DisplayConfigId,false,cancellation);
        if(content!=4 || renderer.Phase!=1 || deformation.Phase!=1 || deformation.Errors!=0 || ui.Phase!=1 || ui.Rejected!=0 || movie.Phase!=2 || completion.Phase==0 || audio.Phase!=2 || audio.Result!=1 || controls.Phase!=1 || display.Phase!=2)
            throw new CacheException("CAMPAIGN_SESSION_INCOMPLETE","This Forge session has incomplete campaign support. Close Forge and use Play again.");
        return JsonSerializer.Serialize(new{content,renderer,deformation,ui,movie,completion,audio,controls,display});
    }
    public static CampaignPlayResult Run(CampaignWatchRequest request,IProgress<IndexProgress> progress,CancellationToken cancellation)
    {
        var last="";Process? process=null;
        try{
            var ready=PlayableCache.Resolve(PlayableCache.Read(request.Campaign.Locations.CacheRoot,request.Campaign.Locations.ForgeRoot,request.Campaign.Locations.PackageFullName,request.Campaign.PreparedId)).Prepared;
            var target=PackageTarget.Forge(request.Campaign.Locations.ForgeRoot,request.Campaign.Locations.PackageFullName);
            var expected=new H5SoloLauncher.Core.Forge.RunningForge(request.ProcessId,request.ProcessCreated);
            if(target.Find()!=expected)return new("Exited","Game","Forge has closed. Your prepared cache is ready for next time.");
            // Individual setup calls take short lifecycle leases. Keep one outer
            // lease for the live campaign session so PLM cannot suspend Forge
            // between the monitor's five-second health checks.
            using var lifecycle=PackageDebugLease.Acquire(request.Campaign.Locations.PackageFullName);
            process=Process.GetProcessById(expected.ProcessId);
            // Retain a handle while Forge is alive so Windows preserves its exit code.
            // GetProcessById alone only associates this object with a PID.
            try{_ = process.SafeHandle;}
            catch(InvalidOperationException) when(process.HasExited){}
            while(!process.HasExited){
                cancellation.ThrowIfCancellationRequested();if(target.Find()!=expected)break;
                string observed;
                try{observed=Check(request.Campaign,ready,cancellation);}
                catch(Exception error) when(error is not OperationCanceledException && process.HasExited){break;}
                if(last!=observed){last=observed;PlayLog.Write(new{eventName="Runtime",request.ProcessId,status=observed});}
                progress.Report(new("Running in Forge",0,0));
                if(cancellation.WaitHandle.WaitOne(5000))cancellation.ThrowIfCancellationRequested();
                process.Refresh();
            }
            int? exit=null;
            if(process.HasExited){
                try{exit=process.ExitCode;}
                catch(InvalidOperationException){} // Forge may have closed before we acquired its handle.
            }
            PlayLog.Write(new{eventName="Forge exited",request.ProcessId,exit});
            if(exit is null)return new("Exited","Game","Forge is no longer being monitored. Its exit code is unavailable; the launcher cannot determine whether it crashed.",Code:"FORGE_EXIT_CODE_UNAVAILABLE",Details:$"Play log: {PlayLog.PathName}\n{last}");
            return exit==0?new("Exited","Game","Forge has closed. Your prepared cache is ready for next time.",Details:$"Play log: {PlayLog.PathName}\n{last}"):
                new("Failed","Game","Forge stopped unexpectedly. Your cache was kept. Copy details before trying Play again.",Code:"FORGE_EXITED_UNEXPECTEDLY",Details:$"Exit: {exit:x8}\nPlay log: {PlayLog.PathName}\n{last}");
        }catch(OperationCanceledException){return new("Detached","Game","Game monitoring stopped. Forge can keep running.");}
        catch(Exception error){PlayLog.Write(new{eventName="Runtime error",error=error.ToString()});return new("Failed","Game",error.Message,request.ProcessId,error is CacheException known?known.Code:"CAMPAIGN_RUNTIME_FAILED",$"Play log: {PlayLog.PathName}\n{last}\n{error}");}
        finally{process?.Dispose();}
    }
}
