using System.Security.Cryptography;
using System.Text.Json;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Planning;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Core.Forge;

public sealed class ForgePreparation
{
    public const string Rules = "forge-global-candidates-2";
    public ForgeResult Run(ForgeRequest request, Func<IForgeFileReader> readerFactory, IProgress<IndexProgress> progress, CancellationToken cancellation)
    {
        string? current = null; CacheLocation? cache = null; bool locked = false;
        try
        {
            cancellation.ThrowIfCancellationRequested(); ForgePaths.ValidatePackage(request.PackageFullName);
            var source = SourceDiscovery.Describe(request.SourceRoot);
            cache = CacheFolders.Open(request.CacheRoot, source.Root, request.ForgeRoot);
            using var lease = Lock(cache.Root); locked = true;
            using var snapshot = new CatalogSnapshot(cache, source);
            progress.Report(new("Checking saved campaign plan", 0, 0));
            var plan = BuildPlanStore.ReadVerified(cache, snapshot, request.PlanId);
            var missing = plan.Issues.Where(x => x.Code == "DEPENDENCY_MISSING" && x.Identity is not null)
                .Select(x => x.Identity!).Distinct().OrderBy(x => x.Group, StringComparer.Ordinal).ThenBy(x => x.TagId, StringComparer.Ordinal).ThenBy(x => x.AssetId, StringComparer.Ordinal).ToArray();
            var candidates = missing.ToDictionary(x => x, _ => new List<ForgeCandidate>());
            var alternatives = missing.ToDictionary(x => x, _ => new List<ForgeIdentityAlternative>());
            var byTag = missing.GroupBy(x => (x.Group, x.TagId)).ToDictionary(x => x.Key, x => x.ToArray());
            var files = ForgePaths.Inventory(request.ForgeRoot);
            CacheLog.Write(cache.Root, $"Preparing Forge data: {request.PackageFullName}, {files.Length} global modules, plan {request.PlanId}.");
            if (request.AllowCacheRead) progress.Report(new("Allowing Forge to read this cache", 0, 0));
            using var reader = readerFactory(); using var catalog = new ForgeCatalog(cache);
            List<ForgeModule> modules = []; var reused = 0;
            foreach (var relative in files)
            {
                cancellation.ThrowIfCancellationRequested(); current = relative;
                progress.Report(new("Reading Forge global tables", modules.Count, files.Length, relative, reused));
                var first = reader.Read(relative, 0, 48, cancellation);
                var size = FileMetadata.ModuleTableLength(first.Bytes, first.FileLength);
                var tables = new byte[size]; first.Bytes.CopyTo(tables, 0);
                for (var offset = 48; offset < size;)
                {
                    cancellation.ThrowIfCancellationRequested(); var count = Math.Min(1024 * 1024, size - offset);
                    var chunk = reader.Read(relative, offset, count, cancellation);
                    if (chunk.FileLength != first.FileLength || chunk.LastWriteFileTime != first.LastWriteFileTime || chunk.Bytes.Length != count) throw Changed();
                    chunk.Bytes.CopyTo(tables, offset); offset += count;
                }
                var final = reader.Read(relative, 0, 48, cancellation);
                if (final.FileLength != first.FileLength || final.LastWriteFileTime != first.LastWriteFileTime || !final.Bytes.AsSpan().SequenceEqual(first.Bytes)) throw Changed();
                var metadata = FileMetadata.FromModuleTables(tables, first.FileLength, first.LastWriteFileTime);
                if (metadata.Revision != 27) throw new CacheException("FORGE_REVISION_UNSUPPORTED", "Expected Forge module revision 27. This installation needs a different reader.");
                var tagCount = 0;
                for (var i = 0; i < metadata.ItemCount; i++)
                {
                    cancellation.ThrowIfCancellationRequested(); var tag = metadata.Entry(i);
                    if (tag.TagId == "ffffffff" || tag.StoredSize == 0) continue;
                    tagCount++;
                    if (candidates.TryGetValue(new(tag.Group, tag.TagId, tag.AssetId), out var list)) list.Add(new(relative, i, tag.Name, tag.Checksum));
                    if (byTag.TryGetValue((tag.Group, tag.TagId), out var identities))
                        foreach (var identity in identities.Where(x => x.AssetId != tag.AssetId))
                            alternatives[identity].Add(new(relative, i, tag.Name, tag.AssetId, tag.Checksum));
                }
                for (var i = 0; i < metadata.BlockCount; i++) metadata.Block(i);
                for (var i = 0; i < metadata.ResourceCount; i++) metadata.Resource(i);
                if (catalog.Checkpoint(request.PackageFullName, relative, metadata, cancellation)) reused++;
                modules.Add(new(relative, metadata.FileLength, metadata.Digest, metadata.Revision, tagCount, size));
            }
            if (!files.SequenceEqual(ForgePaths.Inventory(request.ForgeRoot))) throw Changed();
            // Validate the entire acquired snapshot again: an update must not mix generations.
            var checkedModules = 0;
            foreach (var module in modules)
            {
                cancellation.ThrowIfCancellationRequested();
                progress.Report(new("Verifying Forge snapshot", checkedModules++, modules.Count, module.Path, reused));
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                for (var offset = 0; offset < module.TableBytes;)
                {
                    var count = Math.Min(1024 * 1024, module.TableBytes - offset);
                    var chunk = reader.Read(module.Path, offset, count, cancellation);
                    if (chunk.FileLength != module.Length || chunk.Bytes.Length != count) throw Changed();
                    digest.AppendData(chunk.Bytes); offset += count;
                }
                if (Convert.ToHexString(digest.GetHashAndReset()) != module.Digest) throw Changed();
            }
            current = null; progress.Report(new("Checking Forge access to the cache", 0, 0));
            Directory.CreateDirectory(SafePaths.Child(cache.Root, "forge"));
            var probePath = SafePaths.Child(cache.Root, "forge/probe-" + Guid.NewGuid().ToString("N") + ".bin");
            CacheProbe probe;
            try
            {
                var nonce = RandomNumberGenerator.GetBytes(32);
                using (var stream = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(nonce); stream.Flush(true); }
                probe = reader.Probe(probePath, nonce, cancellation);
            }
            finally { if (File.Exists(probePath)) File.Delete(probePath); }
            cancellation.ThrowIfCancellationRequested();
            var catalogId = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Rules, request.PackageFullName, Modules = modules })));
            var report = new ForgeReport(1, Rules, cache.Id, request.PackageFullName, request.PlanId, catalogId, modules.ToArray(),
                missing.Select(x => new ForgeMatch(x, candidates[x].ToArray(), alternatives[x].ToArray())).ToArray(), probe, reader.Mode,
                ["Resolve effective native patch ordering; these are table candidates, not proof of loaded tags.",
                 "Acquire and validate native schemas, convert campaign tags and resolve resource payloads.",
                 "Investigate different asset IDs separately; sharing a group/tag ID does not establish a safe replacement.",
                 "Build game modules and test the complete output from Forge before enabling campaign launch."]);
            var summary = ForgeReportStore.Publish(cache, report, reused, cancellation);
            CacheLog.Write(cache.Root, $"Forge report {summary.ReportId}: {summary.NativeMatches}/{summary.MissingIdentities} missing identities have native candidates; cache probe {probe.State}.");
            return new("Prepared", summary, Message: "Forge table comparison saved. Game conversion is still required.");
        }
        catch (OperationCanceledException) { return new("Paused", Message: "Forge preparation paused. Completed module checkpoints were kept. Click Prepare Forge data to resume; a startup request already sent to Windows may still complete."); }
        catch (Exception e)
        {
            if (cache is not null && locked) CacheLog.TryWrite(cache.Root, $"Forge preparation failed at {current}: {e}");
            return new("Failed", Code: e is CacheException known ? known.Code : "FORGE_PREPARATION_FAILED", Message: e.Message, Details: $"Module: {current}\n{e}");
        }
    }
    private static CacheException Changed() => new("FORGE_CHANGED", "Forge’s files changed during collection. Let updates finish, check Forge again, then retry.");
    private static FileStream Lock(string root)
    {
        try { return new(SafePaths.Child(root, "index.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new CacheException("CACHE_BUSY", "Another worker is using this cache. Let it finish before preparing Forge data."); }
    }
}
