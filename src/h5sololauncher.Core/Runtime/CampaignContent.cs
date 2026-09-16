using System.Text;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Conversion;
using H5SoloLauncher.Core.Planning;
using H5SoloLauncher.Core.Preparation;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Core.Runtime;

public sealed record CampaignFile(string OriginalPath,string CachePath,long Bytes,string Sha256);
public sealed record CampaignContentConfig(string PackageFullName,string CacheRoot,CampaignFile[] Files,CampaignRoute[] Routes);
public sealed record CampaignContentRequest(InputRequest Inputs,string CatalogueId,string AudioId,string? ArtworkId=null);
public sealed record CampaignContentResult(string State,string? ConfigId=null,int Files=0,string? Code=null,string? Message=null,string? Details=null);
public interface ICampaignContent : IDisposable
{
    void Activate(string configuration,string sha256,IProgress<IndexProgress> progress,CancellationToken cancellation);
}
public static class CampaignContent
{
    public static CampaignContentResult Run(CampaignContentRequest request,Func<ICampaignContent> factory,IProgress<IndexProgress> progress,CancellationToken cancellation)
    {
        try
        {
            var input=request.Inputs;var source=SourceDiscovery.Describe(input.SourceRoot);var cache=CacheFolders.Open(input.CacheRoot,source.Root,input.ForgeRoot);
            using var lease=new FileStream(SafePaths.Child(cache.Root,"index.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            using var snapshot=new CatalogSnapshot(cache,source);BuildPlanStore.ReadVerified(cache,snapshot,input.PlanId);
            var catalogue=InputFiles.Read<CampaignCatalogue>(cache.Root,InputFiles.PathFor("catalogue-manifests",request.CatalogueId),1024*1024,request.CatalogueId);
            var modules=InputFiles.Read<AssembledCampaign>(cache.Root,InputFiles.PathFor("game-manifests",catalogue.ModulesId),4*1024*1024,catalogue.ModulesId);
            var audio=InputFiles.Read<PreparedAudio>(cache.Root,InputFiles.PathFor("audio-manifests",request.AudioId),32*1024*1024,request.AudioId);
            if(catalogue.Format!=1 || catalogue.Rules!=CampaignMetadata.Rules || catalogue.SourceFingerprint!=snapshot.Fingerprint || catalogue.PackageFullName!=input.PackageFullName ||
                modules.Rules!=ModuleAssembly.Rules || modules.PackageFullName!=input.PackageFullName || audio.Rules!=AudioPreparation.Rules || audio.PackageFullName!=input.PackageFullName || audio.EffectivePlanId!=modules.EffectivePlanId || audio.AssetsId!=modules.AssetsId)throw InputFiles.Damaged();
            List<CampaignFile> files=modules.Modules.Select(x=>new CampaignFile(x.OriginalPath,x.RelativePath,x.Verified.Bytes,x.Verified.Sha256)).ToList();
            files.Add(new("sound/win/h5solo-campaign.pck",audio.RelativePath,audio.Bytes,audio.Sha256));
            if(request.ArtworkId is not null)
            {
                var artwork=InputFiles.Read<MenuArtworkManifest>(cache.Root,InputFiles.PathFor("menu-artwork-manifests",request.ArtworkId),1024*1024,request.ArtworkId);
                if(artwork.Rules!=MenuArtwork.Rules || artwork.SourceFingerprint!=snapshot.Fingerprint || artwork.EffectivePlanId!=modules.EffectivePlanId)throw InputFiles.Damaged();
                files.Add(new(artwork.OriginalPath,artwork.RelativePath,artwork.Verified.Bytes,artwork.Verified.Sha256));
            }
            foreach(var name in new[]{"campaignnormal.bin","campaignarcade.bin"})
            {
                var relative="__cms__/campaign/"+name;using var stream=File.OpenRead(SafePaths.Child(source.Root,relative));
                if(stream.Length is <=0 or >1024*1024)throw new CacheException("CAMPAIGN_VARIANT_INVALID","A source campaign variant is missing or has an unsupported size.");
                var bytes=new byte[stream.Length];stream.ReadExactly(bytes);var digest=InputFiles.Hash(bytes);var target="game/variants/"+digest.ToLowerInvariant()+"/"+name;var fullPath=SafePaths.Child(cache.Root,target);
                if(!File.Exists(fullPath) || !File.ReadAllBytes(fullPath).AsSpan().SequenceEqual(bytes))InputFiles.Write(cache.Root,target,bytes);
                files.Add(new(relative,target,bytes.Length,digest));
            }
            var config=new CampaignContentConfig(input.PackageFullName,cache.Root,files.ToArray(),catalogue.Maps.Where(x=>x.Available).ToArray());
            var wire=Encode(config);var id=InputFiles.Hash(wire);var path=InputFiles.PathFor("runtime-config",id,".bin");InputFiles.Write(cache.Root,path,wire);
            using var runtime=factory();runtime.Activate(SafePaths.Child(cache.Root,path),id,progress,cancellation);
            return new("Active",id,files.Count,Message:"Campaign files and mission availability are active in Forge. Menu and renderer preparation are still required.");
        }
        catch(OperationCanceledException){return new("Paused",Message:"Campaign file verification paused. A queued native operation remains owned by Forge.");}
        catch(Exception error){return new("Failed",Code:error is CacheException known?known.Code:"CAMPAIGN_CONTENT_FAILED",Message:error.Message,Details:error.ToString());}
    }
    public static byte[] Encode(CampaignContentConfig config)
    {
        if(config.Files.Length is <1 or >1024 || config.Routes.Length is <1 or >64 || config.Files.Select(x=>x.OriginalPath).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=config.Files.Length || config.Routes.Select(x=>x.MapId).Distinct().Count()!=config.Routes.Length)
            throw new CacheException("CAMPAIGN_CONTENT_INVALID","The campaign runtime contains invalid or duplicate routes.");
        using var stream=new MemoryStream();using var writer=new BinaryWriter(stream,Encoding.UTF8,true);
        void Blob(byte[] bytes){writer.Write(bytes.Length);writer.Write(bytes);}
        void Text(string text)=>Blob(new UTF8Encoding(false,true).GetBytes(text));
        writer.Write(0x54433548u);writer.Write(1);Text(config.PackageFullName);Text(SafePaths.Canonical(config.CacheRoot));writer.Write(config.Files.Length);
        foreach(var file in config.Files)
        {
            SafePaths.Child(config.CacheRoot,file.CachePath);
            if(file.Bytes<=0 || file.Sha256.Length!=64 || file.Sha256.Any(c=>!Uri.IsHexDigit(c)))throw new CacheException("CAMPAIGN_CONTENT_INVALID","A campaign runtime file has an invalid size or digest.");
            Text(file.OriginalPath);Text(file.CachePath);writer.Write(file.Bytes);Blob(Convert.FromHexString(file.Sha256));
        }
        writer.Write(config.Routes.Length);
        foreach(var route in config.Routes){CampaignMetadata.ModuleList(route.SourceModules);CampaignMetadata.ModuleList(route.TargetModules);writer.Write(route.MapId);writer.Write(route.Mission);writer.Write(route.NextMapId);Text(route.Scenario);Blob(route.SourceModules);Blob(route.TargetModules);}
        writer.Flush();if(stream.Length>4*1024*1024)throw new CacheException("CAMPAIGN_CONTENT_INVALID","The campaign runtime configuration exceeds its supported size.");return stream.ToArray();
    }
}
