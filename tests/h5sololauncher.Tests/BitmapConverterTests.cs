using System.Buffers.Binary;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Conversion;
using Xunit;

namespace H5SoloLauncher.Tests;

public sealed class BitmapConverterTests
{
    [Theory]
    [InlineData(64,64,4,3,14,7,14)]
    [InlineData(32,32,32,1,11,6,13)]
    [InlineData(512,512,1,0,32,1,14)]
    public void ConvertedTexturesPreserveEveryElementAcrossSlicesAndMipTail(int width,int height,int depth,int kind,int format,int mips,int tile)
    {
        var shape = new BitmapDescriptor(width,height,depth,kind,format,mips,tile);
        var profile = TextureAddresses.BuiltIn.For(shape);
        var tiled = new byte[checked((int)profile.MaximumEnd)];
        var expected = new List<byte>();
        for (var level = 0; level < profile.Mips.Length; level++)
        {
            var mip = profile.Mips[level];
            for (var slice = 0; slice < mip.Slices; slice++)
            for (var y = 0; y < mip.Height; y++)
            for (var x = 0; x < mip.Width; x++)
            {
                // Exercise every byte, including high sample bits and shrinking volume slices.
                var block = Enumerable.Range(0,shape.FormatInfo.Bytes).Select(b=>(byte)(level*29+slice*17+x*7+y*13+b*131)).ToArray();
                block.CopyTo(tiled, checked((int)TextureAddresses.Address(mip, x, y, slice)));
                expected.AddRange(block);
            }
        }
        const int header = 196;
        var resource = new byte[header + 80 + tiled.Length];
        "ucsh"u8.CopyTo(resource); W(resource, 4, 27);
        BinaryPrimitives.WriteUInt64LittleEndian(resource.AsSpan(8), 0x704947f99b79968a);
        W(resource, 32, 2); W(resource, 36, 2); W(resource, 40, 1);
        W(resource, 60, header); W(resource, 64, 80); W(resource, 68, tiled.Length);
        W(resource, 80, 80); resource[86] = 1;
        W(resource, 96, tiled.Length); resource[102] = 2;
        W(resource, 112 + 24, -1);
        W(resource, 144 + 20, -1); W(resource, 144 + 28, 48);
        W(resource, 176 + 8, 1);
        resource[header + 32] = (byte)tile; resource[header + 33] = kind==1 ? (byte)3 : (byte)0;
        tiled.CopyTo(resource, header + 80);
        var image = new byte[40];
        foreach(var (at,value) in new[]{(0,width),(2,height),(4,depth),(6,kind),(8,format)})
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(at),(ushort)value);
        image[16]=(byte)(mips-1);
        var result = BitmapConverter.Convert(image, new TagDocument(resource, true), [], TextureAddresses.BuiltIn);
        var native = new TagDocument(result.Resource, true);
        Assert.Equal("98820e556647dcae", native.Metadata.Schema);
        Assert.Equal(88, native.Blocks[0].Size);
        Assert.Equal(expected.ToArray(), native.Block(1).ToArray());
        Assert.Equal(expected.Count, BinaryPrimitives.ReadInt32LittleEndian(native.Block(0)[24..]));
        Assert.Equal(0, native.Block(0)[32]);
        Assert.Equal(kind==1 ? 1 : 0,native.Block(0)[33]);
        Assert.Empty(result.Chunks);
    }
    private static void W(byte[] bytes, int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at), value);
}
