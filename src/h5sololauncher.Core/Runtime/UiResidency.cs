using System.Globalization;
using System.Text;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Conversion;
using H5SoloLauncher.Core.Forge;
using H5SoloLauncher.Core.Planning;
using H5SoloLauncher.Core.Preparation;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Core.Runtime;

public sealed record UiResidencyResult(string State,string? ConfigId=null,int Assets=0,int Versions=0,string? Code=null,string? Message=null,string? Details=null);
public sealed record UiResidencyRequest(MenuConfigurationRequest Menu,string ModulesId);
public static class UiResidency
{
    private sealed record NativeTag(CachedNativeModule Module,FileMetadata Metadata,ModuleEntry Entry);
    private sealed record Version(string Id,string Asset,string Checksum);
    public static UiResidencyResult Run(UiResidencyRequest preparation,IProgress<IndexProgress> progress,CancellationToken cancellation)
    {
        try
        {
            var request=preparation.Menu;var input=request.Inputs;var source=SourceDiscovery.Describe(input.SourceRoot);var cache=CacheFolders.Open(input.CacheRoot,source.Root,input.ForgeRoot);
            using var lease=new FileStream(SafePaths.Child(cache.Root,"index.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            using var snapshot=new CatalogSnapshot(cache,source);BuildPlanStore.ReadVerified(cache,snapshot,input.PlanId);
            var menu=InputFiles.Read<MenuSourceManifest>(cache.Root,InputFiles.PathFor("menu-source-manifests",request.SourcesId),4*1024*1024,request.SourcesId);
            var artwork=InputFiles.Read<MenuArtworkManifest>(cache.Root,InputFiles.PathFor("menu-artwork-manifests",request.ArtworkId),1024*1024,request.ArtworkId);
            var native=InputFiles.Read<NativeModuleManifest>(cache.Root,InputFiles.PathFor("native-manifests",menu.NativeManifestId),1024*1024,menu.NativeManifestId);
            if(menu.SourcePlanId!=input.PlanId || menu.SourceFingerprint!=snapshot.Fingerprint || menu.PackageFullName!=input.PackageFullName || artwork.SourceFingerprint!=snapshot.Fingerprint || native.PackageFullName!=input.PackageFullName)throw InputFiles.Damaged();
            Dictionary<(string Group,string Id),NativeTag> latest=[];List<NativeTag> all=[];
            foreach(var module in native.Modules.OrderBy(x=>EffectivePlanBuilder.Rank(x.Path,0)))
            {
                cancellation.ThrowIfCancellationRequested();progress.Report(new("Checking native pause menu assets",0,0,module.Path));var path=SafePaths.Child(cache.Root,module.RelativePath);var metadata=FileMetadata.Read(path,"Module",cancellation);
                using var stream=File.OpenRead(path);if(metadata.Digest!=module.TableDigest || metadata.FileLength!=module.Length || InputFiles.Digest(stream,cancellation)!=module.Sha256)throw InputFiles.Damaged();
                foreach(var entry in Enumerable.Range(0,metadata.ItemCount).Select(metadata.Entry).Where(x=>x.StoredSize>0 && x.TagId!="ffffffff")){var tag=new NativeTag(module,metadata,entry);latest[(entry.Group,entry.TagId)]=tag;all.Add(tag);}
            }
            Queue<NativeTag> pending=[];Dictionary<(string Group,string Id),MenuTagProof> selected=[];
            NativeTag Find(string group,string id)=>latest.TryGetValue((group,id),out var tag)?tag:throw Invalid("A native menu resource is missing: "+group+"/"+id);
            byte[] Read(NativeTag tag){using var stream=File.OpenRead(SafePaths.Child(cache.Root,tag.Module.RelativePath));return ModulePayloadReader.Read(stream,tag.Metadata,tag.Entry,cancellation).Bytes;}
            foreach(var id in new[]{"5da21966","357f0fec"})pending.Enqueue(Find("ngst",id));
            foreach(var id in new[]{"0f87e6e4","deb65c50","8269fecc"})pending.Enqueue(Find("retm",id));
            foreach(var item in menu.Tags.Where(x=>x.Role!="MenuGraph"))pending.Enqueue(Find(item.Native.Identity.Group,item.Native.Identity.TagId));
            foreach(var target in menu.Tags.SelectMany(x=>x.Bindings).Where(x=>x.Target is not null).Select(x=>x.Target!))pending.Enqueue(Find(target.Identity.Group,target.Identity.TagId));
            var shared=TagMetadataReader.Read(Read(Find("wigl","000038cb")));
            foreach(var reference in shared.Dependencies.Where(x=>x.Identity.Group=="bitd"))pending.Enqueue(Find(reference.Identity.Group,reference.Identity.TagId));
            while(pending.TryDequeue(out var tag))
            {
                var entry=tag.Entry;var key=(entry.Group,entry.TagId);if(selected.ContainsKey(key))continue;cancellation.ThrowIfCancellationRequested();
                var bytes=Read(tag);var metadata=TagMetadataReader.Read(bytes);var proof=new MenuTagProof(tag.Module.Path,entry.Index,entry.Name,new(entry.Group,entry.TagId,entry.AssetId),entry.Checksum,InputFiles.Hash(bytes));selected.Add(key,proof);
                if(selected.Count>4090)throw Invalid("The required native menu closure exceeds the supported size.");
                progress.Report(new("Preparing pause and campaign menu retention",selected.Count,0,entry.Name));
                foreach(var dependency in metadata.Dependencies)
                {
                    var target=Find(dependency.Identity.Group,dependency.Identity.TagId);
                    if(target.Entry.AssetId!=dependency.Identity.AssetId && (dependency.Name is null || DependencyNames.Canonical(dependency.Name)!=DependencyNames.Canonical(target.Entry.Name)))throw Invalid("A native menu dependency has an incompatible identity: "+dependency.Name);
                    pending.Enqueue(target);
                }
            }
            var modified=menu.Tags.Select(x=>x.Native.Identity.TagId).ToHashSet();
            var versions=all.Where(x=>x.Entry.Group=="unic" && selected.ContainsKey((x.Entry.Group,x.Entry.TagId)) && !modified.Contains(x.Entry.TagId)).Select(x=>new Version(x.Entry.TagId,x.Entry.AssetId,x.Entry.Checksum)).ToHashSet();
            var modules=InputFiles.Read<AssembledCampaign>(cache.Root,InputFiles.PathFor("game-manifests",preparation.ModulesId),4*1024*1024,preparation.ModulesId);
            if(modules.Rules!=ModuleAssembly.Rules || modules.PackageFullName!=input.PackageFullName || modules.EffectivePlanId!=artwork.EffectivePlanId)throw InputFiles.Damaged();
            foreach(var module in modules.Modules)
            {
                cancellation.ThrowIfCancellationRequested();progress.Report(new("Checking mission localization versions",0,0,module.OriginalPath));var path=SafePaths.Child(cache.Root,module.RelativePath);
                using var stream=File.OpenRead(path);if(stream.Length!=module.Verified.Bytes || InputFiles.Digest(stream,cancellation)!=module.Verified.Sha256)throw InputFiles.Damaged();
                var metadata=FileMetadata.Read(path,"Module",cancellation);
                foreach(var entry in Enumerable.Range(0,metadata.ItemCount).Select(metadata.Entry).Where(x=>x.Group=="unic" && x.StoredSize>0 && selected.ContainsKey((x.Group,x.TagId)) && !modified.Contains(x.TagId)))versions.Add(new(entry.TagId,entry.AssetId,entry.Checksum));
            }
            using var output=new MemoryStream();using var writer=new BinaryWriter(output,Encoding.UTF8,true);
            void Text(string value){var bytes=Encoding.UTF8.GetBytes(value);writer.Write(bytes.Length);writer.Write(bytes);}
            void Identity(string id,string asset,string checksum){writer.Write(uint.Parse(id,NumberStyles.HexNumber));writer.Write(ulong.Parse(asset,NumberStyles.HexNumber));writer.Write(ulong.Parse(checksum,NumberStyles.HexNumber));}
            writer.Write(0x49553548u);writer.Write(1);Text(input.PackageFullName);writer.Write(selected.Count+artwork.Tags.Length);
            foreach(var item in selected.Values.OrderBy(x=>x.Identity.TagId))Identity(item.Identity.TagId,item.Identity.AssetId,item.Checksum);
            foreach(var item in artwork.Tags)Identity(item.Identity.TagId,item.Identity.AssetId,item.Checksum);
            writer.Write(versions.Count);foreach(var item in versions.OrderBy(x=>x.Id).ThenBy(x=>x.Asset).ThenBy(x=>x.Checksum))Identity(item.Id,item.Asset,item.Checksum);writer.Flush();
            var data=output.ToArray();var configId=InputFiles.Hash(data);InputFiles.Write(cache.Root,InputFiles.PathFor("ui-residency",configId,".bin"),data);
            return new("Prepared",configId,selected.Count+artwork.Tags.Length,versions.Count,Message:"Native pause and campaign menu assets prepared for mission transitions.");
        }
        catch(OperationCanceledException){return new("Paused",Message:"Menu retention preparation paused.");}
        catch(Exception error){return new("Failed",Code:error is CacheException known?known.Code:"CAMPAIGN_UI_RETENTION",Message:error.Message,Details:error.ToString());}
    }
    private static CacheException Invalid(string message)=>new("CAMPAIGN_UI_RETENTION",message);
}
