using System.Buffers.Binary;
using System.Text.Json;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Forge;

namespace H5SoloLauncher.Core.Conversion;

public sealed record LayoutStride(string Guid, int Kind, int Size);
public sealed record LayoutRoute(string OwnerGuid, int OwnerKind, int Offset, string ChildGuid, int ChildKind);
public sealed record LayoutProfile(string Group, string RootGuid, string SourceSchema, string NativeSchema, LayoutStride[] Strides, LayoutRoute[] Routes);
public sealed record ConvertedTag(byte[] Bytes, string Method);

/// <summary>Changes an envelope only when its entire structure follows verified native strides and field routes.</summary>
public sealed class StructuralConverter
{
    private readonly NativeSchemaSnapshot snapshot;
    private readonly Dictionary<string, NativeSchema> native;
    private readonly Dictionary<string, LayoutProfile> profiles;
    private static readonly Lazy<LayoutProfile[]> BuiltIn = new(() =>
    {
        using var stream = typeof(StructuralConverter).Assembly.GetManifestResourceStream("H5SoloLauncher.Core.Conversion.layout-profiles.json") ?? throw Invalid("Layout profiles are missing.");
        return JsonSerializer.Deserialize<LayoutProfile[]>(stream) ?? throw Invalid("Layout profiles are damaged.");
    });

    public StructuralConverter(NativeSchemaSnapshot snapshot, LayoutProfile[]? profiles = null)
    {
        if (snapshot.Format != 1 || snapshot.Profile != ForgeSchemaRegistry.Profile || snapshot.PackageFullName != ForgeSchemaRegistry.Package) throw Invalid("The native layout snapshot is unsupported.");
        this.snapshot = snapshot; native = snapshot.Structures.ToDictionary(x => x.Guid, StringComparer.Ordinal);
        this.profiles = (profiles ?? BuiltIn.Value).ToDictionary(x => x.Group, StringComparer.Ordinal);
    }

    public ConvertedTag Convert(string group, TagDocument tag)
    {
        var roots = tag.Metadata.RootGuids;
        if (roots.Length == 0) throw Invalid("The tag has no root structure.");
        if (roots.All(guid => native.TryGetValue(guid, out var value) && value.Schema == tag.Metadata.Schema)) return new(tag.Bytes, "NativeLayout");
        if (roots.All(guid => ForgeLegacySchemas.Accepts(snapshot, guid, tag.Metadata.Schema))) return new(tag.Bytes, "NativeLegacyLayout");
        if (group == "bitm" && roots.Length == 1 && tag.Metadata.Schema == BitmapConverter.SourceTagSchema &&
            native.TryGetValue(roots[0], out var bitmap) && bitmap.Schema == BitmapConverter.NativeTagSchema)
            // Bitmap resources must be converted separately before this tag can be published.
            return new(tag.WithSchema(BitmapConverter.NativeTagSchema), "BitmapEnvelope");
        if (!profiles.TryGetValue(group, out var profile) || roots.Length != 1 || roots[0] != profile.RootGuid || tag.Metadata.Schema != profile.SourceSchema ||
            !native.TryGetValue(profile.RootGuid, out var root) || root.Schema != profile.NativeSchema)
            throw Invalid($"No verified conversion is available for {group} schema {tag.Metadata.Schema}.");
        Validate(tag, profile);
        return new(tag.WithSchema(profile.NativeSchema), "VerifiedEnvelope");
    }

    public void Validate(TagDocument tag, LayoutProfile profile)
    {
        var strides = profile.Strides.ToDictionary(x => (x.Guid, x.Kind), x => x.Size);
        var routes = profile.Routes.ToHashSet();
        Dictionary<int, (string Guid, int Kind, int Stride)> owners = [];
        foreach (var structure in tag.Structures)
        {
            if (structure.Target < 0) continue;
            uint count;
            if (structure.Kind is 0 or 65536)
            {
                if (structure.FieldBlock != -1 || structure.FieldOffset != 0) throw Invalid("A root has an unexpected field location.");
                count = 1;
            }
            else
            {
                if (structure.Kind is not (1 or 65537)) throw Invalid("A stored structure has an unsupported kind.");
                tag.Field(structure.FieldBlock, structure.FieldOffset, 28);
                count = BinaryPrimitives.ReadUInt32LittleEndian(tag.Block(structure.FieldBlock)[(structure.FieldOffset + 16)..]);
            }
            var size = tag.Blocks[structure.Target].Size;
            if (count == 0 || size <= 0 || size % count != 0) throw Invalid("A structure array has an invalid element count.");
            var stride = checked((int)(size / count));
            if (!strides.TryGetValue((structure.Guid, structure.Kind), out var expected) || expected != stride)
                throw Invalid($"Unknown {structure.Guid} structure stride {stride}.");
            if (!native.TryGetValue(structure.Guid, out var definition) || definition.Size != stride)
                throw Invalid($"Forge's {structure.Guid} structure size differs from the verified layout.");
            var owner = (structure.Guid, structure.Kind, stride);
            if (owners.TryGetValue(structure.Target, out var existing) && existing != owner) throw Invalid("A block has conflicting structure owners.");
            owners[structure.Target] = owner;
        }
        foreach (var structure in tag.Structures)
        {
            if (structure.Kind is 0 or 65536) continue;
            if (!owners.TryGetValue(structure.FieldBlock, out var owner) || structure.FieldOffset < 0 ||
                !routes.Contains(new(owner.Guid, owner.Kind, structure.FieldOffset % owner.Stride, structure.Guid, structure.Kind)))
                throw Invalid("A structural field has no verified native route.");
            var width = structure.Kind == 3 ? 32 : 28;
            tag.Field(structure.FieldBlock, structure.FieldOffset, width);
            if (width > owner.Stride - structure.FieldOffset % owner.Stride) throw Invalid("A structural field crosses an array element boundary.");
            if (structure.Target < 0 && structure.Kind != 3 && (structure.Kind is not (1 or 65537) ||
                BinaryPrimitives.ReadUInt32LittleEndian(tag.Block(structure.FieldBlock)[(structure.FieldOffset + 16)..]) != 0))
                throw Invalid("An empty array has a nonzero count or an unsupported kind.");
        }
    }

    private static CacheException Invalid(string message) => new("TAG_CONVERSION_UNSUPPORTED", message);
}
