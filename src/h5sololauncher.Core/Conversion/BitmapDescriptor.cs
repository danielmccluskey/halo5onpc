using System.Buffers.Binary;
using H5SoloLauncher.Core.Content;

namespace H5SoloLauncher.Core.Conversion;

public sealed record BitmapDescriptor(int Width, int Height, int Depth, int Kind, int Format, int Mips, int TileMode)
{
    public int Slices => Kind == 2 ? 6 : Depth;
    public (int Dxgi, int Bytes, int Block) FormatInfo => Format switch
    {
        32 => (56, 2, 1), // L16: R16_UNORM; preserve the original 16-bit samples.
        3 => (49, 2, 1), 24 => (2, 16, 1), 1 or 2 => (61, 1, 1), 11 => (87, 4, 1),
        14 => (71, 8, 4), 16 => (77, 16, 4), 25 => (10, 8, 1), 36 or 43 => (80, 8, 4),
        39 => (84, 16, 4), 45 => (83, 16, 4), 47 => (95, 16, 4), 49 => (98, 16, 4), 51 => (26, 4, 1),
        _ => throw new CacheException("BITMAP_FORMAT_UNSUPPORTED", $"Bitmap format {Format} needs a converter.")
    };
    public string Key => $"{Width}-{Height}-{Depth}-{Kind}-{Format}-{Mips}-{TileMode}";
    public static BitmapDescriptor Read(ReadOnlySpan<byte> image, TagDocument resource)
    {
        if (image.Length != 40 || resource.Blocks.Length < 2 || resource.Blocks[0].Size != 80) throw new CacheException("BITMAP_LAYOUT", "The bitmap image or resource layout is unsupported.");
        var value = new BitmapDescriptor(U(image, 0), U(image, 2), U(image, 4), U(image, 6), U(image, 8), image[16] + 1, resource.Block(0)[32]);
        if (value.Width is < 1 or > 8192 || value.Height is < 1 or > 8192 || value.Depth is < 1 or > 128 || value.Kind is < 0 or > 3 ||
            value.Mips is < 1 or > 16 || value.TileMode is not (13 or 14) || (value.Kind == 0 && value.Depth != 1))
            throw new CacheException("BITMAP_DIMENSIONS", "The bitmap dimensions or tiling mode exceed the supported range.");
        _ = value.FormatInfo; return value;
    }
    private static ushort U(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt16LittleEndian(bytes[at..]);
}
