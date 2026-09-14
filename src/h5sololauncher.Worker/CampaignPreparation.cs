using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Conversion;
using H5SoloLauncher.Core.Planning;
using H5SoloLauncher.Core.Preparation;
using H5SoloLauncher.Core.Runtime;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Worker;
internal static class CampaignPreparation
{
    public static PreparationResult Run(InputRequest input,string effectiveId,IProgress<IndexProgress> progress,CancellationToken cancellation)
    {
        var phase="Converting campaign assets";
        void Begin(string stage){cancellation.ThrowIfCancellationRequested();phase=stage;progress.Report(new(stage,0,0));}
        void Require(string state,string expected,string? code,string? message,string? details)
        {
            if(state=="Paused")throw new OperationCanceledException(cancellation);
            if(state!=expected)throw new PreparationFailure(code??"CAMPAIGN_PREPARATION_FAILED",message??"The campaign stage could not finish.",details);
        }
        try
        {
            Begin("Converting campaign assets");var conversion=new ConversionAuditRequest(input,effectiveId);
            var assets=ConvertedAssets.Run(conversion,progress,cancellation);Require(assets.State,"Converted",assets.Code,assets.Message,assets.Details);
            Begin("Preparing campaign shaders");var shaderRequest=new ShaderPreparationRequest(conversion,assets.ManifestId!);
            var shaders=ShaderPreparation.Run(shaderRequest,()=>new ShaderValidator(),progress,cancellation);Require(shaders.State,"Prepared",shaders.Code,shaders.Message,shaders.Details);
            Begin("Building game modules");var modules=ModuleAssembly.Run(new(shaderRequest,shaders.ManifestId!),progress,cancellation);Require(modules.State,"Assembled",modules.Code,modules.Message,modules.Details);
            Begin("Preparing campaign audio");var audio=AudioPreparation.Run(new(conversion,assets.ManifestId!),()=>new ForgeFiles(new(input.SourceRoot,input.CacheRoot,input.ForgeRoot,input.PackageFullName,input.PlanId),cancellation),progress,cancellation);Require(audio.State,"Prepared",audio.Code,audio.Message,audio.Details);
            Begin("Preparing campaign menus");var sources=MenuSources.Run(new(input,effectiveId),progress,cancellation);Require(sources.State,"Prepared",sources.Code,sources.Message,sources.Details);
            var artwork=MenuArtwork.Run(new(input,effectiveId),progress,cancellation);Require(artwork.State,"Prepared",artwork.Code,artwork.Message,artwork.Details);
            var menuRequest=new MenuConfigurationRequest(input,sources.ManifestId!,artwork.ManifestId!);var menu=MenuConfiguration.Run(menuRequest,cancellation);Require(menu.State,"Prepared",menu.Code,menu.Message,menu.Details);
            Begin("Preparing pause menus");var ui=UiResidency.Run(new(menuRequest,modules.ManifestId!),progress,cancellation);Require(ui.State,"Prepared",ui.Code,ui.Message,ui.Details);
            Begin("Preparing the opening movie");var movie=CampaignMovie.Prepare(input,progress,cancellation);Require(movie.State,"Prepared",movie.Code,movie.Message,movie.Details);
            Begin("Preparing the mission catalogue");var title=TitleAutomation.Run(new(input.ForgeRoot,input.PackageFullName),progress,cancellation);Require(title.State,"MainMenu",title.Code,title.Message,title.Details);
            var catalogue=CampaignMetadata.Run(new(input,modules.ManifestId!),()=>new CampaignRegistry(input.ForgeRoot,input.PackageFullName,input.CacheRoot),progress,cancellation);Require(catalogue.State,"Registered",catalogue.Code,catalogue.Message,catalogue.Details);
            Begin("Preparing mission completion");var completion=CompletionConfiguration.Prepare(new(input,sources.ManifestId!,catalogue.ManifestId!),progress,cancellation);Require(completion.State,"Prepared",completion.Code,completion.Message,completion.Details);
            Begin("Preparing display settings");var display=DisplayConfiguration.Prepare(input,sources.ManifestId!,progress,cancellation);
            var ready=new PreparedCampaign(1,PreparedCampaignStore.Rules,"",input.PackageFullName,input.PlanId,effectiveId,assets.ManifestId!,shaders.ManifestId!,modules.ManifestId!,audio.ManifestId!,catalogue.ManifestId!,sources.ManifestId!,artwork.ManifestId!,menu.ConfigId!,ui.ConfigId!,movie.ConfigId!,completion.ConfigId!,display);
            cancellation.ThrowIfCancellationRequested();var legacyId=PreparedCampaignStore.Save(input,ready);
            Begin("Finishing the portable cache");var id=PlayableCache.Publish(input,legacyId,cancellation).Id;
            return new("Ready","Ready","Osiris and Blue Team are prepared. Play will set up Forge and open Solo automatically.",PreparedId:id);
        }
        catch(OperationCanceledException){return new("Paused",phase,"Preparation paused. Completed stages were kept.");}
        catch(PreparationFailure error){return new("Failed",phase,error.Message,error.Code,error.Details);}
        catch(Exception error){return new("Failed",phase,error.Message,error is CacheException known?known.Code:"CAMPAIGN_PREPARATION_FAILED",error.ToString());}
    }
    private sealed class PreparationFailure(string code,string message,string? details):Exception(message){public string Code=>code;public string? Details=>details;}
}
