using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Conversion;
using H5SoloLauncher.Core.Preparation;
using Xunit;

namespace H5SoloLauncher.Tests;

public sealed class ChiefAnimationCompatibilityTests
{
    [Fact]
    public void ReplacementIsRestrictedToTheCapturedChiefRevision()
    {
        var tag = new EffectiveTag("mission.module", 5, "Chief", new("jmad", "ac4f7bcc", "0"), "0", true,
            ChiefAnimationCompatibility.SourceSha256, 581829, null!, [10]);
        Assert.True(ChiefAnimationCompatibility.AppliesTo(tag));
        Assert.False(ChiefAnimationCompatibility.AppliesTo(tag with { Identity = tag.Identity with { TagId = "bf9b22a5" } }));
        Assert.False(ChiefAnimationCompatibility.AppliesTo(tag with { Identity = tag.Identity with { Group = "bipd" } }));
        Assert.False(ChiefAnimationCompatibility.AppliesTo(tag with { Sha256 = ChiefAnimationCompatibility.NativeSha256 }));
        Assert.False(ChiefAnimationCompatibility.AppliesTo(tag with { Sha256 = new string('0', 64) }));
    }

    [Fact]
    public void UnknownNativeRevisionCannotBeUsedAsAReplacement()
    {
        var error = Assert.Throws<CacheException>(() => new ChiefAnimationCompatibility([1], [2], [3]));
        Assert.Equal("CHIEF_ANIMATION_COMPATIBILITY", error.Code);
    }
}
