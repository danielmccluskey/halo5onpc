using System.IO.Compression;
using System.Numerics;
using System.Text.Json;

namespace H5SoloLauncher.Core.Conversion;

public sealed record TextureMipAddresses(int Width, int Height, int Slices, ulong Origin, ulong[] X, ulong[] Y, ulong[] Z, ulong[] Tiles);
public sealed record TextureAddressProfile(string Key, TextureMipAddresses[] Mips, long ElementsChecked, long MaximumEnd, bool Exhaustive);
public sealed record TextureAddressCollection(int Format, string Rules, TextureAddressProfile[] Profiles);

/// <summary>Portable numeric address rules, exhaustively checked at build time. No SDK library is used by the app.</summary>
public sealed class TextureAddresses
{
    private readonly Dictionary<string, TextureAddressProfile> profiles;
    private static readonly Lazy<TextureAddresses> builtIn = new(() =>
    {
        using var resource = typeof(TextureAddresses).Assembly.GetManifestResourceStream("H5SoloLauncher.Core.Conversion.texture-addresses.json.gz") ??
            throw new CacheException("TEXTURE_RULES_MISSING", "This launcher is missing its texture conversion rules.");
        using var gzip = new GZipStream(resource, CompressionMode.Decompress);
        var collection = JsonSerializer.Deserialize<TextureAddressCollection>(gzip) ?? throw Invalid();
        return new(collection);
    });
    public static TextureAddresses BuiltIn => builtIn.Value;
    public TextureAddresses(TextureAddressCollection collection)
    {
        if (collection.Format != 1 || collection.Rules != "verified-texture-addresses-1" || collection.Profiles.Length is < 1 or > 4096) throw Invalid();
        profiles = collection.Profiles.ToDictionary(x => x.Key, StringComparer.Ordinal);
    }
    public TextureAddressProfile For(BitmapDescriptor descriptor)
    {
        if (!profiles.TryGetValue(descriptor.Key, out var profile)) throw new CacheException("TEXTURE_SHAPE_UNSUPPORTED", $"Texture shape {descriptor.Key} needs additional verified address rules. Update the launcher before converting this dump.");
        if (!profile.Exhaustive || profile.Mips.Length != descriptor.Mips || profile.MaximumEnd is <= 0 or > 512L * 1024 * 1024) throw Invalid();
        long count = 0; var format = descriptor.FormatInfo;
        for (var i = 0; i < profile.Mips.Length; i++)
        {
            var mip = profile.Mips[i]; var w = (Math.Max(1, descriptor.Width >> i) + format.Block - 1) / format.Block;
            var h = (Math.Max(1, descriptor.Height >> i) + format.Block - 1) / format.Block;
            var slices = descriptor.Kind == 1 ? Math.Max(1, descriptor.Depth >> i) : descriptor.Slices;
            if (mip.Width != w || mip.Height != h || mip.Slices != slices) throw Invalid();
            var tiled = mip.Tiles.Length != 0;
            if (mip.X.Length != Bits(tiled ? Math.Min(8, w) : w) || mip.Y.Length != Bits(tiled ? Math.Min(8, h) : h) ||
                mip.Z.Length != (tiled ? 0 : Bits(slices)) || (tiled && mip.Tiles.Length != checked(((w + 7) / 8) * ((h + 7) / 8) * slices))) throw Invalid();
            count += (long)w * h * slices;
        }
        if (count != profile.ElementsChecked) throw Invalid(); return profile;
    }
    public byte[][] Untile(BitmapDescriptor descriptor, byte[] source, CancellationToken cancellation = default)
    {
        var profile = For(descriptor); var bpe = descriptor.FormatInfo.Bytes;
        if (profile.MaximumEnd > source.LongLength) throw new CacheException("TEXTURE_SOURCE_TRUNCATED", "The tiled texture payload does not cover its verified address range.");
        byte[][] output = new byte[profile.Mips.Length][];
        for (var level = 0; level < output.Length; level++)
        {
            var mip = profile.Mips[level]; var bytes = checked((long)mip.Width * mip.Height * mip.Slices * bpe);
            if (bytes > 512L * 1024 * 1024) throw Invalid();
            output[level] = new byte[(int)bytes]; var cursor = 0;
            var tiled = mip.Tiles.Length > 0; var tileWidth = (mip.Width + 7) / 8; var tileHeight = (mip.Height + 7) / 8;
            var xs = Enumerable.Range(0, tiled ? Math.Min(8, mip.Width) : mip.Width).Select(x => Fold(x, mip.X)).ToArray();
            var ys = Enumerable.Range(0, tiled ? Math.Min(8, mip.Height) : mip.Height).Select(y => Fold(y, mip.Y)).ToArray();
            for (var z = 0; z < mip.Slices; z++)
            for (var y = 0; y < mip.Height; y++)
            {
                cancellation.ThrowIfCancellationRequested();
                var row = tiled ? ys[y & 7] : mip.Origin ^ Fold(z, mip.Z) ^ ys[y];
                for (var x = 0; x < mip.Width; x++)
                {
                    var offset = tiled ? mip.Tiles[(z * tileHeight + y / 8) * tileWidth + x / 8] ^ row ^ xs[x & 7] : row ^ xs[x];
                    if (offset % (ulong)bpe != 0 || offset > (ulong)(source.Length - bpe)) throw Invalid();
                    source.AsSpan(checked((int)offset), bpe).CopyTo(output[level].AsSpan(cursor, bpe)); cursor += bpe;
                }
            }
        }
        return output;
    }
    public static ulong Address(TextureMipAddresses mip, int x, int y, int z)
    {
        if ((uint)x >= mip.Width || (uint)y >= mip.Height || (uint)z >= mip.Slices) throw Invalid();
        return mip.Tiles.Length == 0 ? mip.Origin ^ Fold(x, mip.X) ^ Fold(y, mip.Y) ^ Fold(z, mip.Z) :
            mip.Tiles[(z * ((mip.Height + 7) / 8) + y / 8) * ((mip.Width + 7) / 8) + x / 8] ^ Fold(x & 7, mip.X) ^ Fold(y & 7, mip.Y);
    }
    private static ulong Fold(int coordinate, ulong[] values)
    {
        ulong result = 0; var bits = (uint)coordinate;
        while (bits != 0) { var bit = BitOperations.TrailingZeroCount(bits); if (bit >= values.Length) throw Invalid(); result ^= values[bit]; bits &= bits - 1; }
        return result;
    }
    private static int Bits(int count) => count <= 1 ? 0 : 32 - BitOperations.LeadingZeroCount((uint)(count - 1));
    private static CacheException Invalid() => new("TEXTURE_RULES_INVALID", "The texture address rules do not match the image descriptor. Keep the details for diagnosis.");
}
