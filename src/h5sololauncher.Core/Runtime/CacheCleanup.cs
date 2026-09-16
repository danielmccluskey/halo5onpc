using System.Text.Json;
using H5SoloLauncher.Core.Preparation;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Core.Runtime;

public sealed record CacheCleanupResult(string State, long Bytes, int Files, string Message);
public sealed record CacheRetention(bool KeepRebuildData = true);

public static class CacheCleanup
{
    public const string Pending = "cleanup-pending.json";

    public static void Schedule(string root, bool keepRebuildData)
    {
        root=CacheFolders.OpenExisting(root).Root;
        using var lease=new FileStream(SafePaths.Child(root,"index.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        InputFiles.Write(root, Pending, JsonSerializer.SerializeToUtf8Bytes(new CacheRetention(keepRebuildData)));
    }

    // Caller supplies a fail-closed process check. No recursive deletion: every
    // candidate is resolved beneath an owned cache and checked again on removal.
    public static CacheCleanupResult Run(string selected, string forge, string package,
        Func<bool> gameRunning, bool preview, CancellationToken cancellation = default)
    {
        var root = CacheFolders.OpenExisting(selected, forge).Root;
        FileStream preparationLease;
        try { preparationLease=new FileStream(SafePaths.Child(root,"preparation.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None); }
        catch(IOException) { return new("Deferred",0,0,"Cleanup deferred while preparation is active."); }
        using var preparationGuard=preparationLease;
        using var lease = new FileStream(SafePaths.Child(root, "index.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (gameRunning()) return new("Deferred", 0, 0, "Cleanup deferred until Forge closes. It will retry before Play.");
        var pointer = InputFiles.Read<Dictionary<string,string>>(root, "playable.json", 4096);
        var cache = PlayableCache.Read(root, forge, package, pointer["Id"]);
        var policy = File.Exists(SafePaths.Child(root, Pending))
            ? InputFiles.Read<CacheRetention>(root, Pending, 4096) : new();
        var keep = cache.Manifest.Files.Append(cache.Manifest.Movie).Select(x => x.CachePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var map in cache.Manifest.Maps) keep.Add("game/metadata/" + map.MetadataSha256.ToLowerInvariant() + ".mapinfo");
        keep.Add(InputFiles.PathFor("playable-manifests", cache.Id));
        var p = cache.Manifest.Prepared;
        foreach (var (category,id) in new[] { ("menu-config",p.MenuConfigId), ("ui-residency",p.UiConfigId),
            ("completion-config",p.CompletionConfigId), ("display-config",p.DisplayConfigId), ("movie-config",p.MovieConfigId) })
            keep.Add(InputFiles.PathFor(category,id,".bin"));
        var candidates = new List<(string Relative,long Bytes)>();
        void Visit(string relative)
        {
            var directory = SafePaths.Child(root, relative);
            if (!Directory.Exists(directory)) return;
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var child = Path.GetRelativePath(root,path).Replace('\\','/');
                var safe = SafePaths.Child(root,child); // Reject links before traversal.
                if (Directory.Exists(safe)) Visit(child);
                else if (!keep.Contains(child)) candidates.Add((child,new FileInfo(safe).Length));
            }
        }
        foreach (var category in new[] { "modules", "audio", "movies", "menu-artwork", "metadata", "variants" }) Visit("game/"+category);
        if (!policy.KeepRebuildData) Visit("inputs");
        if (preview) return new("Preview", candidates.Sum(x=>x.Bytes), candidates.Count, "No files deleted.");
        // Validate the complete replacement before touching any old generation.
        foreach (var file in cache.Manifest.Files.Append(cache.Manifest.Movie))
        {
            cancellation.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(SafePaths.Child(root,file.CachePath));
            if (InputFiles.Digest(stream,cancellation) != file.Sha256)
                throw new CacheException("CLEANUP_CURRENT_DAMAGED", "Cleanup stopped: a current playable file failed verification.");
        }
        long bytes = 0; int count = 0, skipped = 0;
        foreach (var file in candidates)
        {
            cancellation.ThrowIfCancellationRequested();
            if (gameRunning()) return new("Deferred",bytes,count,"Forge started; remaining cleanup deferred.");
            try
            {
                File.Delete(SafePaths.Child(root,file.Relative)); bytes += file.Bytes; count++;
            }
            catch (IOException) { skipped++; }
            catch (UnauthorizedAccessException) { skipped++; }
        }
        if (skipped == 0 && File.Exists(SafePaths.Child(root,Pending))) File.Delete(SafePaths.Child(root,Pending));
        return new(skipped==0 ? "Cleaned" : "Deferred",bytes,count,
            $"Removed {bytes / 1_000_000_000.0:N2} GB of obsolete cache files." + (skipped>0 ? $" {skipped} busy files will be retried before Play." : ""));
    }
}
