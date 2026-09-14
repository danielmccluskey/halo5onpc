using System.IO;
using System.Text;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Preparation;
using H5SoloLauncher.Core.Runtime;
using H5SoloLauncher.Core.Storage;
using Xunit;

namespace H5SoloLauncher.Tests;

public sealed class PreparedCampaignTests
{
    [Theory]
    [InlineData("menu-config")]
    [InlineData("ui-residency")]
    [InlineData("movie-config")]
    [InlineData("completion-config")]
    [InlineData("display-config")]
    public void MissingOrChangedRuntimeConfigurationRequiresPreparationAgain(string category)
    {
        using var fixture=new PlanFixture();var plan=fixture.Run().Summary!.PlanId;
        var root=fixture.Files.Cache;var package="fixture-package";var paths=new Dictionary<string,string>();
        string Write(string kind)
        {
            using var output=new MemoryStream();using var writer=new BinaryWriter(output,Encoding.UTF8,true);
            writer.Write(0x564d3548u);writer.Write(1);
            void Text(string value){var bytes=Encoding.UTF8.GetBytes(value);writer.Write(bytes.Length);writer.Write(bytes);}
            Text(package);Text(root);Text(kind);writer.Flush();var data=output.ToArray();var id=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));
            var path=PreparedCampaignStore.ConfigPath(root,kind,id);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllBytes(path,data);paths[kind]=path;return id;
        }
        var unused=new string('A',64);
        var prepared=new PreparedCampaign(1,PreparedCampaignStore.Rules,"",package,plan,unused,unused,unused,unused,unused,unused,unused,unused,
            Write("menu-config"),Write("ui-residency"),Write("movie-config"),Write("completion-config"),Write("display-config"));
        var id=PreparedCampaignStore.Save(new(fixture.Files.Root,root,"",package,plan),prepared);
        var locations=new PrepareRequest(fixture.Files.Root,root,"",package,"English(US)");
        Assert.Equal(id,PreparedCampaignStore.LatestId(locations));
        var path=paths[category];var original=File.ReadAllBytes(path);var damaged=original.ToArray();damaged[^1]^=1;File.WriteAllBytes(path,damaged);
        Assert.Null(PreparedCampaignStore.LatestId(locations));
        Assert.Equal("PREPARED_CONFIG_DAMAGED",Assert.Throws<CacheException>(()=>PreparedCampaignStore.Read(locations,id)).Code);
        File.WriteAllBytes(path,original);Assert.Equal(id,PreparedCampaignStore.LatestId(locations));
        File.Delete(path);Assert.Null(PreparedCampaignStore.LatestId(locations));
    }
}
