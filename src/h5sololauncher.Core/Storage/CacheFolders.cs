using System.Text.Json;

namespace H5SoloLauncher.Core.Storage;

public static class CacheFolders
{
    public const string FolderName = "h5sololauncher-cache";
    private sealed record Marker(string Application, int Version, string Id);

    public static CacheLocation Select(string selected, string source, string? forge)
    {
        selected = SafePaths.Canonical(selected);
        SafePaths.NoLinks(selected);
        if (!Directory.Exists(selected)) throw new CacheException("CACHE_OFFLINE", "The selected destination folder is unavailable.");
        var root = File.Exists(Path.Combine(selected, "cache.json")) ? selected : Path.Combine(selected, FolderName);
        SafePaths.Separate(root, SafePaths.Canonical(source), forge);
        SafePaths.NoLinks(root);
        RequireVolume(root);
        if (!File.Exists(Path.Combine(root, "cache.json")))
        {
            if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
                throw new CacheException("CACHE_NOT_OWNED", "This folder contains other files. Choose an empty destination or an existing h5sololauncher cache.");
            Directory.CreateDirectory(root);
            // CreateNew prevents two initializers from replacing an existing marker.
            using var file = new FileStream(Path.Combine(root, "cache.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(file, new Marker("h5sololauncher", 1, Guid.NewGuid().ToString("N")));
            file.Flush(true);
        }
        var result = Open(root, source, forge);
        var probe = SafePaths.Child(root, ".probe-" + Guid.NewGuid().ToString("N"));
        var renamed = probe + ".renamed";
        try
        {
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            { stream.WriteByte(0x48); stream.Flush(true); stream.Position = 0; if (stream.ReadByte() != 0x48) throw new IOException("Cache read-back failed."); }
            File.Move(probe, renamed);
        }
        finally
        {
            // Only these two freshly generated files are eligible for cleanup.
            SafePaths.NoLinks(root);
            if (File.Exists(probe)) File.Delete(probe);
            if (File.Exists(renamed)) File.Delete(renamed);
        }
        return result;
    }

    public static CacheLocation Open(string root, string source, string? forge = null)
        => OpenCore(root, SafePaths.Canonical(source), forge);

    public static CacheLocation OpenExisting(string selected, string? forge = null)
    {
        selected = SafePaths.Canonical(selected);
        var root = File.Exists(Path.Combine(selected, "cache.json")) ? selected : Path.Combine(selected, FolderName);
        if (!File.Exists(Path.Combine(root, "cache.json")))
            throw new CacheException("CACHE_NOT_FOUND", "No launcher cache was found here. Choose an existing h5sololauncher-cache folder, or select a campaign dump first to build a new cache.");
        return OpenCore(root, string.Empty, forge);
    }

    private static CacheLocation OpenCore(string root, string source, string? forge)
    {
        root = SafePaths.Canonical(root);
        SafePaths.NoLinks(root);
        SafePaths.Separate(root, source, forge);
        var drive = RequireVolume(root);
        using var file = File.OpenRead(SafePaths.Child(root, "cache.json"));
        if (file.Length > 4096) throw new CacheException("CACHE_INVALID", "The cache ownership file is invalid.");
        var marker = JsonSerializer.Deserialize<Marker>(file);
        if (marker is null || marker.Application != "h5sololauncher" || marker.Version != 1 || !Guid.TryParseExact(marker.Id, "N", out _))
            throw new CacheException("CACHE_FORMAT_UNSUPPORTED", "This cache format is not supported by this launcher. Choose another folder or update the launcher.");
        return new(root, marker.Id, drive.AvailableFreeSpace);
    }

    private static DriveInfo RequireVolume(string root)
    {
        var drive = new DriveInfo(Path.GetPathRoot(root)!);
        if (!drive.IsReady) throw new CacheException("CACHE_OFFLINE", "The cache drive is unavailable. Reconnect it and try again.");
        if (drive.DriveType == DriveType.Network || !drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            throw new CacheException("CACHE_FILESYSTEM_UNSUPPORTED", "This version needs a local NTFS cache folder. You can choose any suitable drive.");
        return drive;
    }
}
