using System.Buffers.Binary;
using System.Numerics;

namespace H5SoloLauncher.Core.Content;

/// <summary>MurmurHash3 x64/128 first lane, with the game's 64-bit seed extension.</summary>
public static class ModuleChecksum
{
    public static ulong Hash(ReadOnlySpan<byte> bytes, ulong seed = 0)
    {
        unchecked
        {
            const ulong c1 = 0x87c37b91114253d5, c2 = 0x4cf5ad432745937f;
            ulong h1 = seed, h2 = seed; var at = 0;
            while (at <= bytes.Length - 16)
            {
                var k1 = BinaryPrimitives.ReadUInt64LittleEndian(bytes[at..]);
                var k2 = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(at + 8)..]);
                h1 ^= BitOperations.RotateLeft(k1 * c1, 31) * c2;
                h1 = (BitOperations.RotateLeft(h1, 27) + h2) * 5 + 0x52dce729;
                h2 ^= BitOperations.RotateLeft(k2 * c2, 33) * c1;
                h2 = (BitOperations.RotateLeft(h2, 31) + h1) * 5 + 0x38495ab5;
                at += 16;
            }
            var tail = bytes[at..]; ulong t1 = 0, t2 = 0;
            for (var i = 0; i < Math.Min(tail.Length, 8); i++) t1 |= (ulong)tail[i] << (i * 8);
            for (var i = 8; i < tail.Length; i++) t2 |= (ulong)tail[i] << ((i - 8) * 8);
            if (tail.Length > 8) h2 ^= BitOperations.RotateLeft(t2 * c2, 33) * c1;
            if (tail.Length > 0) h1 ^= BitOperations.RotateLeft(t1 * c1, 31) * c2;
            h1 ^= (ulong)bytes.Length; h2 ^= (ulong)bytes.Length; h1 += h2; h2 += h1;
            return Mix(h1) + Mix(h2);
        }
    }
    private static ulong Mix(ulong value)
    {
        unchecked { value ^= value >> 33; value *= 0xff51afd7ed558ccd; value ^= value >> 33; value *= 0xc4ceb9fe1a85ec53; return value ^ (value >> 33); }
    }
    public static ulong Metadata(ReadOnlySpan<byte> tables)
    {
        if (tables.Length < 56 || !tables[..4].SequenceEqual("mohd"u8) || BinaryPrimitives.ReadUInt32LittleEndian(tables[4..]) != 27)
            throw new CacheException("MODULE_CHECKSUM_HEADER", "Native metadata checksums require a revision 27 module.");
        static int U(ReadOnlySpan<byte> b, int at) => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(b[at..]));
        var entriesEnd = checked(56 + U(tables, 16) * 88); var resourcesAt = checked(entriesEnd + U(tables, 28));
        var blocksAt = checked(resourcesAt + U(tables, 32) * 4); var end = checked(blocksAt + U(tables, 36) * 32);
        if (end != tables.Length) throw new CacheException("MODULE_CHECKSUM_TABLES", "Module table sizes do not match their header.");
        Span<byte> header = stackalloc byte[56]; tables[..56].CopyTo(header); header[48..56].Clear();
        var hash = Hash(header); hash = Hash(tables[56..entriesEnd], hash);
        hash = Hash(tables[resourcesAt..blocksAt], hash); return Hash(tables[blocksAt..], hash);
    }
}
