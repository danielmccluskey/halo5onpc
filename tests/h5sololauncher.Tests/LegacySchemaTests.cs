using H5SoloLauncher.Core.Forge;
using Xunit;

namespace H5SoloLauncher.Tests;

public sealed class LegacySchemaTests
{
    [Theory]
    [InlineData("66fbc6ffc84e908860918db9de7c0afa", "379d646d6251b0b6", "0186802cf34ce737")]
    [InlineData("66fbc6ffc84e908860918db9de7c0afa", "c9f45d8ea14d4930", "0186802cf34ce737")]
    [InlineData("66fbc6ffc84e908860918db9de7c0afa", "69aab3d527310108", "0186802cf34ce737")]
    [InlineData("782ed8f78c4b07197294d480c100d67b", "b31dd66f224c1140", "c94e6a8320141460")]
    public void OlderWeaponsRequireCapturedAcceptanceAndExactNativeSchema(string guid,string source,string native)
    {
        var snapshot=new NativeSchemaSnapshot(1,ForgeSchemaRegistry.Profile,ForgeSchemaRegistry.Package,[new(guid,16,native)]);
        Assert.False(ForgeLegacySchemas.Accepts(snapshot,guid,source));
        snapshot=snapshot with {Legacy=[new(guid,source,native)]};
        Assert.True(ForgeLegacySchemas.Accepts(snapshot,guid,source));
        Assert.False(ForgeLegacySchemas.Accepts(snapshot with {PackageFullName="other"},guid,source));
        Assert.False(ForgeLegacySchemas.Accepts(snapshot with {Structures=[new(guid,16,"unknown")]},guid,source));
        Assert.False(ForgeLegacySchemas.Accepts(snapshot,guid,"ffffffffffffffff"));
    }
}
