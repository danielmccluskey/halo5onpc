using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Conversion;
using Xunit;

namespace H5SoloLauncher.Tests;

public class TextureAddressTests
{
    [Fact] public void ProducesMipThenSliceOrderWithAddressPermutation()
    {
        var descriptor = new BitmapDescriptor(2, 2, 2, 3, 2, 2, 13);
        var mip0 = new TextureMipAddresses(2, 2, 2, 0, [2], [1], [4], []);
        var mip1 = new TextureMipAddresses(1, 1, 2, 8, [], [], [1], []);
        var addresses = new TextureAddresses(new(1, "verified-texture-addresses-1", [new(descriptor.Key, [mip0, mip1], 10, 10, true)]));
        var result = addresses.Untile(descriptor, Enumerable.Range(0, 10).Select(x => (byte)x).ToArray());
        Assert.Equal(new byte[] { 0, 2, 1, 3, 4, 6, 5, 7 }, result[0]); Assert.Equal(new byte[] { 8, 9 }, result[1]);
    }
    [Fact] public void RejectsUnverifiedOrTruncatedLayouts()
    {
        var descriptor = new BitmapDescriptor(1, 1, 1, 0, 2, 1, 13);
        var profile = new TextureAddressProfile(descriptor.Key, [new(1, 1, 1, 0, [], [], [], [])], 1, 1, false);
        var addresses = new TextureAddresses(new(1, "verified-texture-addresses-1", [profile]));
        Assert.Throws<CacheException>(() => addresses.Untile(descriptor, [1]));
        addresses = new(new(1, "verified-texture-addresses-1", [profile with { Exhaustive = true }]));
        Assert.Equal("TEXTURE_SOURCE_TRUNCATED", Assert.Throws<CacheException>(() => addresses.Untile(descriptor, [])).Code);
    }
    [Fact] public void RejectsCoordinatesOutsideImage()
    {
        var mip = new TextureMipAddresses(1, 1, 1, 0, [], [], [], []);
        Assert.Throws<CacheException>(() => TextureAddresses.Address(mip, -1, 0, 0));
        Assert.Throws<CacheException>(() => TextureAddresses.Address(mip, 0, 1, 0));
    }
    [Fact] public void NonPowerOfTwoMicrotileOriginsRemainDistinct()
    {
        var mip = new TextureMipAddresses(9, 1, 1, 0, [1, 2, 4], [], [], [0, 64]);
        Assert.Equal(7ul, TextureAddresses.Address(mip, 7, 0, 0)); Assert.Equal(64ul, TextureAddresses.Address(mip, 8, 0, 0));
    }
}
