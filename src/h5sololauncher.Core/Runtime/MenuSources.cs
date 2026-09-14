using System.Buffers.Binary;
using System.Text;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Forge;
using H5SoloLauncher.Core.Planning;
using H5SoloLauncher.Core.Preparation;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Core.Runtime;

public sealed record MenuSourceRequest(InputRequest Inputs,string EffectivePlanId);
public sealed record MenuTagProof(string File,int Item,string Name,AssetReference Identity,string Checksum,string PayloadSha256);
public sealed record MenuBinding(int Block,int Offset,string? Name,AssetReference Source,MenuTagProof? Target,string Method);
public sealed record MenuTagSource(string Role,MenuTagProof Native,MenuTagProof? Source,MenuBinding[] Bindings);
public sealed record MenuSourceManifest(int Format,string Rules,string SourcePlanId,string SourceFingerprint,string NativeManifestId,string PackageFullName,MenuTagSource[] Tags);
public sealed record MenuSourceResult(string State,string? ManifestId=null,int Tags=0,int Unresolved=0,string? Code=null,string? Message=null,string? Details=null);

public static class MenuSources
{
    public const string Rules="campaign-menu-sources-1";
    private sealed record NativeTag(CachedNativeModule Module,FileMetadata Metadata,ModuleEntry Entry);
    public static MenuSourceResult Run(MenuSourceRequest request,IProgress<IndexProgress> progress,CancellationToken cancellation)
    {
        try
        {
            var input=request.Inputs;var source=SourceDiscovery.Describe(input.SourceRoot);var cache=CacheFolders.Open(input.CacheRoot,source.Root,input.ForgeRoot);
            using var lease=new FileStream(SafePaths.Child(cache.Root,"index.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            using var catalog=new CatalogSnapshot(cache,source);BuildPlanStore.ReadVerified(cache,catalog,input.PlanId);
            var effective=InputFiles.Read<EffectivePlan>(cache.Root,InputFiles.PathFor("effective-plans",request.EffectivePlanId),128L*1024*1024,request.EffectivePlanId);
            if(effective.SourcePlanId!=input.PlanId || effective.InputFingerprint!=catalog.Fingerprint)throw InputFiles.Damaged();
            var native=InputFiles.Read<NativeModuleManifest>(cache.Root,InputFiles.PathFor("native-manifests",effective.NativeManifestId),1024*1024,effective.NativeManifestId);
            if(native.Rules!=NativeModuleCache.Rules || native.PackageFullName!=input.PackageFullName)throw InputFiles.Damaged();
            Dictionary<(string Group,string Id),NativeTag> nativeTags=[];
            foreach(var module in native.Modules.OrderBy(x=>EffectivePlanBuilder.Rank(x.Path,0)))
            {
                cancellation.ThrowIfCancellationRequested();progress.Report(new("Reading native menu controls",nativeTags.Count,0,module.Path));
                var path=SafePaths.Child(cache.Root,module.RelativePath);var metadata=FileMetadata.Read(path,"Module",cancellation);
                using var stream=File.OpenRead(path);if(metadata.Digest!=module.TableDigest || metadata.FileLength!=module.Length || InputFiles.Digest(stream,cancellation)!=module.Sha256)throw InputFiles.Damaged();
                foreach(var entry in Enumerable.Range(0,metadata.ItemCount).Select(metadata.Entry).Where(x=>x.StoredSize>0 && x.TagId!="ffffffff"))nativeTags[(entry.Group,entry.TagId)]=new(module,metadata,entry);
            }
            var globals=catalog.Files.Where(x=>x.Kind=="Module" && x.Path.StartsWith("deploy/any/levels/globals-rtx-",StringComparison.Ordinal)).SelectMany(x=>catalog.InModule(x.Path)).ToArray();
            Dictionary<(string,int),MenuTagProof> nativeSaved=[];Dictionary<string,TagDocument> documents=[];
            MenuTagProof Store(string file,ModuleEntry entry,byte[] bytes)
            {
                var id=InputFiles.Hash(bytes);InputFiles.Write(cache.Root,InputFiles.PathFor("menu-payloads",id,".bin"),bytes);documents[id]=new(bytes);
                return new(file,entry.Index,entry.Name,new(entry.Group,entry.TagId,entry.AssetId),entry.Checksum,id);
            }
            MenuTagProof Native(NativeTag tag)
            {
                var key=(tag.Module.Path,tag.Entry.Index);if(nativeSaved.TryGetValue(key,out var saved))return saved;
                using var stream=File.OpenRead(SafePaths.Child(cache.Root,tag.Module.RelativePath));var bytes=ModulePayloadReader.Read(stream,tag.Metadata,tag.Entry,cancellation).Bytes;
                saved=Store(tag.Module.Path,tag.Entry,bytes);nativeSaved.Add(key,saved);return saved;
            }
            MenuTagProof Host(string group,string id)=>nativeTags.TryGetValue((group,id),out var tag)?Native(tag):throw Invalid("A required native menu host is missing: "+group+"/"+id);
            MenuTagProof Source(string group,string id)
            {
                var selected=globals.Where(x=>x.Identity.Group==group && x.Identity.TagId==id).OrderBy(x=>EffectivePlanBuilder.Rank(x.File,x.Item)).LastOrDefault()??throw Invalid("The dump is missing a campaign menu asset: "+group+"/"+id);
                var path=SafePaths.Child(source.Root,selected.File);var metadata=FileMetadata.Read(path,"Module",cancellation);var indexed=catalog.Files.Single(x=>x.Path==selected.File);
                if(metadata.Digest!=indexed.Digest || metadata.FileLength!=indexed.Length)throw new CacheException("DUMP_CHANGED","A campaign menu source changed after indexing.");
                var entry=metadata.Entry(selected.Item);if(entry.AssetId!=selected.Identity.AssetId || entry.Checksum!=selected.Checksum)throw InputFiles.Damaged();
                using var stream=File.OpenRead(path);return Store(selected.File,entry,ModulePayloadReader.Read(stream,metadata,entry,cancellation).Bytes);
            }
            var mainLua=Host("luas","00024a4f");var mainWpf=Host("wpfs","00024a4e");var hostLua=Host("luas","b719da90");var hostWpf=Host("wpfs","4435a884");var strings=Host("unic","d0942c43");
            var sourceLua=Source("luas","ac5279de");var sourceWpf=Source("wpfs","31879329");
            var backgroundLua=Host("luas","bca20cd8");var backgroundWpf=Host("wpfs","a5832ad9");
            MenuBinding[] Bind(MenuTagProof selected)
            {
                var doc=documents[selected.PayloadSha256];List<MenuBinding> bindings=[];
                foreach(var reference in doc.References)
                {
                    var dependency=Dependency(doc,reference.Dependency);MenuTagProof? target=null;var method="Unresolved";
                    if(dependency.Identity.Group=="luas" && dependency.Identity.TagId==sourceLua.Identity.TagId){target=hostLua;method="IsolatedCampaignHost";}
                    else if(dependency.Identity.Group=="luas" && dependency.Identity.TagId=="b1f13cf1"){target=backgroundLua;method="IsolatedBackgroundHost";}
                    else if(dependency.Identity.Group=="unic" && dependency.Identity.TagId=="d5e5b02a"){target=strings;method="MergedSkullDictionary";}
                    else if(nativeTags.TryGetValue((dependency.Identity.Group,dependency.Identity.TagId),out var candidate))
                    {
                        if(candidate.Entry.AssetId==dependency.Identity.AssetId){target=Native(candidate);method="NativeExact";}
                        else if(dependency.Name is not null && DependencyNames.Canonical(candidate.Entry.Name)==DependencyNames.Canonical(dependency.Name)){target=Native(candidate);method="NativeMenuPlatformBinding";}
                    }
                    bindings.Add(new(reference.FieldBlock,reference.FieldOffset,dependency.Name,dependency.Identity,target,method));
                }
                return bindings.ToArray();
            }
            MenuTagSource Pair(string role,MenuTagProof host,MenuTagProof from,bool references)
            {
                var original=documents[host.PayloadSha256];var candidate=documents[from.PayloadSha256];
                if(original.Blocks[0].Size!=candidate.Blocks[0].Size || !original.Metadata.RootGuids.SequenceEqual(candidate.Metadata.RootGuids))throw Invalid("A campaign menu host has a different native root layout: "+role);
                if(references)
                {
                    var fields=original.Structures.Where(x=>x.Kind==1).ToDictionary(x=>(x.FieldBlock,x.FieldOffset));
                    foreach(var field in candidate.Structures.Where(x=>x.Kind==1))if(field.FieldBlock!=0 || !fields.TryGetValue((field.FieldBlock,field.FieldOffset),out var control) || control.Guid!=field.Guid)throw Invalid("A campaign menu array layout differs from its native host.");
                    if(candidate.DataReferences.Any(x=>x.FieldBlock!=0))throw Invalid("A campaign menu data field is outside its supported host root.");
                }
                return new(role,host,from,references?Bind(from):[]);
            }
            var tags=new[]{Pair("MainLua",mainLua,Source("luas","00024a4f"),false),Pair("MainWpf",mainWpf,Source("wpfs","00024a4e"),false),Pair("CampaignLua",hostLua,sourceLua,true),Pair("CampaignWpf",hostWpf,sourceWpf,true),Pair("SkullStrings",strings,Source("unic","d5e5b02a"),false),new MenuTagSource("MenuGraph",Host("ngst","cc77d593"),null,[]),Pair("BackgroundLua",backgroundLua,Source("luas","b1f13cf1"),true),Pair("BackgroundWpf",backgroundWpf,Source("wpfs","fca002ec"),true)};
            var manifest=new MenuSourceManifest(1,Rules,input.PlanId,catalog.Fingerprint,effective.NativeManifestId,input.PackageFullName,tags);var id=InputFiles.Save(cache.Root,"menu-source-manifests",manifest);
            return new("Prepared",id,tags.Length,tags.Sum(x=>x.Bindings.Count(y=>y.Target is null)),Message:"Source campaign menu assets and native host bindings prepared.");
        }
        catch(OperationCanceledException){return new("Paused",Message:"Menu source preparation paused.");}
        catch(Exception error){return new("Failed",Code:error is CacheException known?known.Code:"CAMPAIGN_MENU_SOURCES",Message:error.Message,Details:error.ToString());}
    }
    public static byte[] ReadPayload(string cache,string id)
    {
        using var stream=File.OpenRead(SafePaths.Child(cache,InputFiles.PathFor("menu-payloads",id,".bin")));
        if(stream.Length>ModulePayloadReader.MaximumTagBytes || InputFiles.Digest(stream,default)!=id)throw InputFiles.Damaged();stream.Position=0;var bytes=new byte[stream.Length];stream.ReadExactly(bytes);return bytes;
    }
    private static NamedDependency Dependency(TagDocument doc,int index)
    {
        var count=BinaryPrimitives.ReadUInt32LittleEndian(doc.Bytes.AsSpan(28));if(index<0 || index>=count)throw Invalid("A campaign menu reference has no valid dependency.");
        var at=80+index*24;var group=doc.Bytes.AsSpan(at,4).ToArray();Array.Reverse(group);var gid=BinaryPrimitives.ReadUInt32LittleEndian(doc.Bytes.AsSpan(at+16)).ToString("x8");var asset=BinaryPrimitives.ReadUInt64LittleEndian(doc.Bytes.AsSpan(at+8)).ToString("x16");
        var identity=new AssetReference(Encoding.ASCII.GetString(group),gid,asset);
        var named=doc.Metadata.Dependencies.Where(x=>x.Identity==identity).ToArray();if(named.Length!=1)throw Invalid("A campaign menu dependency has ambiguous names.");return named[0];
    }
    private static CacheException Invalid(string message)=>new("CAMPAIGN_MENU_LAYOUT",message);
}
