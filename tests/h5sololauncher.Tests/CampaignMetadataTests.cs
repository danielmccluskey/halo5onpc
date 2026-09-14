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
