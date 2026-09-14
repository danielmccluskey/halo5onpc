using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Core.Content;

public static class SourceDiscovery
{
    // Search at most the selected folder, Mount, or one package level below it.
    public static string ResolveRoot(string selected)
    {
        var root = SafePaths.Canonical(selected);
        SafePaths.NoLinks(root);
        if (Path.GetFileName(root).Equals("deploy", StringComparison.OrdinalIgnoreCase))
            root = Path.GetDirectoryName(root)!;
        if (File.Exists(Path.Combine(root, "AppxManifest.xml"))) return root;
        var mount = Path.Combine(root, "Mount");
        if (File.Exists(Path.Combine(mount, "AppxManifest.xml"))) { SafePaths.NoLinks(mount); return mount; }
        List<string> candidates = [];
        foreach (var child in new DirectoryInfo(root).EnumerateDirectories())
        {
            if (child.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            mount = Path.Combine(child.FullName, "Mount");
            if (File.Exists(Path.Combine(mount, "AppxManifest.xml"))) candidates.Add(mount);
            if (candidates.Count > 1) break;
        }
        if (candidates.Count > 1) throw new CacheException("DUMP_AMBIGUOUS", "Several exported packages were found. Choose the required package's Mount folder.");
        if (candidates.Count == 1) { SafePaths.NoLinks(candidates[0]); return candidates[0]; }
        return root; // Describe provides the actionable missing-manifest error.
    }

    public static SourceDescriptor Describe(string selected)
    {
        var root = ResolveRoot(selected);
        var path = SafePaths.Child(root, "AppxManifest.xml");
        using var stream = File.OpenRead(path);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
        var document = XDocument.Load(reader);
        var identity = document.Root?.Elements().SingleOrDefault(x => x.Name.LocalName == "Identity");
        var version = (string?)identity?.Attribute("Version");
        if (document.Root?.Name.LocalName != "Package" || (string?)identity?.Attribute("Name") != "Halo5-Guardians" || !Version.TryParse(version, out _))
            throw new CacheException("DUMP_INVALID", "Choose an extracted Halo 5: Guardians package with a readable manifest.");
        foreach (var required in new[] { "deploy/any/levels", "deploy/x1/levels", "__cms__", "sound", "bink" })
            if (!Directory.Exists(SafePaths.Child(root, required)))
                throw new CacheException("DUMP_INCOMPLETE", $"The export is missing {required}.");
        // A cache belongs to one source package version. Relocation is allowed;
        // actual table digests, not this descriptor, decide whether records can be reused.
        var key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("Halo5-Guardians\n" + version)));
        return new(root, version!, key);
    }
}
