using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Planning;
using H5SoloLauncher.Core.Preparation;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Core.Runtime;

public sealed record MenuConfigurationRequest(InputRequest Inputs,string SourcesId,string ArtworkId);
public sealed record MenuConfigurationResult(string State,string? ConfigId=null,int OptionalSounds=0,string? Code=null,string? Message=null,string? Details=null);
public static class MenuConfiguration
{
    public static MenuConfigurationResult Run(MenuConfigurationRequest request,CancellationToken cancellation)
    {
        try
        {
            var input=request.Inputs;var source=SourceDiscovery.Describe(input.SourceRoot);var cache=CacheFolders.Open(input.CacheRoot,source.Root,input.ForgeRoot);
            using var lease=new FileStream(SafePaths.Child(cache.Root,"index.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            using var catalog=new CatalogSnapshot(cache,source);BuildPlanStore.ReadVerified(cache,catalog,input.PlanId);
            var menu=InputFiles.Read<MenuSourceManifest>(cache.Root,InputFiles.PathFor("menu-source-manifests",request.SourcesId),4*1024*1024,request.SourcesId);
            var artwork=InputFiles.Read<MenuArtworkManifest>(cache.Root,InputFiles.PathFor("menu-artwork-manifests",request.ArtworkId),1024*1024,request.ArtworkId);
            if(menu.Format!=1 || menu.Rules!=MenuSources.Rules || menu.SourcePlanId!=input.PlanId || menu.SourceFingerprint!=catalog.Fingerprint || menu.PackageFullName!=input.PackageFullName || artwork.Format!=1 || artwork.Rules!=MenuArtwork.Rules || artwork.SourceFingerprint!=catalog.Fingerprint)throw InputFiles.Damaged();
            using var stream=new MemoryStream();using var writer=new BinaryWriter(stream,Encoding.UTF8,true);
            void Blob(byte[] value){writer.Write(value.Length);writer.Write(value);}
            void Text(string value)=>Blob(new UTF8Encoding(false,true).GetBytes(value));
            void Identity(AssetReference identity,string checksum){writer.Write(uint.Parse(identity.TagId,NumberStyles.HexNumber));writer.Write(ulong.Parse(identity.AssetId,NumberStyles.HexNumber));writer.Write(ulong.Parse(checksum,NumberStyles.HexNumber));}
            writer.Write(0x4E4D3548u);writer.Write(1);Text(input.PackageFullName);Text(artwork.OriginalPath);writer.Write(artwork.Tags.Length);
            foreach(var tag in artwork.Tags){Identity(tag.Identity,tag.Checksum);Text(tag.Name);}
            var roles=new[]{"MainLua","MainWpf","CampaignLua","CampaignWpf","SkullStrings","MenuGraph","BackgroundLua","BackgroundWpf"};
            if(menu.Tags.Length!=roles.Length || !roles.All(x=>menu.Tags.Count(y=>y.Role==x)==1))throw Invalid("The campaign menu has missing or duplicate components.");
            writer.Write(roles.Length);var optional=0;
            foreach(var role in roles)
            {
                cancellation.ThrowIfCancellationRequested();var item=menu.Tags.Single(x=>x.Role==role);var native=new TagDocument(MenuSources.ReadPayload(cache.Root,item.Native.PayloadSha256));
                var candidate=item.Source is null?native:new TagDocument(MenuSources.ReadPayload(cache.Root,item.Source.PayloadSha256));
                writer.Write(Array.IndexOf(roles,role));Identity(item.Native.Identity,item.Native.Checksum);writer.Write(native.Blocks[0].Size);
                if(role=="MenuGraph"){Blob([]);writer.Write(0);writer.Write(0);writer.Write(0);continue;}
                var blocks=Enumerable.Range(0,candidate.Blocks.Length).Select(x=>candidate.Block(x).ToArray()).ToArray();
                var fixes=candidate.Structures.Where(x=>x.Kind==1).Select(x=>(Offset:x.FieldOffset,Target:x.Target,Length:-1)).Concat(candidate.DataReferences.Select(x=>(Offset:x.FieldOffset,Target:x.Target,Length:x.FieldOffset+24))).ToList();
                byte[] expected=[];
                if(role is "MainLua" or "MainWpf")
                {
                    var offset=role=="MainLua"?20:160;var original=native.DataReferences.Single(x=>x.FieldBlock==0 && x.FieldOffset==offset);var replacement=candidate.DataReferences.Single(x=>x.FieldBlock==0 && x.FieldOffset==offset);
                    expected=native.Block(original.Target).ToArray();var data=blocks[replacement.Target];
                    if(role=="MainWpf")data=XmlChange(data,"<halo:DoLuaAction LuaLine=\"OnLoaded()\"/>","<halo:DoLuaAction LuaLine=\"OnLoaded()\"/><halo:DoLuaAction LuaLine=\"OnListBoxSelectionChange()\"/>");
                    blocks=[native.Block(0).ToArray(),data];fixes=[(offset,1,offset+24)];
                }
                if(role=="CampaignLua")
                {
                    var data=candidate.DataReferences.Single(x=>x.FieldOffset==20).Target;
                    byte[] Constant(string value){var bytes=Encoding.ASCII.GetBytes(value+'\0');var output=new byte[8+bytes.Length];BinaryPrimitives.WriteUInt64BigEndian(output,checked((ulong)bytes.Length));bytes.CopyTo(output,8);return output;}
                    blocks[data]=ReplaceOnce(blocks[data],Constant("goto_campaign_background"),Constant("goto_theater"));
                }
                if(role=="CampaignWpf")
                {
                    var data=candidate.DataReferences.Single(x=>x.FieldOffset==160).Target;blocks[data]=XmlChange(blocks[data],"Text=\"{halo:Loc found}\"","Text=\"\"");
                }
                if(role=="SkullStrings")
                {
                    var merged=MergeStrings(native,candidate);blocks=[native.Block(0).ToArray(),merged.Records,merged.Text];fixes=[(16,1,-1),(72,2,96)];
                    BinaryPrimitives.WriteInt32LittleEndian(blocks[0].AsSpan(32),merged.Records.Length/72);
                }
                Blob(expected);writer.Write(blocks.Length);foreach(var block in blocks)Blob(block);
                writer.Write(fixes.Count);
                foreach(var fix in fixes)
                {
                    if(fix.Offset<16 || fix.Offset+28>blocks[0].Length || fix.Target< -1 || fix.Target>=blocks.Length)throw Invalid("A campaign menu pointer lies outside its supported root.");
                    writer.Write(fix.Offset);writer.Write(fix.Target);writer.Write(fix.Length);
                }
                var bindings=role is "MainLua" or "MainWpf" or "SkullStrings"?[]:item.Bindings;writer.Write(bindings.Length);
                foreach(var binding in bindings)
                {
                    var target=binding.Target;var image=artwork.Tags.SingleOrDefault(x=>x.Identity.Group==binding.Source.Group && x.Identity.TagId==binding.Source.TagId);
                    var sound=target is null && image is null && binding.Source.Group=="snd!" && new[]{"824b8d73","a2871547","5ffafede","a875ae6a","b130f8bb"}.Contains(binding.Source.TagId);
                    if(target is null && image is null && !sound)throw Invalid("A required campaign menu resource has no verified binding: "+binding.Name);
                    if(sound)optional++;
                    candidate.Field(binding.Block,binding.Offset,32);writer.Write(binding.Block);writer.Write(binding.Offset);writer.Write(Encoding.UTF8.GetByteCount(binding.Name??""));
                    writer.Write(sound?1:0);Identity(target?.Identity??image?.Identity??binding.Source,target?.Checksum??image?.Checksum??"0");
                }
            }
            writer.Flush();if(stream.Length>16*1024*1024)throw Invalid("Campaign menu data exceeds its supported size.");var bytes=stream.ToArray();var id=InputFiles.Hash(bytes);InputFiles.Write(cache.Root,InputFiles.PathFor("menu-config",id,".bin"),bytes);
            return new("Prepared",id,optional,Message:"Campaign menu configuration prepared from the dump.");
        }
        catch(OperationCanceledException){return new("Paused",Message:"Campaign menu preparation paused.");}
        catch(Exception error){return new("Failed",Code:error is CacheException known?known.Code:"CAMPAIGN_MENU_CONFIGURATION",Message:error.Message,Details:error.ToString());}
    }
    public static (byte[] Records,byte[] Text) MergeStrings(TagDocument native,TagDocument source)
    {
        if(native.Blocks[0].Size!=168 || source.Blocks[0].Size!=168)throw Invalid("Campaign localization has an unsupported root.");
        SortedDictionary<uint,byte[]> records=[];using var text=new MemoryStream();
        foreach(var document in new[]{native,source})
        {
            var rows=document.Block(1);var data=document.Block(2);if(rows.Length%72!=0 || data.Length>8*1024*1024)throw Invalid("Campaign localization is malformed.");
            var start=checked((int)text.Length);text.Write(data);
            for(var i=0;i<rows.Length;i+=72)
            {
                var row=rows.Slice(i,72).ToArray();var id=BinaryPrimitives.ReadUInt32LittleEndian(row);
                for(var language=0;language<17;language++)
                {
                    var offset=4+language*4;var at=BinaryPrimitives.ReadInt32LittleEndian(row.AsSpan(offset));
                    if(at== -1)continue;if(at<0 || at>=data.Length || data[at..].IndexOf((byte)0)<0)throw Invalid("Campaign localization contains an invalid text offset.");
                    BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(offset),checked(start+at));
                }
                if(!records.TryAdd(id,row))throw Invalid("Campaign localization has conflicting string identifiers.");
            }
        }
        return(records.Values.SelectMany(x=>x).ToArray(),text.ToArray());
    }
    private static byte[] XmlChange(byte[] bytes,string from,string to)
    {
        var result=ReplaceOnce(bytes,Encoding.UTF8.GetBytes(from),Encoding.UTF8.GetBytes(to));var value=new UTF8Encoding(false,true).GetString(result).TrimEnd('\0').TrimStart('\uFEFF');
        _=XDocument.Parse(value,LoadOptions.PreserveWhitespace);return result;
    }
    private static byte[] ReplaceOnce(byte[] bytes,byte[] from,byte[] to)
    {
        var at=bytes.AsSpan().IndexOf(from);if(at<0 || bytes.AsSpan(at+from.Length).IndexOf(from)>=0)throw Invalid("A required campaign menu action is missing or ambiguous.");
        var result=new byte[bytes.Length-from.Length+to.Length];bytes.AsSpan(0,at).CopyTo(result);to.CopyTo(result,at);bytes.AsSpan(at+from.Length).CopyTo(result.AsSpan(at+to.Length));return result;
    }
    private static CacheException Invalid(string message)=>new("CAMPAIGN_MENU_CONFIGURATION",message);
}
