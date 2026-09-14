using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Conversion;
using H5SoloLauncher.Core.Forge;
using H5SoloLauncher.Core.Planning;
using H5SoloLauncher.Core.Preparation;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Core.Runtime;

public sealed record MenuArtworkTag(string Name,AssetReference Identity,string Checksum,int Index);
public sealed record MenuArtworkManifest(int Format,string Rules,string EffectivePlanId,string SourceFingerprint,string OriginalPath,string RelativePath,ModuleWriteResult Verified,MenuArtworkTag[] Tags);
public sealed record MenuArtworkResult(string State,string? ManifestId=null,int Images=0,long Bytes=0,string? Code=null,string? Message=null,string? Details=null);
public static class MenuArtwork
{
    public const string Rules="campaign-menu-artwork-1";
    private sealed record Item(string File,ModuleEntry Entry,int[] Children,byte[]? Payload);
    public static MenuArtworkResult Run(MenuSourceRequest request,IProgress<IndexProgress> progress,CancellationToken cancellation)
    {
        try
        {
            var input=request.Inputs;var source=SourceDiscovery.Describe(input.SourceRoot);var cache=CacheFolders.Open(input.CacheRoot,source.Root,input.ForgeRoot);
            using var lease=new FileStream(SafePaths.Child(cache.Root,"index.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            using var catalog=new CatalogSnapshot(cache,source);BuildPlanStore.ReadVerified(cache,catalog,input.PlanId);
            var effective=InputFiles.Read<EffectivePlan>(cache.Root,InputFiles.PathFor("effective-plans",request.EffectivePlanId),128L*1024*1024,request.EffectivePlanId);
            if(effective.SourcePlanId!=input.PlanId || effective.InputFingerprint!=catalog.Fingerprint)throw InputFiles.Damaged();
            var schemas=InputFiles.Read<NativeSchemaSnapshot>(cache.Root,InputFiles.PathFor("native-schemas",effective.SchemaId),4*1024*1024,effective.SchemaId);var converter=new StructuralConverter(schemas);
            var wanted=new[]{"d9489f7b","f87213af","2574a4ba","acd901b4","37c51ddd"};
            var tags=catalog.Files.Where(x=>x.Kind=="Module" && x.Path.StartsWith("deploy/x1/levels/globals-rtx-",StringComparison.Ordinal)).SelectMany(x=>catalog.InModule(x.Path)).Where(x=>x.Identity.Group=="bitm" && wanted.Contains(x.Identity.TagId)).GroupBy(x=>x.Identity.TagId).Select(x=>x.OrderBy(t=>EffectivePlanBuilder.Rank(t.File,t.Item)).Last()).OrderBy(x=>x.Identity.TagId,StringComparer.Ordinal).ToArray();
            if(tags.Length!=wanted.Length)throw Invalid("The dump is missing required campaign menu pictures.");
            List<Item> items=[];var imagesDone=0;long totalBytes=0;
            foreach(var group in tags.GroupBy(x=>x.File))
            {
                var path=SafePaths.Child(source.Root,group.Key);var metadata=FileMetadata.Read(path,"Module",cancellation);var indexed=catalog.Files.Single(x=>x.Path==group.Key);
                if(metadata.Digest!=indexed.Digest || metadata.FileLength!=indexed.Length)throw new CacheException("DUMP_CHANGED","A campaign menu picture source changed after indexing.");
                using var stream=File.OpenRead(path);
                int[] Children(ModuleEntry entry)=>Enumerable.Range(entry.ResourceIndex,entry.ResourceCount).Select(metadata.Resource).ToArray();
                void Add(ModuleEntry entry,byte[]? payload){totalBytes+=payload?.LongLength??0;if(totalBytes>512L*1024*1024)throw Invalid("The campaign menu artwork exceeds its supported size.");items.Add(new(group.Key,entry,Children(entry),payload));}
                foreach(var tag in group)
                {
                    cancellation.ThrowIfCancellationRequested();progress.Report(new("Converting campaign menu pictures",imagesDone,0,tag.Name));
                    var entry=metadata.Entry(tag.Item);var document=new TagDocument(ModulePayloadReader.Read(stream,metadata,entry,cancellation).Bytes);
                    if(document.Metadata.Schema!=BitmapConverter.SourceTagSchema)throw Invalid("A menu bitmap uses an unsupported source schema.");
                    Add(entry,converter.Convert("bitm",document).Bytes);var children=Children(entry);var imageRef=document.Structures.Single(x=>x.FieldBlock==0 && x.FieldOffset==240);var images=document.Block(imageRef.Target);
                    if(images.Length!=children.Length*40)throw Invalid("The menu bitmap image count differs from its resource graph.");
                    for(var j=0;j<children.Length;j++)
                    {
                        var child=metadata.Entry(children[j]);if(child.Parent!=entry.Index || child.StoredSize==0)throw Invalid("A menu image is stripped or belongs to another bitmap.");
                        var chunks=Children(child).Select(metadata.Entry).ToArray();if(chunks.Any(x=>x.Parent!=child.Index || x.ResourceCount!=0 || x.StoredSize==0))throw Invalid("A menu bitmap has an unsupported streamed resource graph.");
                        var resource=new TagDocument(ModulePayloadReader.ReadResource(stream,metadata,child,cancellation).Bytes,true);
                        var converted=BitmapConverter.Convert(images.Slice(j*40,40),resource,chunks.Select(x=>ModulePayloadReader.ReadResource(stream,metadata,x,cancellation).Bytes).ToArray(),TextureAddresses.BuiltIn,cancellation);
                        if(converter.Convert("bitm/resource",new TagDocument(converted.Resource,true)).Method!="NativeLayout")throw Invalid("A converted menu picture does not match Forge's native layout.");
                        Add(child,converted.Resource);for(var k=0;k<chunks.Length;k++)Add(chunks[k],converted.Chunks[k]);imagesDone++;
                    }
                }
            }
            var ordered=items.Where(x=>x.Entry.Parent==-1).Concat(items.Where(x=>x.Entry.Parent!=-1)).ToArray();var mapping=ordered.Select((x,i)=>(x.File,x.Entry.Index,New:i)).ToDictionary(x=>(x.File,x.Index),x=>x.New);
            using var strings=new MemoryStream();List<int> resources=[];List<ModuleWriteEntry> entries=[];
            foreach(var item in ordered)
            {
                var raw=(byte[])item.Entry.Raw.Clone();W(raw,0,checked((int)strings.Position));W(raw,4,item.Entry.Parent<0?-1:mapping[(item.File,item.Entry.Parent)]);W(raw,8,item.Children.Length);W(raw,12,resources.Count);
                strings.Write(Encoding.UTF8.GetBytes(item.Entry.Name));strings.WriteByte(0);resources.AddRange(item.Children.Select(x=>mapping[(item.File,x)]));entries.Add(new(raw,()=>item.Payload is null?null:new(item.Payload)));
            }
            var key=InputFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(new{Rules,request.EffectivePlanId,Tags=tags.Select(x=>new{x.File,x.Item,x.Checksum}),Payloads=items.Select(x=>x.Payload is null?null:InputFiles.Hash(x.Payload))}));
            var header=new byte[56];"mohd"u8.CopyTo(header);Convert.FromHexString(key).AsSpan(0,8).CopyTo(header.AsSpan(8));
            var relative="game/menu-artwork/"+key.ToLowerInvariant()+"/h5solo-menu.module";var destination=SafePaths.Child(cache.Root,relative);
            var verified=ModuleWriter.Write(destination,new(header,entries.ToArray(),strings.ToArray(),resources.ToArray(),tags.Length),cancellation);
            var final=FileMetadata.Read(destination,"Module",cancellation);var identities=Enumerable.Range(0,tags.Length).Select(final.Entry).Select(x=>new MenuArtworkTag(x.Name,new(x.Group,x.TagId,x.AssetId),x.Checksum,x.Index)).ToArray();
            var manifest=new MenuArtworkManifest(1,Rules,request.EffectivePlanId,catalog.Fingerprint,"deploy/pc/levels/h5solo-menu.module",relative,verified,identities);var id=InputFiles.Save(cache.Root,"menu-artwork-manifests",manifest);
            return new("Prepared",id,imagesDone,verified.Bytes,Message:"Campaign menu artwork converted from the dump and verified.");
        }
        catch(OperationCanceledException){return new("Paused",Message:"Campaign menu artwork preparation paused.");}
        catch(Exception error){return new("Failed",Code:error is CacheException known?known.Code:"CAMPAIGN_ARTWORK_FAILED",Message:error.Message,Details:error.ToString());}
    }
    private static void W(byte[] bytes,int offset,int value)=>BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset),value);
    private static CacheException Invalid(string message)=>new("CAMPAIGN_ARTWORK_INVALID",message);
}
