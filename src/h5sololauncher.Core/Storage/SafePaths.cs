namespace H5SoloLauncher.Core.Storage;

public static class SafePaths
{
    public static string Canonical(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new CacheException("PATH_INVALID", "Choose an absolute folder path.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public static bool Within(string parent, string child) =>
        child.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
        child.StartsWith(parent.EndsWith(Path.DirectorySeparatorChar) ? parent : parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static void NoLinks(string path)
    {
        for (var cursor = Path.GetFullPath(path); cursor is not null; cursor = Path.GetDirectoryName(cursor))
        {
            try
            {
                if (File.GetAttributes(cursor).HasFlag(FileAttributes.ReparsePoint))
                    throw new CacheException("PATH_LINKED", $"Choose a real folder rather than a linked path: {cursor}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    // Windows may register an app package through a link when it is installed on
    // another drive. Permit that package-root link only; reject linked descendants.
    public static void PackageRoot(string root)
    {
        root = Canonical(root);
        var parent = Path.GetDirectoryName(root);
        if (parent is not null) NoLinks(parent);
        if (!File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint)) return;
        var info = new DirectoryInfo(root);
        var target = info.ResolveLinkTarget(true)?.FullName;
        if (target is null) throw new CacheException("PATH_LINKED", $"The package junction could not be resolved: {root}");
        NoLinks(target);
    }

    public static string PackageChild(string root, string relative)
    {
        root = Canonical(root);
        PackageRoot(root);
        if (Path.IsPathRooted(relative)) throw new CacheException("PATH_INVALID", "An indexed path was not relative.");
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase) || !Within(root, path))
            throw new CacheException("PATH_INVALID", "An indexed path escaped its source folder.");
        for (var cursor = path; !cursor.Equals(root, StringComparison.OrdinalIgnoreCase); cursor = Path.GetDirectoryName(cursor)!)
        {
            try
            {
                if (File.GetAttributes(cursor).HasFlag(FileAttributes.ReparsePoint))
                    throw new CacheException("PATH_LINKED", $"Choose a real folder rather than a linked path: {cursor}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return path;
    }

    public static bool PackageChildMatches(string root, string relative, string actual)
    {
        var expected = PackageChild(root, relative);
        actual = Canonical(actual);
        if (actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) return true;
        var physicalRoot = new DirectoryInfo(Canonical(root)).ResolveLinkTarget(true)?.FullName;
        return physicalRoot is not null && actual.Equals(
            Path.GetFullPath(Path.Combine(physicalRoot, relative.Replace('/', Path.DirectorySeparatorChar))),
            StringComparison.OrdinalIgnoreCase);
    }

    public static string Child(string root, string relative)
    {
        root=Canonical(root);
        if (Path.IsPathRooted(relative)) throw new CacheException("PATH_INVALID", "An indexed path was not relative.");
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase) || !Within(root, path))
            throw new CacheException("PATH_INVALID", "An indexed path escaped its source folder.");
        NoLinks(path);
        return path;
    }

    public static void Separate(string cache, string source, string? forge)
    {
        if (!string.IsNullOrEmpty(source) && (Within(cache, source) || Within(source, cache)))
            throw new CacheException("CACHE_OVERLAPS_SOURCE", "Choose a cache folder outside the campaign dump.");
        if (!string.IsNullOrWhiteSpace(forge) && (Within(Canonical(forge), cache) || Within(cache, Canonical(forge))))
            throw new CacheException("CACHE_OVERLAPS_FORGE", "Choose a cache folder outside the Forge installation.");
        for (var cursor = cache; cursor is not null; cursor = Path.GetDirectoryName(cursor))
            if (File.Exists(Path.Combine(cursor, "AppxManifest.xml")))
                throw new CacheException("CACHE_INSIDE_PACKAGE", "Choose a cache folder outside an installed or extracted game package.");
    }
}
