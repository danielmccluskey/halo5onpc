using System.Text.RegularExpressions;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Core.Forge;

public static partial class ForgePaths
{
    public const string Family = "Microsoft.Halo5Forge_8wekyb3d8bbwe";
    [GeneratedRegex(@"^deploy/(any|pc)/levels/globals(?:-[A-Za-z0-9]+)*\.module$", RegexOptions.CultureInvariant)]
    private static partial Regex GlobalPattern();
    [GeneratedRegex(@"^Microsoft\.Halo5Forge_\d+\.\d+\.\d+\.\d+_x64__8wekyb3d8bbwe$", RegexOptions.CultureInvariant)]
    private static partial Regex PackagePattern();
    public static bool IsGlobal(string relative) => GlobalPattern().IsMatch(relative);
    public static bool IsAudio(string relative)
    {
        if (relative is "sound/win/SFX/soundbank.pck" or "sound/win/SFX/soundstream.pck") return true;
        const string prefix = "sound/win/", suffix = "/soundvoice.pck";
        if (!relative.StartsWith(prefix, StringComparison.Ordinal) || !relative.EndsWith(suffix, StringComparison.Ordinal)) return false;
        var language = relative[prefix.Length..^suffix.Length];
        return language.Length is > 0 and <= 40 && language.All(c => char.IsAsciiLetter(c) || c is '(' or ')' or '-');
    }
    public static void ValidatePackage(string package)
    { if (!PackagePattern().IsMatch(package)) throw new CacheException("FORGE_IDENTITY_INVALID", "Check the installed Forge package again."); }
    public static string[] Inventory(string root)
    {
        root = SafePaths.Canonical(root); SafePaths.NoLinks(root);
        var paths = new List<string>();
        foreach (var platform in new[] { "any", "pc" })
        {
            var folder = SafePaths.Child(root, $"deploy/{platform}/levels");
            if (!Directory.Exists(folder)) throw new CacheException("FORGE_GLOBALS_MISSING", "Forge’s global module folder is missing. Check the installation again.");
            foreach (var file in Directory.EnumerateFiles(folder, "globals*.module", SearchOption.TopDirectoryOnly))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (!IsGlobal(relative)) continue;
                SafePaths.Child(root, relative); paths.Add(relative);
            }
        }
        if (paths.Count == 0) throw new CacheException("FORGE_GLOBALS_MISSING", "No supported Forge global modules were found.");
        return paths.Order(StringComparer.Ordinal).ToArray();
    }
}
