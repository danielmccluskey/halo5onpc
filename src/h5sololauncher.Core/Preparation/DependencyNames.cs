using H5SoloLauncher.Core.Forge;
using H5SoloLauncher.Core.Planning;

namespace H5SoloLauncher.Core.Preparation;

public static class DependencyNames
{
    // Diagnostic evidence only. A generated name match never authorizes an asset-ID rewrite.
    public static DependencyNameEvidence Compare(ForgeMatch missing, IEnumerable<NamedDependency> references)
    {
        var names = references.Where(x => x.Identity == missing.Identity && !string.IsNullOrEmpty(x.Name)).Select(x => x.Name!)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var canonical = names.Select(Canonical).Distinct(StringComparer.Ordinal).ToArray();
        var matches = canonical.Length == 1 ? (missing.DifferentAssetIds ?? []).Where(x =>
            IsGenerated(x.Name) && Canonical(x.Name) == canonical[0]).OrderBy(x => x.File, StringComparer.Ordinal).ThenBy(x => x.Item).ToArray() : [];
        var state = missing.Candidates.Length > 0 ? "ExactNativeCandidates" : canonical.Length == 0 ? "NameUnavailable" :
            canonical.Length > 1 ? "ConflictingNames" : matches.Length > 0 ? "PotentialPlatformMatch" : "NoNameMatch";
        return new(missing.Identity, names, state, matches);
    }
    public static bool IsGenerated(string name) => new[] { "__chore/pc__/", "__chore/x1__/", "__chore/gen__/" }
        .Any(prefix => name.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    public static string Canonical(string name)
    {
        name = name.Replace('\\', '/').ToLowerInvariant();
        foreach (var prefix in new[] { "__chore/pc__/", "__chore/x1__/", "__chore/gen__/" })
            if (name.StartsWith(prefix, StringComparison.Ordinal)) { name = name[prefix.Length..]; break; }
        foreach (var marker in new[] { "{pc}", "{x1}", "{pm}", "{b2p}", "{ctrim}" }) name = name.Replace(marker, "", StringComparison.Ordinal);
        var dot = name.LastIndexOf('.'); return dot > name.LastIndexOf('/') ? name[..dot] : name;
    }
}
