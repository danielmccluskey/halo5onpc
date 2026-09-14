using System.Security.Cryptography;
using System.Text.Json;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Planning;
using H5SoloLauncher.Core.Preparation;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Core.Forge;

public sealed record CachedNativeModule(string Path, string RelativePath, string Sha256, string TableDigest, long Length, long Modified);
public sealed record NativeModuleManifest(int Format, string Rules, string PackageFullName, string CatalogId, CachedNativeModule[] Modules);
public sealed record NativeModuleResult(string State, string? ManifestId = null, CachedNativeModule[]? Modules = null, int Reused = 0, string? Code = null, string? Message = null, string? Details = null);

/// <summary>Acquires original native controls through Forge without changing package file permissions.</summary>
public static class NativeModuleCache
{
    public const string Rules = "native-module-controls-1";
    public static NativeModuleResult Run(InputRequest request, Func<IForgePayloadReader> readerFactory, IProgress<IndexProgress> progress, CancellationToken cancellation)
    {
        string? current = null; string? temporary = null; CacheLocation? cache = null; var reused = 0;
        try
        {
            ForgePaths.ValidatePackage(request.PackageFullName);
            var source = SourceDiscovery.Describe(request.SourceRoot);
            cache = CacheFolders.Open(request.CacheRoot, source.Root, request.ForgeRoot);
            using var lease = new FileStream(SafePaths.Child(cache.Root, "index.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var catalog = new CatalogSnapshot(cache, source); BuildPlanStore.ReadVerified(cache, catalog, request.PlanId);
            var summary = ForgeReportStore.Read(cache, request.PackageFullName, request.PlanId) ?? throw new CacheException("FORGE_DATA_REQUIRED", "Prepare Forge data before collecting native controls.");
            var report = InputFiles.Read<ForgeReport>(cache.Root, summary.RelativePath, 128L * 1024 * 1024, summary.ReportId);
            using var reader = readerFactory(); List<CachedNativeModule> collected = [];
            Directory.CreateDirectory(SafePaths.Child(cache.Root, "inputs/native-modules"));
            Directory.CreateDirectory(SafePaths.Child(cache.Root, "inputs/work"));
            var total = report.Modules.Sum(x => x.Length); long completed = 0;
            foreach (var module in report.Modules)
            {
                cancellation.ThrowIfCancellationRequested(); current = module.Path;
                var first = reader.Read(current, 0, 48, cancellation);
                if (first.FileLength != module.Length) throw Changed();
                var key = InputFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(new { Rules, request.PackageFullName, module.Path, module.Digest, module.Length, first.LastWriteFileTime }));
                var relative = "inputs/native-modules/" + key.ToLowerInvariant() + ".module";
                var checkpoint = "inputs/native-modules/" + key.ToLowerInvariant() + ".json";
                var target = SafePaths.Child(cache.Root, relative);
                CachedNativeModule? saved = null;
                if (File.Exists(target) && File.Exists(SafePaths.Child(cache.Root, checkpoint)))
                {
                    progress.Report(new("Verifying saved native module (MiB)", checked((int)(completed / 1048576)), checked((int)(total / 1048576)), current, reused));
                    try
                    {
                        var candidate = InputFiles.Read<CachedNativeModule>(cache.Root, checkpoint, 16384);
                        using var stream = File.OpenRead(target);
                        if (candidate.Path == current && candidate.RelativePath == relative && candidate.TableDigest == module.Digest && candidate.Length == stream.Length &&
                            candidate.Length == first.FileLength && candidate.Modified == first.LastWriteFileTime && InputFiles.Digest(stream, cancellation) == candidate.Sha256)
                            saved = candidate;
                    }
                    catch (Exception e) when (e is IOException or JsonException || e is CacheException { Code: "INPUT_CACHE_DAMAGED" }) { }
                }
                if (saved is null)
                {
                    if (new DriveInfo(Path.GetPathRoot(cache.Root)!).AvailableFreeSpace < module.Length + 512L * 1024 * 1024)
                        throw new CacheException("NATIVE_CACHE_SPACE", $"The cache drive needs {(module.Length + 512L * 1024 * 1024) / (1024.0 * 1024 * 1024):N1} GiB free to collect this native module. Completed modules were kept.");
                    temporary = SafePaths.Child(cache.Root, "inputs/work/" + Guid.NewGuid().ToString("N") + ".tmp");
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024))
                    {
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        for (long offset = 0; offset < module.Length;)
                        {
                            cancellation.ThrowIfCancellationRequested(); var count = (int)Math.Min(1024 * 1024, module.Length - offset);
                            var read = reader.ReadPayload(current, offset, count, cancellation);
                            if (read.FileLength != module.Length || read.LastWriteFileTime != first.LastWriteFileTime || read.Bytes.Length != count) throw Changed();
                            output.Write(read.Bytes); hash.AppendData(read.Bytes); offset += count;
                            progress.Report(new("Collecting native game controls (MiB)", checked((int)((completed + offset) / 1048576)), checked((int)(total / 1048576)), current, reused));
                        }
                        output.Flush(true);
                        saved = new(current, relative, Convert.ToHexString(hash.GetHashAndReset()), module.Digest, module.Length, first.LastWriteFileTime);
                    }
                    var metadata = FileMetadata.Read(temporary, "Module", cancellation);
                    if (metadata.Digest != module.Digest || metadata.FileLength != module.Length) throw Changed();
                    using (var verify = File.OpenRead(temporary))
                        if (InputFiles.Digest(verify, cancellation) != saved.Sha256) throw new CacheException("NATIVE_CACHE_VERIFY", "A native module failed read-back verification. Check the cache drive and retry.");
                    cancellation.ThrowIfCancellationRequested();
                    File.Move(temporary, target, overwrite: true); temporary = null;
                    InputFiles.Write(cache.Root, checkpoint, JsonSerializer.SerializeToUtf8Bytes(saved));
                }
                else reused++;
                VerifyCurrent(reader, module, saved.Modified, cancellation);
                collected.Add(saved); completed += module.Length;
            }
            if (!report.Modules.Select(x => x.Path).SequenceEqual(ForgePaths.Inventory(request.ForgeRoot))) throw Changed();
            foreach (var module in report.Modules)
                VerifyCurrent(reader, module, collected.Single(x => x.Path == module.Path).Modified, cancellation);
            var manifest = new NativeModuleManifest(1, Rules, request.PackageFullName, summary.CatalogId, collected.ToArray());
            cancellation.ThrowIfCancellationRequested();
            var id = InputFiles.Save(cache.Root, "native-manifests", manifest);
            CacheLog.Write(cache.Root, $"Native controls {id}: {collected.Count} original modules, {total} bytes, {reused} reused. Conversion pending.");
            return new("Cached", id, collected.ToArray(), reused, Message: "Native controls collected from installed Forge.");
        }
        catch (OperationCanceledException) { return new("Paused", Reused: reused, Message: "Native collection paused. Completed modules were kept."); }
        catch (Exception e) { return new("Failed", Reused: reused, Code: e is CacheException known ? known.Code : "NATIVE_MODULE_FAILED", Message: e.Message, Details: $"Module: {current}\n{e}"); }
        finally { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void VerifyCurrent(IForgeFileReader reader, ForgeModule module, long modified, CancellationToken cancellation)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var offset = 0; offset < module.TableBytes;)
        {
            var count = Math.Min(1024 * 1024, module.TableBytes - offset); var chunk = reader.Read(module.Path, offset, count, cancellation);
            if (chunk.FileLength != module.Length || chunk.LastWriteFileTime != modified || chunk.Bytes.Length != count) throw Changed();
            hash.AppendData(chunk.Bytes); offset += count;
        }
        if (Convert.ToHexString(hash.GetHashAndReset()) != module.Digest) throw Changed();
    }
    private static CacheException Changed() => new("FORGE_CHANGED", "Forge's files changed during native collection. Let updates finish, check Forge again, then resume.");
}
