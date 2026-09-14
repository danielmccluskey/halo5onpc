using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Core.Content;

public sealed class SourceIndexer(Func<string, long>? availableBytes = null)
{
    internal sealed record SourceFile(string Relative, string Kind, long Length, long Ticks);

    public IndexResult Run(IndexRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation)
    {
        CacheLocation? cache = null; SourceDescriptor? source = null; IndexCatalog? catalog = null; FileStream? lease = null;
        string? currentFile = null;
        try
        {
            source = SourceDiscovery.Describe(request.SourceRoot);
            cache = CacheFolders.Open(request.CacheRoot, source.Root, request.ForgeRoot);
            try { lease = new FileStream(SafePaths.Child(cache.Root, "index.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { throw new CacheException("CACHE_IN_USE", "Another indexer is using this cache. Wait for it to finish or pause, then try again."); }
            Space(cache.Root, 256L * 1024 * 1024);
            catalog = new IndexCatalog(cache, source);
            catalog.SaveSummary(catalog.Summary(source, "Indexing"));
            CacheLog.Write(cache.Root, "Started metadata indexing.");
            progress.Report(new("Finding source files", 0, 0));
            var files = Inventory(source.Root, cancellation);
            if (!files.Any(x => x.Kind == "Module" && x.Relative.StartsWith("deploy/any/", StringComparison.OrdinalIgnoreCase)) ||
                !files.Any(x => x.Kind == "Module" && x.Relative.StartsWith("deploy/x1/", StringComparison.OrdinalIgnoreCase)) ||
                !files.Any(x => x.Kind == "Audio") || !files.Any(x => x.Kind == "Movie"))
                throw new CacheException("DUMP_INCOMPLETE", "The export needs modules for both platforms, audio packages and movies.");
            var epoch = Guid.NewGuid().ToString("N"); var completed = 0; var reused = 0;
            foreach (var file in files)
            {
                cancellation.ThrowIfCancellationRequested();
                currentFile = file.Relative;
                progress.Report(new("Reading metadata", completed, files.Count, currentFile, reused));
                var path = SafePaths.Child(source.Root, file.Relative);
                var metadata = FileMetadata.Read(path, file.Kind, cancellation);
                if (metadata.FileLength != file.Length || metadata.Ticks != file.Ticks) throw new CacheException("DUMP_CHANGED", "The export changed during indexing. Let extraction finish, then resume.");
                Space(cache.Root, Math.Max(128L * 1024 * 1024, metadata.TableBytes * 8L + 64L * 1024 * 1024));
                if (catalog.Store(file.Relative, metadata, epoch, cancellation)) reused++;
                completed++;
                progress.Report(new("Reading metadata", completed, files.Count, currentFile, reused));
            }
            progress.Report(new("Checking source snapshot", completed, files.Count, null, reused));
            var after = Inventory(source.Root, cancellation);
            if (!files.SequenceEqual(after) || SourceDiscovery.Describe(source.Root) != source)
                throw new CacheException("DUMP_CHANGED", "Files or the package identity changed during indexing. Resume when the export is stable.");
            cancellation.ThrowIfCancellationRequested();
            catalog.RemoveAbsent(epoch);
            var summary = catalog.Summary(source, "Indexed"); catalog.SaveSummary(summary);
            CacheLog.Write(cache.Root, $"Indexed {summary.Modules} modules, {summary.Entries} item records and {summary.AudioEntries} audio records. {summary.UnresolvedPayloads} payload references unresolved. Conversion not built.");
            return new("Indexed", summary);
        }
        catch (OperationCanceledException)
        {
            var summary = Finish("Paused");
            return new("Paused", summary, Message: "Indexing paused. Completed file checkpoints were kept. Press Index / resume to continue.");
        }
        catch (Exception exception)
        {
            var summary = Finish("Failed");
            var code = exception is CacheException known ? known.Code : exception is UnauthorizedAccessException ? "ACCESS_DENIED" : "INDEX_FAILED";
            var message = exception is CacheException ? exception.Message : "The index could not be completed. Check that the dump and cache are accessible, then resume or copy the details.";
            var details = $"File: {currentFile ?? "(setup)"}\n{exception}";
            if (cache is not null && lease is not null) CacheLog.TryWrite(cache.Root, $"{code}: {details}");
            return new("Failed", summary, code, message, details);
        }
        finally { catalog?.Dispose(); lease?.Dispose(); }

        IndexSummary? Finish(string state)
        {
            if (catalog is null || source is null) return null;
            try { var value = catalog.Summary(source, state); catalog.SaveSummary(value); return value; }
            catch { return null; } // Preserve the original error if disk/database failure also prevents a checkpoint.
        }
    }

    private void Space(string root, long required)
    {
        var free = availableBytes?.Invoke(root) ?? new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace;
        if (free < required) throw new CacheException("CACHE_NEEDS_SPACE", $"The cache needs at least {required / (1024 * 1024)} MiB free for its next metadata checkpoint. Free space, then resume.");
    }

    internal static List<SourceFile> Inventory(string root, CancellationToken cancellation)
    {
        List<SourceFile> files = [];
        foreach (var name in new[] { "AppxManifest.xml", "version.txt" })
        {
            var path = SafePaths.Child(root, name);
            if (File.Exists(path)) Add(new FileInfo(path), "Metadata");
        }
        foreach (var folder in new[] { "deploy", "__cms__", "sound", "bink" })
        {
            var stack = new Stack<DirectoryInfo>(); stack.Push(new(SafePaths.Child(root, folder)));
            while (stack.TryPop(out var directory))
            {
                cancellation.ThrowIfCancellationRequested();
                foreach (var entry in directory.EnumerateFileSystemInfos())
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new CacheException("DUMP_LINKED", $"The export contains a linked entry: {entry.FullName}");
                    if (entry is DirectoryInfo child) stack.Push(child);
                    else if (entry is FileInfo file)
                    {
                        var kind = file.Extension.ToLowerInvariant() switch { ".module" => "Module", ".pck" => "Audio", ".bk2" => "Movie", _ => folder == "__cms__" ? "Metadata" : null };
                        if (kind is not null) Add(file, kind);
                    }
                }
            }
        }
        files.Sort((a, b) => StringComparer.Ordinal.Compare(a.Relative, b.Relative));
        return files;
        void Add(FileInfo file, string kind) => files.Add(new(Path.GetRelativePath(root, file.FullName).Replace('\\', '/'), kind, file.Length, file.LastWriteTimeUtc.Ticks));
    }

}
