using System.Buffers.Binary;
using System.Text;
using H5SoloLauncher.Core.Planning;

namespace H5SoloLauncher.Core.Content;

public static class TagDependencyReader
{
    public const string Version = "ucsh-dependencies-1";

    public static AssetReference[] Read(byte[] bytes)
    {
        if (bytes.Length < 80 || !bytes.AsSpan(0, 4).SequenceEqual("ucsh"u8)) Invalid("The tag header is not recognized.");
        long tableEnd = 80;
        int[] strides = [24, 16, 32, 20, 16, 8];
        for (var i = 0; i < strides.Length; i++) tableEnd += U(bytes, 28 + i * 4) * (long)strides[i];
        var strings = U(bytes, 52); var headerSize = U(bytes, 60);
        if (headerSize < 80 || headerSize > bytes.Length || tableEnd > headerSize || strings > headerSize - tableEnd)
            Invalid("Tag dependency tables or strings exceed the header bounds.");
        List<AssetReference> result = [];
        for (var i = 0; i < U(bytes, 28); i++)
        {
            var at = checked(80 + i * 24);
            var name = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 4));
            if (name < -1 || (name >= 0 && (name >= strings || Array.IndexOf(bytes, (byte)0, (int)tableEnd + name, (int)strings - name) < 0)))
                Invalid($"Dependency {i} has an invalid name reference.");
            var gid = U(bytes, at + 16);
            if (gid == uint.MaxValue) continue;
            var rawGroup = bytes.AsSpan(at, 4).ToArray(); Array.Reverse(rawGroup);
            if (rawGroup.Any(x => x < 32 || x > 126)) Invalid($"Dependency {i} has an unsupported tag group.");
            result.Add(new(Encoding.ASCII.GetString(rawGroup), gid.ToString("x8"),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at + 8)).ToString("x16")));
        }
        return result.Distinct().OrderBy(x => x.Group, StringComparer.Ordinal).ThenBy(x => x.TagId, StringComparer.Ordinal)
            .ThenBy(x => x.AssetId, StringComparer.Ordinal).ToArray();
    }
    private static uint U(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
    private static void Invalid(string message) => throw new CacheException("TAG_METADATA_INVALID", message);
}
