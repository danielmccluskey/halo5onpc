using System.Text.Json;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Conversion;
using H5SoloLauncher.Core.Forge;
using H5SoloLauncher.Core.Preparation;
using H5SoloLauncher.Core.Storage;
using H5SoloLauncher.Core.Runtime;

// Development only: exercise conversion against previously captured native inputs
// without opening Forge. This deliberately does not certify live cache access.
internal static class OfflineInputs
{
    public static int Legacy(string[] args)
    {
        var snapshot=Manifest<NativeSchemaSnapshot>(args[1],"native-schemas",args[2]);
        using var memory=new CapturedLegacyMemory(args[3]);
        var captured=ForgeLegacySchemas.Capture(snapshot,memory);
        using var lease=new FileStream(SafePaths.Child(args[1],"index.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var bytes=JsonSerializer.SerializeToUtf8Bytes(captured);
        var id=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        var path=SafePaths.Child(args[1],"inputs/native-schemas/"+id.ToLowerInvariant()+".json");
        if(File.Exists(path)) {if(!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))throw new InvalidDataException("Captured schema hash differs.");}
        else File.WriteAllBytes(path,bytes);
        Console.WriteLine(JsonSerializer.Serialize(new{State="Captured",Id=id,LiveAccess="NotTested",Legacy=captured.Legacy!.Length}));
        return 0;
    }
    private sealed class CapturedLegacyMemory:IForgeMemory
    {
        private readonly byte[] text,rdata;
        public CapturedLegacyMemory(string directory)
        {
            byte[] Load(string file,string hash)
            {
                var bytes=File.ReadAllBytes(Path.Combine(directory,file));
                if(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))!=hash)
                    throw new InvalidDataException("The reviewed Forge image capture differs: "+file);
                return bytes;
            }
            text=Load("text.bin","6E399881DED38DA91510F586413F48589E499056CAC866D019F2F4C85060D6A5");
            rdata=Load("rdata.bin","AC81134BCF2889623195A5E2D90B55D4264B16A9BA8DA23482DF6301C45D5037");
        }
        public ulong ImageBase=>0;
        public byte[] Read(ulong address,int count)
        {
            foreach(var (start,bytes) in new[]{(0x1000ul,text),(0x3283000ul,rdata)})
                if(count>=0 && address>=start && address-start<=(ulong)bytes.Length && (ulong)count<=(ulong)bytes.Length-(address-start))
                    return bytes.AsSpan(checked((int)(address-start)),count).ToArray();
            throw new InvalidDataException("Requested code lies outside the reviewed static capture.");
        }
        public void VerifyIdentity(){} // Immutable hash-verified buffers; no live process is represented.
        public void Dispose(){}
    }
    // Package a development cache without launching or exercising the game.
    // Cached catalogue fields may only be reused for byte-identical source mapinfo.
    public static int Finish(string[] args)
    {
        var locations=JsonSerializer.Deserialize<FinishLocations>(File.ReadAllBytes(args[1]))!;
        var input=new InputRequest(locations.Source,locations.Cache,locations.Forge,locations.Package,locations.Plan);
        var prior=Manifest<CampaignCatalogue>(input.CacheRoot,"catalogue-manifests",args[2]);
        var effective=Manifest<EffectivePlan>(input.CacheRoot,"effective-plans",locations.Effective,128L*1024*1024);
        var modules=Manifest<AssembledCampaign>(input.CacheRoot,"game-manifests",locations.Modules);
        var audio=Manifest<PreparedAudio>(input.CacheRoot,"audio-manifests",locations.Audio);
        if(prior.Format!=1 || prior.Rules!=CampaignMetadata.Rules || prior.PackageFullName!=input.PackageFullName ||
            prior.SourceFingerprint!=effective.InputFingerprint || effective.SourcePlanId!=input.PlanId ||
            modules.PackageFullName!=input.PackageFullName || modules.EffectivePlanId!=locations.Effective || modules.AssetsId!=locations.Assets || modules.ShadersId!=locations.Shaders ||
            audio.EffectivePlanId!=locations.Effective || audio.AssetsId!=locations.Assets || audio.PackageFullName!=input.PackageFullName)
            throw new InvalidDataException("Captured catalogue or converted content belongs to different inputs.");
        var progress=new ProgressLog();
        void Require(string state,string expected,string? message,string? details)
        {if(state!=expected)throw new InvalidDataException(message+"\n"+details);}
        var sources=MenuSources.Run(new(input,locations.Effective),progress,default);Require(sources.State,"Prepared",sources.Message,sources.Details);
        var artwork=MenuArtwork.Run(new(input,locations.Effective),progress,default);Require(artwork.State,"Prepared",artwork.Message,artwork.Details);
        var menuRequest=new MenuConfigurationRequest(input,sources.ManifestId!,artwork.ManifestId!);
        var menu=MenuConfiguration.Run(menuRequest,default);Require(menu.State,"Prepared",menu.Message,menu.Details);
        var ui=UiResidency.Run(new(menuRequest,locations.Modules),progress,default);Require(ui.State,"Prepared",ui.Message,ui.Details);
        var movie=CampaignMovie.Prepare(input,progress,default);Require(movie.State,"Prepared",movie.Message,movie.Details);
        var catalogue=CampaignMetadata.Run(new(input,locations.Modules),()=>new CapturedRegistry(prior),progress,default);
        Require(catalogue.State,"Registered",catalogue.Message,catalogue.Details);
        var completion=CompletionConfiguration.Prepare(new(input,sources.ManifestId!,catalogue.ManifestId!),progress,default);
        Require(completion.State,"Prepared",completion.Message,completion.Details);
        var display=DisplayConfiguration.Prepare(input,sources.ManifestId!,progress,default);
        var prepared=new PreparedCampaign(1,PreparedCampaignStore.Rules,"",input.PackageFullName,input.PlanId,locations.Effective,
            locations.Assets,locations.Shaders,locations.Modules,locations.Audio,catalogue.ManifestId!,sources.ManifestId!,artwork.ManifestId!,
            menu.ConfigId!,ui.ConfigId!,movie.ConfigId!,completion.ConfigId!,display);
        var preparedId=PreparedCampaignStore.Save(input,prepared);
        var published=PlayableCache.Publish(input,preparedId,default);
        Console.WriteLine(JsonSerializer.Serialize(new{State="Cached",published.Id,Maps=catalogue.Available,Files=published.Manifest.Files.Length,
            LiveAccess="NotTested",Gameplay="NotTested",Message="Prepared using verified captured metadata. Normal Play still performs Forge access and runtime checks."}));
        return 0;
    }
    private sealed record FinishLocations(string Source,string Cache,string Forge,string Package,string Plan,string Effective,string Assets,string Shaders,string Modules,string Audio);
    private sealed class CapturedRegistry(CampaignCatalogue prior):ICampaignRegistry
    {
        public CampaignMapMetadata[] Read(CampaignMapInput[] inputs,IProgress<IndexProgress> progress,CancellationToken cancellation)
        {
            if(inputs.Length!=prior.Maps.Length)throw new InvalidDataException("Captured catalogue is incomplete.");
            return inputs.Select(input=>
            {
                cancellation.ThrowIfCancellationRequested();
                var map=prior.Maps.Single(x=>input.SourcePath=="__cms__/rtx/"+x.Scenario+".mapinfo");
                if(map.MetadataSha256!=input.Sha256)throw new InvalidDataException("Source map metadata differs from the native capture.");
                return new CampaignMapMetadata(map.MapId,map.Mission,map.Sublevel,map.SourceModules);
            }).ToArray();
        }
        public void Dispose(){}
    }
    public static int Seal(string[] args)
    {
        var prepared=Manifest<PreparedCampaign>(args[2],"prepared-manifests",args[5]);
        var selection=PlayableCache.Publish(new(SourceDiscovery.Describe(args[1]).Root,args[2],args[3],args[4],prepared.SourcePlanId),args[5],default);
        Console.WriteLine(JsonSerializer.Serialize(new{State="Ready",selection.Id,Maps=selection.Manifest.Maps.Count(x=>x.Available),Files=selection.Manifest.Files.Length}));return 0;
    }
    private static T Manifest<T>(string root,string category,string id,long maximum=32L*1024*1024)
    {
        if(id.Length!=64 || id.Any(c=>!Uri.IsHexDigit(c)))throw new InvalidDataException("Invalid manifest identity.");
        using var stream=File.OpenRead(SafePaths.Child(root,"inputs/"+category+"/"+id.ToLowerInvariant()+".json"));
        if(stream.Length>maximum)throw new InvalidDataException("Manifest exceeds the supported size: "+category);
        var bytes=new byte[checked((int)stream.Length)];stream.ReadExactly(bytes);
        if(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))!=id.ToUpperInvariant())throw new InvalidDataException("Manifest hash mismatch.");
        return JsonSerializer.Deserialize<T>(bytes)!;
    }
    public static int Audio(string[] args)
    {
        var input=new InputRequest(args[1],args[2],args[3],args[4],args[5]);
        var prior=Manifest<PreparedAudio>(input.CacheRoot,"audio-manifests",args[8]);
        if(prior.PackageFullName!=input.PackageFullName)throw new InvalidDataException("Captured audio package mismatch.");
        var result=AudioPreparation.Run(new(new(input,args[6]),args[7]),()=>new CapturedAudioReader(input.CacheRoot,prior),new ProgressLog(),default);
        Console.WriteLine(JsonSerializer.Serialize(result));return result.State=="Prepared"?0:1;
    }
    public static int Run(string[] args)
    {
        var input=new InputRequest(args[1],args[2],args[3],args[4],args[5]);
        if(args[6].Length!=64 || args[6].Any(c=>!Uri.IsHexDigit(c)))throw new InvalidDataException("Invalid manifest identity.");
        var manifestBytes=File.ReadAllBytes(SafePaths.Child(input.CacheRoot,"inputs/native-manifests/"+args[6].ToLowerInvariant()+".json"));
        if(manifestBytes.Length>1024*1024 || Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(manifestBytes))!=args[6].ToUpperInvariant())throw new InvalidDataException("Native manifest hash mismatch.");
        var native=JsonSerializer.Deserialize<NativeModuleManifest>(manifestBytes)!;
        if(native.PackageFullName!=input.PackageFullName)throw new InvalidDataException("Native package mismatch.");
        var progress=new ProgressLog();
        var forge=new ForgePreparation().Run(new(input.SourceRoot,input.CacheRoot,input.ForgeRoot,input.PackageFullName,input.PlanId),()=>new CapturedReader(input.CacheRoot,native),progress,default);
        Console.WriteLine(JsonSerializer.Serialize(forge));if(forge.State!="Prepared")return 1;
        var cached=new InputCacheBuilder().Run(input,progress,default);
        Console.WriteLine(JsonSerializer.Serialize(cached));if(cached.State!="Cached")return 1;
        var effective=EffectivePlanBuilder.Run(new(input,args[6],args[7]),progress,default);
        Console.WriteLine(JsonSerializer.Serialize(effective));return effective.State=="Planned"?0:1;
    }
    private sealed class CapturedReader(string root,NativeModuleManifest manifest):IForgeFileReader
    {
        public string Mode=>"Development: previously captured native modules; live access not tested";
        public ForgeRead Read(string relative,long offset,int count,CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var module=manifest.Modules.Single(x=>x.Path==relative);
            var path=SafePaths.Child(root,module.RelativePath);
            using var stream=File.OpenRead(path);
            if(stream.Length!=module.Length)throw new InvalidDataException("Captured module size mismatch.");
            stream.Position=offset;var bytes=new byte[count];stream.ReadExactly(bytes);
            return new(bytes,stream.Length,File.GetLastWriteTimeUtc(path).ToFileTimeUtc());
        }
        public CacheProbe Probe(string path,byte[] expected,CancellationToken cancellation)=>new("NotTested","Offline development pass; a live Forge read is required before playback.");
        public void Dispose(){}
    }
    private sealed class CapturedAudioReader(string root,PreparedAudio manifest):IForgeAudioReader
    {
        public ForgeRead ReadAudio(string relative,long offset,int count,CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();var proof=manifest.NativePackages.Single(x=>x.Path==relative);
            var bytes=File.ReadAllBytes(SafePaths.Child(root,"inputs/native-audio-tables/"+proof.TableSha256.ToLowerInvariant()+".bin"));
            if(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))!=proof.TableSha256)throw new InvalidDataException("Captured audio tables failed their hash check.");
            return new(bytes.AsSpan(checked((int)offset),count).ToArray(),proof.Length,proof.Modified);
        }
        public void Dispose(){}
    }
    private sealed class ProgressLog:IProgress<IndexProgress>
    {
        private DateTime last;
        public void Report(IndexProgress value){if((DateTime.UtcNow-last).TotalSeconds<5)return;last=DateTime.UtcNow;Console.WriteLine(JsonSerializer.Serialize(value));}
    }
}
