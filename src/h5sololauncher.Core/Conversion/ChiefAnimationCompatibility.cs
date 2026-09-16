using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Forge;
using H5SoloLauncher.Core.Preparation;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Core.Conversion;

/// <summary>
/// Campaign patch 79425 embeds an older Chief graph whose inherited clip indices
/// address a 2,694-animation parent. Forge supplies the 2,826-animation parent.
/// Use Forge's matching child graph; its sole local resource is identical.
/// </summary>
public sealed class ChiefAnimationCompatibility
{
    public const string Rules = "chief-animation-native-graph-1";
    public const string SourceSha256 = "3A0BC3CC1737C3C2E4A6DDC7571A848A41EACA15B374E33CAAB00275237EDFA2";
    public const string NativeSha256 = "8F8EDFCD79F9BFA5EC103BC01369C2487F193CA368E021B87F2538A08DD15843";
    public const string ParentSha256 = "4EE64C18F9400BC7BF6DD1DF51051936500380C9C8525D72A9A0E8576BE9A9B1";
    public const string ResourceSha256 = "74A61FC098029764C7C979D313B849E465285656924F093B05C468122D645F47";
    private readonly byte[] graph;

    public ChiefAnimationCompatibility(byte[] nativeGraph, byte[] nativeParent, byte[] nativeResource)
    {
        if (InputFiles.Hash(nativeGraph) != NativeSha256 || InputFiles.Hash(nativeParent) != ParentSha256 ||
            InputFiles.Hash(nativeResource) != ResourceSha256) throw Invalid("The native animation graph or resource does not match the verified Forge revision.");
        graph = (byte[])nativeGraph.Clone();
    }

    public static bool AppliesTo(EffectiveTag tag) => tag.Identity.Group == "jmad" && tag.Identity.TagId == "ac4f7bcc" && tag.Sha256 == SourceSha256;

    public byte[] Replace(byte[] sourceGraph, byte[] sourceResource)
    {
        if (InputFiles.Hash(sourceGraph) != SourceSha256 || InputFiles.Hash(sourceResource) != ResourceSha256)
            throw Invalid("Chief's campaign animation graph or local resource changed after compatibility selection.");
        return (byte[])graph.Clone();
    }

    public static ChiefAnimationCompatibility? Load(string cacheRoot, EffectivePlan plan, CancellationToken cancellation)
    {
        if (!plan.Tags.Any(AppliesTo)) return null;
        // This repair is applicable only when the shared parent is supplied by Forge.
        var parents = plan.NativeDependencies.Where(x => x.Requested.Group == "jmad" && x.Requested.TagId == "000033ce")
            .Select(x => (x.File, x.Item)).Distinct().ToArray();
        if (parents.Length == 0) return null;
        if (parents.Length != 1) throw Invalid("The shared animation parent has ambiguous native routes.");
        var manifest = InputFiles.Read<NativeModuleManifest>(cacheRoot, InputFiles.PathFor("native-manifests", plan.NativeManifestId), 1024 * 1024, plan.NativeManifestId);
        if (manifest.Rules != NativeModuleCache.Rules) throw InputFiles.Damaged();
        var module = manifest.Modules.Single(x => x.Path == parents[0].File);
        var path = SafePaths.Child(cacheRoot, module.RelativePath);
        var metadata = FileMetadata.Read(path, "Module", cancellation);
        if (metadata.FileLength != module.Length || metadata.Digest != module.TableDigest) throw InputFiles.Damaged();
        var chief = Enumerable.Range(0, metadata.ItemCount).Select(metadata.Entry)
            .Single(x => x.Group == "jmad" && x.TagId == "ac4f7bcc" && x.StoredSize > 0);
        var parent = metadata.Entry(parents[0].Item);
        if (parent.Group != "jmad" || parent.TagId != "000033ce" || chief.ResourceCount != 1)
            throw Invalid("The native animation resource layout is unsupported.");
        var resource = metadata.Entry(metadata.Resource(chief.ResourceIndex));
        if (resource.Parent != chief.Index || resource.ResourceCount != 0)
            throw Invalid("Chief's native local animation resource layout is unsupported.");
        using var stream = File.OpenRead(path);
        return new(ModulePayloadReader.Read(stream, metadata, chief, cancellation).Bytes,
            ModulePayloadReader.Read(stream, metadata, parent, cancellation).Bytes,
            ModulePayloadReader.ReadResource(stream, metadata, resource, cancellation).Bytes);
    }

    private static CacheException Invalid(string message) => new("CHIEF_ANIMATION_COMPATIBILITY", message);
}
