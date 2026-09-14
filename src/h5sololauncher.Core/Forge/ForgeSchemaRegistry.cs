using System.Buffers.Binary;

namespace H5SoloLauncher.Core.Forge;

public interface IForgeMemory : IDisposable
{
    ulong ImageBase { get; }
    byte[] Read(ulong address, int count);
    void VerifyIdentity();
}

public sealed record NativeSchema(string Guid, uint Size, string Schema);
public sealed record NativeSchemaSnapshot(int Format, string Profile, string PackageFullName, NativeSchema[] Structures, NativeLegacyAcceptance[]? Legacy = null);

/// <summary>Version-specific, read-only registry decoding. No addresses escape the snapshot.</summary>
public static class ForgeSchemaRegistry
{
    public const string Profile = "forge-1.194.6192.2-schemas-1";
    public const string Package = "Microsoft.Halo5Forge_1.194.6192.2_x64__8wekyb3d8bbwe";
    public const uint LookupRva = 0x29ee790;
    public const uint RegistryRva = 0x6cb56d8;
    public static ReadOnlySpan<byte> LookupGuard => Convert.FromHexString(
        "4883ec084c8b153d6f2c044c8bd948891c24498bc2498b5208807a190075434c8b4908488b4a28493bc97c137509498b0b48394a207c08488bc2488b12eb04488b5210807a190074da493bc27414488b50284c3bca7c0b750c488b482049390b7d03498bc2488b1c24493bc274094883c0304883c408c333c04883c408c3");

    public static NativeSchemaSnapshot Capture(string package, IForgeMemory memory, CancellationToken cancellation = default)
    {
        if (package != Package) throw new CacheException("FORGE_VERSION_UNSUPPORTED", "This Forge version needs a different campaign compatibility profile. Update the launcher before preparing this game.");
        memory.VerifyIdentity();
        var guard = memory.Read(checked(memory.ImageBase + LookupRva), LookupGuard.Length);
        if (!guard.AsSpan().SequenceEqual(LookupGuard)) throw new CacheException("FORGE_SCHEMA_GUARD", "Forge's schema lookup differs from the supported game build. No campaign changes were applied.");
        var first = Read(memory, cancellation); memory.VerifyIdentity();
        var second = Read(memory, cancellation); memory.VerifyIdentity();
        if (!first.SequenceEqual(second) || !memory.Read(checked(memory.ImageBase + LookupRva), LookupGuard.Length).AsSpan().SequenceEqual(guard))
            throw new CacheException("FORGE_SCHEMA_CHANGED", "Forge's layouts changed while they were being read. Wait for startup to finish and retry preparation.");
        return new(1, Profile, package, first);
    }

    private static NativeSchema[] Read(IForgeMemory memory, CancellationToken cancellation)
    {
        static ulong Q(byte[] bytes, int at = 0) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at));
        static void Pointer(ulong value)
        {
            if (value < 0x10000 || value > 0x00007ffffffffff0 || (value & 7) != 0)
                throw new CacheException("FORGE_SCHEMA_POINTER", "Forge returned an invalid schema registry address.");
        }
        var sentinel = Q(memory.Read(checked(memory.ImageBase + RegistryRva), 8)); Pointer(sentinel);
        var root = Q(memory.Read(checked(sentinel + 8), 8)); Pointer(root);
        Stack<ulong> pending = new(); pending.Push(root);
        HashSet<ulong> visited = []; Dictionary<string, NativeSchema> structures = new(StringComparer.Ordinal);
        while (pending.TryPop(out var address))
        {
            cancellation.ThrowIfCancellationRequested(); Pointer(address);
            if (address == sentinel) continue;
            if (!visited.Add(address) || visited.Count > 20000) throw new CacheException("FORGE_SCHEMA_TREE", "Forge's schema registry contains a cycle or exceeds the supported size.");
            var node = memory.Read(address, 56);
            if (node[25] != 0) throw new CacheException("FORGE_SCHEMA_TREE", "An unexpected sentinel was found in Forge's schema registry.");
            pending.Push(Q(node)); pending.Push(Q(node, 16));
            var definition = Q(node, 48); Pointer(definition);
            var data = memory.Read(definition, 0x98);
            if (!data.AsSpan(16, 16).SequenceEqual(node.AsSpan(32, 16))) continue; // The registry also contains non-structure definitions.
            var guid = Convert.ToHexString(node.AsSpan(32, 16)).ToLowerInvariant();
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(40));
            if (size > 64 * 1024 * 1024) throw new CacheException("FORGE_SCHEMA_SIZE", "A Forge structure exceeds the supported size.");
            var schema = new NativeSchema(guid, size, Q(data, 144).ToString("x16"));
            if (!structures.TryAdd(guid, schema)) throw new CacheException("FORGE_SCHEMA_DUPLICATE", "Forge contains duplicate schema identities.");
        }
        if (structures.Count < 100) throw new CacheException("FORGE_SCHEMAS_NOT_READY", "Forge is still loading its layouts. Wait for startup to finish, then resume preparation.");
        return structures.Values.OrderBy(x => x.Guid, StringComparer.Ordinal).ToArray();
    }
}
