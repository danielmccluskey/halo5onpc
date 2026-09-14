using System.Globalization;
using System.Text;
using System.Text.Json;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Forge;
using H5SoloLauncher.Core.Planning;
using H5SoloLauncher.Core.Preparation;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Core.Runtime;
public sealed record CompletionConfigurationRequest(InputRequest Inputs,string SourcesId,string CatalogueId);
public sealed record CompletionConfigurationResult(string State,string? ConfigId=null,string? Code=null,string? Message=null,string? Details=null);
public sealed record UiScriptEdit(int Offset,string Before,string After);
public sealed record UiScriptRecipe(string InputSha256,string OutputSha256,int InputBytes,int OutputBytes,UiScriptEdit[] Edits);
public static class CompletionConfiguration
{
    public static byte[] PatchUi(byte[] source)
        =>PatchUi(source,"completion-ui-recipe.json","COMPLETION_UI_UNSUPPORTED","campaign report");
    internal static byte[] PatchUi(byte[] source,string resource,string code,string label)
    {
        using var stream=typeof(CompletionConfiguration).Assembly.GetManifestResourceStream("H5SoloLauncher.Core.Runtime."+resource)
            ??throw new CacheException("UI_RECIPE_NOT_INCLUDED","This build supports existing-cache playback but does not include the UI recipes needed to prepare a new cache. Select a complete cache. See docs/publication.md for build details.");
        var recipe=JsonSerializer.Deserialize<UiScriptRecipe>(stream)??throw InputFiles.Damaged();
        if(source.Length!=recipe.InputBytes || InputFiles.Hash(source)!=recipe.InputSha256)throw new CacheException(code,$"The installed {label} script differs from the supported Forge version.");
        using var output=new MemoryStream();var cursor=0;
        foreach(var edit in recipe.Edits)
        {
            var before=Convert.FromHexString(edit.Before);var after=Convert.FromHexString(edit.After);
            if(edit.Offset<cursor || edit.Offset>source.Length-before.Length || !source.AsSpan(edit.Offset,before.Length).SequenceEqual(before))throw InputFiles.Damaged();
            output.Write(source.AsSpan(cursor,edit.Offset-cursor));output.Write(after);cursor=edit.Offset+before.Length;
        }
        output.Write(source.AsSpan(cursor));var result=output.ToArray();if(result.Length!=recipe.OutputBytes || InputFiles.Hash(result)!=recipe.OutputSha256)throw InputFiles.Damaged();return result;
    }
    public static CompletionConfigurationResult Prepare(CompletionConfigurationRequest request,IProgress<IndexProgress> progress,CancellationToken cancellation)
    {
        try
        {
            var input=request.Inputs;var source=SourceDiscovery.Describe(input.SourceRoot);var cache=CacheFolders.Open(input.CacheRoot,source.Root,input.ForgeRoot);
            using var lease=new FileStream(SafePaths.Child(cache.Root,"index.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            using var snapshot=new CatalogSnapshot(cache,source);BuildPlanStore.ReadVerified(cache,snapshot,input.PlanId);
            var menu=InputFiles.Read<MenuSourceManifest>(cache.Root,InputFiles.PathFor("menu-source-manifests",request.SourcesId),4*1024*1024,request.SourcesId);
            var catalogue=InputFiles.Read<CampaignCatalogue>(cache.Root,InputFiles.PathFor("catalogue-manifests",request.CatalogueId),1024*1024,request.CatalogueId);
            var native=InputFiles.Read<NativeModuleManifest>(cache.Root,InputFiles.PathFor("native-manifests",menu.NativeManifestId),1024*1024,menu.NativeManifestId);
            if(menu.SourceFingerprint!=snapshot.Fingerprint || menu.SourcePlanId!=input.PlanId || catalogue.SourceFingerprint!=snapshot.Fingerprint || catalogue.PackageFullName!=input.PackageFullName || native.PackageFullName!=input.PackageFullName)throw InputFiles.Damaged();
            Dictionary<string,(ModuleEntry Entry,byte[] Bytes)> hosts=[];
            foreach(var module in native.Modules.OrderBy(x=>EffectivePlanBuilder.Rank(x.Path,0)))
            {
                cancellation.ThrowIfCancellationRequested();progress.Report(new("Preparing mission completion support",0,0,module.Path));var path=SafePaths.Child(cache.Root,module.RelativePath);var metadata=FileMetadata.Read(path,"Module",cancellation);
                using var stream=File.OpenRead(path);if(stream.Length!=module.Length || InputFiles.Digest(stream,cancellation)!=module.Sha256 || metadata.Digest!=module.TableDigest)throw InputFiles.Damaged();
                foreach(var entry in Enumerable.Range(0,metadata.ItemCount).Select(metadata.Entry).Where(x=>x.StoredSize>0 && ((x.Group=="wpfs" && x.TagId=="f79a5480") || (x.Group=="luas" && x.TagId=="5887e62f"))))hosts[entry.TagId]=(entry,ModulePayloadReader.Read(stream,metadata,entry,cancellation).Bytes);
            }
            using var output=new MemoryStream();using var writer=new BinaryWriter(output,Encoding.UTF8,true);
            void Blob(byte[] bytes){writer.Write(bytes.Length);writer.Write(bytes);}
            void Text(string text)=>Blob(Encoding.UTF8.GetBytes(text));
            writer.Write(0x50433548u);writer.Write(1);Text(input.PackageFullName);
            foreach(var (id,offset) in new[]{("f79a5480",160),("5887e62f",20)})
            {
                if(!hosts.TryGetValue(id,out var host))throw new CacheException("COMPLETION_UI_MISSING","The installed Forge campaign report is incomplete.");var doc=new TagDocument(host.Bytes);var reference=doc.DataReferences.Single(x=>x.FieldBlock==0 && x.FieldOffset==offset);var bytes=doc.Block(reference.Target).ToArray();
                writer.Write(uint.Parse(id,NumberStyles.HexNumber));writer.Write(ulong.Parse(host.Entry.AssetId,NumberStyles.HexNumber));writer.Write(ulong.Parse(host.Entry.Checksum,NumberStyles.HexNumber));Blob(bytes);Blob(id=="5887e62f"?PatchUi(bytes):[]);
            }
            var routes=catalogue.Maps.Where(x=>x.Available).ToArray();writer.Write(routes.Length);
            foreach(var route in routes){writer.Write(route.MapId);writer.Write(route.NextMapId);Text(route.Scenario);Blob(route.TargetModules);}
            writer.Flush();var data=output.ToArray();var config=InputFiles.Hash(data);InputFiles.Write(cache.Root,InputFiles.PathFor("completion-config",config,".bin"),data);
            return new("Prepared",config,Message:"Mission reports and validated next-mission routes are prepared.");
        }
        catch(OperationCanceledException){return new("Paused",Message:"Mission completion preparation paused.");}
        catch(Exception error){return new("Failed",Code:error is CacheException known?known.Code:"COMPLETION_PREPARATION_FAILED",Message:error.Message,Details:error.ToString());}
    }
}
