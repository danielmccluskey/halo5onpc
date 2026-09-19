using System.Text;
using System.Text.Json;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Preparation;
using H5SoloLauncher.Core.Runtime;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Worker;
internal static class CampaignPlayback
{
    public static CampaignPlayResult Run(CampaignPlayRequest request,IProgress<IndexProgress> progress,CancellationToken cancellation)
    {
        var phase="Checking the prepared cache";int? pid=null;var details=new StringBuilder();
        void Begin(string next){cancellation.ThrowIfCancellationRequested();phase=next;progress.Report(new(next,0,0));details.AppendLine(next);PlayLog.Write(new{phase,pid});}
        void Require(string state,string expected,string? code,string? message,string? diagnostic=null){if(state=="Paused")throw new OperationCanceledException(cancellation);if(state!=expected)throw new PlayFailure(code??"CAMPAIGN_PLAY_FAILED",message??"Campaign startup could not finish.",diagnostic);}
        void Record<T>(T value){details.AppendLine(JsonSerializer.Serialize(value));PlayLog.Write(value);}
        try
        {
            Begin(phase);var locations=request.Locations;
            Begin("Reading the playable manifest");var cache=PlayableCache.Read(locations.CacheRoot,locations.ForgeRoot,locations.PackageFullName,request.PreparedId);
            Begin("Checking cache maintenance");
            details.AppendLine(CacheMaintenance.Pending(cache.Root,locations.ForgeRoot,locations.PackageFullName));
            Begin("Opening the cache session");
            using var lease=new FileStream(SafePaths.Child(cache.Root,"index.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            Begin("Building runtime configuration");var runtime=PlayableCache.Resolve(cache);var ready=runtime.Prepared;
            Begin("Allowing Forge to read the cache");ForgeCacheAccess.GrantRead(new("",cache.Root,locations.ForgeRoot,locations.PackageFullName,ready.SourcePlanId,true),cancellation);
            var input=new InputRequest("",cache.Root,locations.ForgeRoot,locations.PackageFullName,ready.SourcePlanId);
            string Config(string category,string id)=>PreparedCampaignStore.ConfigPath(input.CacheRoot,category,id);
            Begin("Starting Forge");var launch=WindowsForgeLaunch.Run(new(input.ForgeRoot,input.PackageFullName),progress,cancellation);Record(launch);Require(launch.State,"Running",launch.Code,launch.Message,launch.Details);pid=launch.ProcessId;
            var running=PackageTarget.Forge(input.ForgeRoot,input.PackageFullName).Find()??throw new CacheException("FORGE_NOT_RUNNING","Forge closed during startup.");
            if(CampaignMenuRuntime.IsReady(input.ForgeRoot,input.PackageFullName,ready.MenuConfigId,cancellation)){
                details.AppendLine(CampaignWatch.Check(request,ready,cancellation));
                Begin("Showing Forge");WindowsForgeLaunch.ActivateExisting(new(input.ForgeRoot,input.PackageFullName));Record(ForgePresentation.Run(input.ForgeRoot,input.PackageFullName,true,cancellation));
                return new("Ready","Game","Your campaign session is already running in Forge.",pid,Details:$"Play log: {PlayLog.PathName}\n{details}",ProcessCreated:running.Created);
            }
            Begin("Continuing through the title screen");var title=TitleAutomation.Run(new(input.ForgeRoot,input.PackageFullName),progress,cancellation);Record(title);Require(title.State,"MainMenu",title.Code,title.Message,title.Details);
            Begin("Registering campaign missions");using(var registry=new CampaignRegistry(input.ForgeRoot,input.PackageFullName,input.CacheRoot))PlayableCache.Register(cache,registry,progress,cancellation);
            Begin("Checking campaign files inside Forge");using(var content=new CampaignContentRuntime(input.ForgeRoot,input.PackageFullName))content.Activate(runtime.ContentPath,runtime.ContentId,progress,cancellation);
            Begin("Preparing the campaign menu");CampaignMenuRuntime.Activate(input.ForgeRoot,input.PackageFullName,Config("menu-config",ready.MenuConfigId),ready.MenuConfigId,progress,cancellation);
            Begin("Preparing campaign rendering");var renderer=CampaignRendererRuntime.Run(input.ForgeRoot,input.PackageFullName,true,cancellation);Record(renderer);if(renderer.Phase!=1 || renderer.Fault!=0)throw new CacheException("CAMPAIGN_RENDERER_NOT_READY","Campaign rendering is not ready. Restart Forge.");
            var deformation=CampaignDeformationRuntime.Run(input.ForgeRoot,input.PackageFullName,true,cancellation);Record(deformation);if(deformation.Phase!=1 || deformation.Errors!=0)throw new CacheException("CAMPAIGN_DEFORMATION_NOT_READY","Facial animation is not ready. Restart Forge.");
            Begin("Preparing pause menus");var ui=CampaignUiRuntime.Run(input.ForgeRoot,input.PackageFullName,Config("ui-residency",ready.UiConfigId),ready.UiConfigId,true,cancellation);Record(ui);if(ui.Phase!=1 || ui.Rejected!=0)throw new CacheException("CAMPAIGN_UI_NOT_READY","Required menu assets are not ready. Restart Forge.");
            Begin("Registering campaign audio");var audio=CampaignAudioRuntime.Run(input.ForgeRoot,input.PackageFullName,true,cancellation);Record(audio);if(audio.Phase!=2 || audio.Result!=1)throw new CacheException("CAMPAIGN_AUDIO_NOT_READY","Campaign audio is not ready. Restart Forge.");
            Begin("Preparing the opening movie");var movie=CampaignMovieRuntime.Run(input.ForgeRoot,input.PackageFullName,Config("movie-config",ready.MovieConfigId),ready.MovieConfigId,true,cancellation);Record(movie);
            Begin("Preparing campaign controls");var controls=CampaignControlsRuntime.Run(input.ForgeRoot,input.PackageFullName,true,cancellation);Record(controls);if(controls.Phase!=1)throw new CacheException("CAMPAIGN_CONTROLS_NOT_READY","Campaign controls are not ready. Restart Forge.");
            Begin("Preparing display settings");var display=CampaignDisplayRuntime.Run(input.ForgeRoot,input.PackageFullName,Config("display-config",ready.DisplayConfigId),ready.DisplayConfigId,true,cancellation);Record(display);
            Begin("Preparing mission completion");var completion=CampaignCompletionRuntime.Run(input.ForgeRoot,input.PackageFullName,Config("completion-config",ready.CompletionConfigId),ready.CompletionConfigId,1,cancellation);Record(completion);if(completion.Phase!=1)throw new CacheException("CAMPAIGN_COMPLETION_NOT_READY","Mission completion is not ready. Restart Forge.");
            Begin("Opening Solo");CampaignMenuRuntime.Activate(input.ForgeRoot,input.PackageFullName,Config("menu-config",ready.MenuConfigId),ready.MenuConfigId,progress,cancellation,true);
            Begin("Showing Forge");WindowsForgeLaunch.ActivateExisting(new(input.ForgeRoot,input.PackageFullName));Record(ForgePresentation.Run(input.ForgeRoot,input.PackageFullName,true,cancellation));
            return new("Ready","Solo","Solo is ready in Forge. Choose an available mission, set your difficulty and start.",pid,Details:$"Play log: {PlayLog.PathName}\n{details}",ProcessCreated:running.Created);
        }
        catch(OperationCanceledException){return new("Paused",phase,"Startup paused. Close Forge before trying again if setup was interrupted.",pid,Details:details.ToString());}
        catch(PlayFailure error){PlayLog.Write(new{phase,error.Code,error.Message,error.Details});return new("Failed",phase,error.Message,pid,error.Code,details+"\n"+error.Details);}
        catch(Exception error){PlayLog.Write(new{phase,error=error.ToString()});return new("Failed",phase,error.Message,pid,error is CacheException known?known.Code:"CAMPAIGN_PLAY_FAILED",details+"\n"+error);}
    }
    private sealed class PlayFailure(string code,string message,string? details):Exception(message){public string Code=>code;public string? Details=>details;}
}
