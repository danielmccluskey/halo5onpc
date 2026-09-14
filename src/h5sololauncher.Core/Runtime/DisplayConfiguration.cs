using System.Globalization;
using System.Text;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Forge;
using H5SoloLauncher.Core.Planning;
using H5SoloLauncher.Core.Preparation;
using H5SoloLauncher.Core.Storage;
namespace H5SoloLauncher.Core.Runtime;

public static class DisplayConfiguration
{
    public static byte[] PatchUi(byte[] source)=>CompletionConfiguration.PatchUi(source,"display-ui-recipe.json","DISPLAY_UI_UNSUPPORTED","audio and video settings");
    public static string Prepare(InputRequest input,string sourcesId,IProgress<IndexProgress> progress,CancellationToken cancellation)
    {
        var source=SourceDiscovery.Describe(input.SourceRoot);var cache=CacheFolders.Open(input.CacheRoot,source.Root,input.ForgeRoot);
        using var lease=new FileStream(SafePaths.Child(cache.Root,"index.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        using var snapshot=new CatalogSnapshot(cache,source);BuildPlanStore.ReadVerified(cache,snapshot,input.PlanId);
        var menu=InputFiles.Read<MenuSourceManifest>(cache.Root,InputFiles.PathFor("menu-source-manifests",sourcesId),4*1024*1024,sourcesId);
        var native=InputFiles.Read<NativeModuleManifest>(cache.Root,InputFiles.PathFor("native-manifests",menu.NativeManifestId),1024*1024,menu.NativeManifestId);
        if(menu.SourceFingerprint!=snapshot.Fingerprint || menu.SourcePlanId!=input.PlanId || native.PackageFullName!=input.PackageFullName)throw InputFiles.Damaged();
        (ModuleEntry Entry,byte[] Bytes)? host=null;
        foreach(var module in native.Modules.OrderBy(x=>EffectivePlanBuilder.Rank(x.Path,0)))
        {
            cancellation.ThrowIfCancellationRequested();progress.Report(new("Preparing display settings",0,0,module.Path));
            var path=SafePaths.Child(cache.Root,module.RelativePath);var metadata=FileMetadata.Read(path,"Module",cancellation);
            using var stream=File.OpenRead(path);
            if(stream.Length!=module.Length || InputFiles.Digest(stream,cancellation)!=module.Sha256 || metadata.Digest!=module.TableDigest)throw InputFiles.Damaged();
            foreach(var entry in Enumerable.Range(0,metadata.ItemCount).Select(metadata.Entry).Where(x=>x.Group=="luas" && x.TagId=="003d87f1" && x.StoredSize>0))
                host=(entry,ModulePayloadReader.Read(stream,metadata,entry,cancellation).Bytes);
        }
        if(host is null)throw new CacheException("DISPLAY_UI_MISSING","The installed Forge audio and video settings script is missing.");
        var document=new TagDocument(host.Value.Bytes);var reference=document.DataReferences.Single(x=>x.FieldBlock==0 && x.FieldOffset==20);
        var before=document.Block(reference.Target).ToArray();var after=PatchUi(before);
        using var output=new MemoryStream();using var writer=new BinaryWriter(output,Encoding.UTF8,true);
        void Blob(byte[] bytes){writer.Write(bytes.Length);writer.Write(bytes);}
        writer.Write(0x53443548u);writer.Write(1);Blob(Encoding.UTF8.GetBytes(input.PackageFullName));
        writer.Write(uint.Parse(host.Value.Entry.TagId,NumberStyles.HexNumber));writer.Write(ulong.Parse(host.Value.Entry.AssetId,NumberStyles.HexNumber));writer.Write(ulong.Parse(host.Value.Entry.Checksum,NumberStyles.HexNumber));
        Blob(before);Blob(after);writer.Flush();var bytes=output.ToArray();var id=InputFiles.Hash(bytes);
        InputFiles.Write(cache.Root,InputFiles.PathFor("display-config",id,".bin"),bytes);return id;
    }
}
