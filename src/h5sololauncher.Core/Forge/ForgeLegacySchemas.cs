namespace H5SoloLauncher.Core.Forge;

public sealed record NativeLegacyAcceptance(string Guid, string SourceSchema, string NativeSchema);

/// <summary>Records existing native compatibility paths; never patches validators or invents a schema.</summary>
public static class ForgeLegacySchemas
{
    private sealed record Guard(uint Rva, string Bytes);
    private static readonly Guard[] Guards =
    [
        new(0x29044b0, "493bd07503b001c3488d05e9390801488bc84c8d0def3908010f1f8000000000483911740c4883c108493bc975f232c0c3493bc9750a32c0c30f1f80000000004c390074094883c008493bc1ebe6493bc174e3483bc80f93c0c3"),
        new(0x14f54e0, "493bd07503b001c3488d05a9611102488bc84c8d0daf6111020f1f8000000000483911740c4883c108493bc975f232c0c3493bc9750a32c0c30f1f80000000004c390074094883c008493bc1ebe6493bc174e3483bc80f93c0c3"),
        new(0xa7ba60, "493bd0742148b8537170113925fc91483bd0750f48b8b7111d53d742c0924c3bc0740332c0c3b001c3"),
        new(0x119fac0, "493bd0742148b8bd5c29ef0f642262483bd0750f48b8a17106a79814a44f4c3bc0740332c0c3b001c3"),
        new(0x3987ea8, "1b08328ec63abd7526388b73cb72018f"),
        new(0x360b698, "57480236502293c30415b644b359534c")
    ];
    private static readonly NativeLegacyAcceptance[] Supported =
    [
        new("873a3c7935406c5095fc3bbfe49b45f0", "75bd3ac68e32081b", "8f0172cb738b3826"),
        new("0cf698cea2431e8337675597e2a20d2d", "4fa41498a70671a1", "6222640fef295cbd"),
        new("46e72696024560c8b6584cac8130f914", "c393225036024857", "4c5359b344b61504"),
        new("2e88aeef1d4dc90d6acb58a3405d8734", "92c042d7531d11b7", "91fc253911707153")
    ];

    public static NativeSchemaSnapshot Capture(NativeSchemaSnapshot snapshot, IForgeMemory memory)
    {
        if (snapshot.PackageFullName != ForgeSchemaRegistry.Package || snapshot.Profile != ForgeSchemaRegistry.Profile) throw Invalid();
        memory.VerifyIdentity();
        foreach (var guard in Guards)
        {
            var expected = Convert.FromHexString(guard.Bytes);
            if (!memory.Read(checked(memory.ImageBase + guard.Rva), expected.Length).AsSpan().SequenceEqual(expected)) throw Invalid();
        }
        foreach (var item in Supported)
            if (!snapshot.Structures.Any(x => x.Guid == item.Guid && x.Schema == item.NativeSchema)) throw Invalid();
        memory.VerifyIdentity();
        return snapshot with { Legacy = Supported.ToArray() };
    }

    public static bool Accepts(NativeSchemaSnapshot snapshot, string guid, string sourceSchema) =>
        snapshot.PackageFullName == ForgeSchemaRegistry.Package && snapshot.Profile == ForgeSchemaRegistry.Profile &&
        Supported.Any(x => x.Guid == guid && x.SourceSchema == sourceSchema && snapshot.Legacy?.Contains(x) == true &&
            snapshot.Structures.Any(s => s.Guid == guid && s.Schema == x.NativeSchema));

    private static CacheException Invalid() => new("FORGE_LEGACY_SCHEMA_GUARD", "Forge's built-in campaign layout compatibility differs from the supported build. Preparation stopped before applying game changes.");
}
