using System.Buffers.Binary;
using System.Text;
using H5SoloLauncher.Core.Preparation;

namespace H5SoloLauncher.Core.Content;

/// <summary>Portable source header evidence, not certification of a Forge schema.</summary>
public static class TagMetadataReader
{
    public static TagMetadata Read(byte[] bytes)
    {
        // Keep the established bounded dependency-table validation compatible with old plans.
        TagDependencyReader.Read(bytes);
        var header = U(bytes, 60);
        if (header + (long)U(bytes, 64) + U(bytes, 68) != bytes.Length)
            throw new CacheException("TAG_LAYOUT_INVALID", "The tag's header and data lengths do not match its payload.");
        int[] strides = [24, 16, 32, 20, 16, 8]; long stringsAt = 80;
        for (var i = 0; i < strides.Length; i++) stringsAt += U(bytes, 28 + i * 4) * (long)strides[i];
        List<NamedDependency> dependencies = [];
        for (var i = 0; i < U(bytes, 28); i++)
        {
            var at = checked(80 + i * 24); var gid = U(bytes, at + 16);
            if (gid == uint.MaxValue) continue;
            var group = bytes.AsSpan(at, 4).ToArray(); Array.Reverse(group);
            var nameAt = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 4)); string? name = null;
            if (nameAt >= 0)
            {
                var start = checked((int)stringsAt + nameAt);
                var end = Array.IndexOf(bytes, (byte)0, start, checked((int)U(bytes, 52) - nameAt));
                if (end - start > 32768) throw new CacheException("TAG_NAME_INVALID", "A dependency name exceeds the supported length.");
                try { name = new UTF8Encoding(false, true).GetString(bytes, start, end - start); }
                catch (DecoderFallbackException) { throw new CacheException("TAG_NAME_INVALID", "A dependency name is not valid UTF-8."); }
            }
            dependencies.Add(new(new(Encoding.ASCII.GetString(group), gid.ToString("x8"),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at + 8)).ToString("x16")), name));
        }
        var structures = checked(80 + (int)U(bytes, 28) * 24 + (int)U(bytes, 32) * 16);
        List<string> roots = [];
        for (var i = 0; i < U(bytes, 36); i++)
        {
            var at = checked(structures + i * 32); var kind = U(bytes, at + 16);
            if (kind is 0 or 65536) roots.Add(Convert.ToHexString(bytes.AsSpan(at, 16)).ToLowerInvariant());
        }
        return new(BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8)).ToString("x16"), roots.ToArray(),
            dependencies.Distinct().OrderBy(x => x.Identity.Group, StringComparer.Ordinal)
                .ThenBy(x => x.Identity.TagId, StringComparer.Ordinal).ThenBy(x => x.Identity.AssetId, StringComparer.Ordinal)
                .ThenBy(x => x.Name, StringComparer.Ordinal).ToArray());
    }
    private static uint U(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
}
