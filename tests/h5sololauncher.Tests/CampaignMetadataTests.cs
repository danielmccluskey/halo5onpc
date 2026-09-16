using System.Text;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Conversion;
using H5SoloLauncher.Core.Forge;
using H5SoloLauncher.Core.Runtime;
using Xunit;

namespace H5SoloLauncher.Tests;

public sealed class CampaignMetadataTests
{
    [Fact]
    public void SixthMissionIncludesMeridianRouteAndStopsBeforeReunion()
    {
        var (inputs,maps,modules,native)=Fixture();
        var scenarios=H5SoloLauncher.Core.Planning.ContentBundles.Get("first-six-missions").Scenarios;
        var slots=new[]{(0,0),(1,0),(1,1),(2,0),(2,1),(3,0),(4,0),(5,0)};
        var added=new List<AssembledModule>();
        for(var i=0;i<scenarios.Length;i++)
        {
            var scenario=scenarios[i];var path=scenario.Replace('/','\\');
            inputs[i]=inputs[i] with {SourcePath="__cms__/rtx/"+scenario+".mapinfo"};
            maps[i]=maps[i] with {Mission=slots[i].Item1,Sublevel=slots[i].Item2,Modules=Encoding.ASCII.GetBytes($"?any\\levels\\globals-rtx-\0?%(Platform)\\levels\\globals-rtx-\0?any\\{path}-rtx-\0?%(Platform)\\{path}-rtx-\0<2>1\0<3>1\0")};
            foreach(var platform in new[]{"any","pc"})
                added.Add(modules.Modules[0] with {OriginalPath=$"deploy/{platform}/{scenario}-rtx-1.module"});
        }
        maps[8]=maps[8] with {Mission=6,Sublevel=0};
        for(var i=9;i<maps.Length;i++) maps[i]=maps[i] with {Mission=6+(i-8)/2,Sublevel=(i-8)%2};
        var result=CampaignMetadata.Build(inputs,maps,modules with {Modules=[..added]},native,scenarios);
        Assert.Equal(8,result.Count(x=>x.Available));
        Assert.Equal(103u,result.Single(x=>x.MapId==102).NextMapId);
        Assert.Equal(104u,result.Single(x=>x.MapId==103).NextMapId);
        Assert.Equal(105u,result.Single(x=>x.MapId==104).NextMapId);
        Assert.True(result.Single(x=>x.MapId==105).Available);
        Assert.Equal(106u,result.Single(x=>x.MapId==105).NextMapId);
        Assert.True(result.Single(x=>x.MapId==106).Available);
        Assert.True(result.Single(x=>x.MapId==107).Available);
        Assert.Equal(108u,result.Single(x=>x.MapId==107).NextMapId);
        Assert.False(result.Single(x=>x.MapId==108).Available);
        Assert.Throws<CacheException>(()=>CampaignMetadata.Build(inputs,maps,modules with {Modules=added.Where(x=>!x.OriginalPath.Contains("w1_evacuation")).ToArray()},native,scenarios));
    }
    [Fact]
    public void FourthMissionFollowsGlassedAndStopsBeforeUnconfirmed()
    {
        var (inputs,maps,modules,native)=Fixture();
        var scenarios=H5SoloLauncher.Core.Planning.ContentBundles.Get("first-four-missions").Scenarios;
        var slots=new[]{(0,0),(1,0),(1,1),(2,0),(2,1),(3,0)};
        var added=new List<AssembledModule>();
        for(var i=0;i<scenarios.Length;i++)
        {
            var scenario=scenarios[i];var path=scenario.Replace('/','\\');
            inputs[i]=inputs[i] with {SourcePath="__cms__/rtx/"+scenario+".mapinfo"};
            maps[i]=maps[i] with {Mission=slots[i].Item1,Sublevel=slots[i].Item2,Modules=Encoding.ASCII.GetBytes($"?any\\levels\\globals-rtx-\0?%(Platform)\\levels\\globals-rtx-\0?any\\{path}-rtx-\0?%(Platform)\\{path}-rtx-\0<2>1\0<3>1\0")};
            foreach(var platform in new[]{"any","pc"})
                added.Add(modules.Modules[0] with {OriginalPath=$"deploy/{platform}/{scenario}-rtx-1.module"});
        }
        maps[6]=maps[6] with {Mission=4,Sublevel=0};
        for(var i=7;i<maps.Length;i++) maps[i]=maps[i] with {Mission=4+(i-6)/2,Sublevel=(i-6)%2};
        var result=CampaignMetadata.Build(inputs,maps,modules with {Modules=[..added]},native,scenarios);
        Assert.Equal(6,result.Count(x=>x.Available));
        Assert.Equal(103u,result.Single(x=>x.MapId==102).NextMapId);
        Assert.Equal(104u,result.Single(x=>x.MapId==103).NextMapId);
        Assert.Equal(105u,result.Single(x=>x.MapId==104).NextMapId);
        Assert.True(result.Single(x=>x.MapId==105).Available);
        Assert.Equal(106u,result.Single(x=>x.MapId==105).NextMapId);
        Assert.False(result.Single(x=>x.MapId==106).Available);
        Assert.Throws<CacheException>(()=>CampaignMetadata.Build(inputs,maps,modules with {Modules=added.Where(x=>!x.OriginalPath.Contains("w1_miningtown")).ToArray()},native,scenarios));
    }
    [Fact]
    public void ThirdMissionIncludesItsOpeningAndStopsAtTheNextUnpreparedMission()
    {
        var (inputs,maps,modules,native)=Fixture();
        var scenarios=H5SoloLauncher.Core.Planning.ContentBundles.Get("first-three-missions").Scenarios;
        var slots=new[]{(0,0),(1,0),(1,1),(2,0),(2,1)};
        var added=new List<AssembledModule>();
        for(var i=0;i<scenarios.Length;i++)
        {
            var scenario=scenarios[i];var path=scenario.Replace('/','\\');
            inputs[i]=inputs[i] with {SourcePath="__cms__/rtx/"+scenario+".mapinfo"};
            maps[i]=maps[i] with {Mission=slots[i].Item1,Sublevel=slots[i].Item2,Modules=Encoding.ASCII.GetBytes($"?any\\levels\\globals-rtx-\0?%(Platform)\\levels\\globals-rtx-\0?any\\{path}-rtx-\0?%(Platform)\\{path}-rtx-\0<2>1\0<3>1\0")};
            foreach(var platform in new[]{"any","pc"})
                added.Add(modules.Modules[0] with {OriginalPath=$"deploy/{platform}/{scenario}-rtx-1.module"});
        }
        maps[5]=maps[5] with {Mission=3,Sublevel=0};
        for(var i=6;i<maps.Length;i++) maps[i]=maps[i] with {Mission=3+(i-5)/2,Sublevel=(i-5)%2};
        var result=CampaignMetadata.Build(inputs,maps,modules with {Modules=[..added]},native,scenarios);
        Assert.Equal(5,result.Count(x=>x.Available));
        Assert.Equal(103u,result.Single(x=>x.MapId==102).NextMapId);
        Assert.Equal(104u,result.Single(x=>x.MapId==103).NextMapId);
        Assert.Equal(105u,result.Single(x=>x.MapId==104).NextMapId);
        Assert.False(result.Single(x=>x.MapId==105).Available);
        Assert.Throws<CacheException>(()=>CampaignMetadata.Build(inputs,maps,modules with {Modules=added.Where(x=>!x.OriginalPath.Contains("cin_060")).ToArray()},native,scenarios));
    }
    [Fact]
    public void OrdersNativeRevisionsAndPreservesSuccessorsIncludingUnavailableMaps()
    {
        var (inputs,maps,modules,native)=Fixture();
        var result=CampaignMetadata.Build(inputs,maps,modules,native,["levels/campaignworld030/map0/map0"]);
        Assert.True(result[0].Available); Assert.False(result[1].Available); Assert.Equal(101u,result[0].NextMapId);
        var list=Encoding.ASCII.GetString(result[0].TargetModules).Split('\0');
        Assert.Equal(new[]{"<0>1","<1>1","<0>20-1","<1>20-1","<1>21-1","<2>1","<3>1"},list.Skip(4).Take(7));
        Assert.Equal(maps[0].Modules,result[0].SourceModules);
    }
    [Fact]
    public void RejectsMissingMissionModuleAndMetadataIdentityMismatch()
    {
        var (inputs,maps,modules,native)=Fixture();
        Assert.Throws<CacheException>(()=>CampaignMetadata.Build(inputs,maps,modules with {Modules=modules.Modules[1..]},native,["levels/campaignworld030/map0/map0"]));
        inputs[0]=inputs[0] with {SourcePath="__cms__/rtx/levels/unrelated.mapinfo"};
        Assert.Throws<CacheException>(()=>CampaignMetadata.Build(inputs,maps,modules,native,["levels/campaignworld030/map0/map0"]));
    }
    [Theory]
    [InlineData("?any\\x\0?p\\x\0?any\\a\0?p\\a\0<2>1\0<2>1\0")]
    [InlineData("?any\\x\0?p\\x\0?any\\a\0?p\\a\0<2>../../escape\0")]
    [InlineData("?any\\x\0?p\\x\0?any\\a\0?p\\a\0")]
    public void RejectsAmbiguousOrEscapingModuleLists(string data)
    {
        var (inputs,maps,modules,native)=Fixture(); maps[0]=maps[0] with {Modules=Encoding.ASCII.GetBytes(data)};
        Assert.Throws<CacheException>(()=>CampaignMetadata.Build(inputs,maps,modules,native,["levels/campaignworld030/map0/map0"]));
    }
    private static (CampaignMapInput[],CampaignMapMetadata[],AssembledCampaign,NativeModuleManifest) Fixture()
    {
        var inputs=Enumerable.Range(0,19).Select(i=>new CampaignMapInput($"__cms__/rtx/levels/campaignworld030/map{i}/map{i}.mapinfo",$"game/metadata/{i}.mapinfo",new string('A',64))).ToArray();
        var maps=Enumerable.Range(0,19).Select(i=>new CampaignMapMetadata((uint)(100+i),i/2,i%2,Encoding.ASCII.GetBytes($"?any\\levels\\globals-rtx-\0?%(Platform)\\levels\\globals-rtx-\0?any\\levels\\campaignworld030\\map{i}\\map{i}-rtx-\0?%(Platform)\\levels\\campaignworld030\\map{i}\\map{i}-rtx-\0<0>99-1\0<1>99-1\0<2>1\0<3>1\0"))).ToArray();
        AssembledModule Module(string path,bool shared=false)=>new(path,"output", "key",new(0,"hash","table",1,1,1),[],shared);
        var modules=new AssembledCampaign(1,ModuleAssembly.Rules,"plan","assets","shaders","package",[
            Module("deploy/any/levels/campaignworld030/map0/map0-rtx-1.module"),Module("deploy/pc/levels/campaignworld030/map0/map0-rtx-1.module"),Module("deploy/pc/levels/globals-rtx-21-1.module",true)],[]);
        var native=new NativeModuleManifest(1,NativeModuleCache.Rules,"package","catalog",new[]{"pc/levels/globals-rtx-20-1.module","any/levels/globals-rtx-1.module","any/levels/globals-rtx-20-1.module","pc/levels/globals-rtx-1.module"}.Select(x=>new CachedNativeModule("deploy/"+x,"input","hash","table",0,0)).ToArray());
        return (inputs,maps,modules,native);
    }
}
