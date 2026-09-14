using System.Security.Cryptography;
using System.Text.Json;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Storage;
using Microsoft.Data.Sqlite;

namespace H5SoloLauncher.Core.Forge;

internal sealed class ForgeCatalog : IDisposable
{
    private readonly SqliteConnection db;
    public ForgeCatalog(CacheLocation cache)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) SafePaths.Child(cache.Root, "forge.sqlite" + suffix);
        db = new(new SqliteConnectionStringBuilder { DataSource = SafePaths.Child(cache.Root, "forge.sqlite"), Pooling = false }.ToString());
        try
        {
            db.Open(); using var c = db.CreateCommand();
            c.CommandText = "PRAGMA application_id"; var app = Convert.ToInt32(c.ExecuteScalar());
            c.CommandText = "PRAGMA user_version"; var version = Convert.ToInt32(c.ExecuteScalar());
            c.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table'"; var tables = Convert.ToInt32(c.ExecuteScalar());
            if ((app != 0x48354647 || version != 1) && !(app == 0 && version == 0 && tables == 0))
                throw new CacheException("FORGE_CATALOG_UNSUPPORTED", "The Forge catalog format is not supported. Choose another cache or update the launcher.");
            c.CommandText = "PRAGMA application_id=1211450951; PRAGMA user_version=1; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; " +
                "CREATE TABLE IF NOT EXISTS Owner(Id TEXT NOT NULL); " +
                "CREATE TABLE IF NOT EXISTS Modules(Package TEXT, Path TEXT, Digest TEXT, Length INTEGER, Tables BLOB NOT NULL, PRIMARY KEY(Package,Path,Digest,Length)); " +
                "CREATE TABLE IF NOT EXISTS Entries(Package TEXT, Path TEXT, Digest TEXT, Length INTEGER, Item INTEGER, Name TEXT, TagGroup TEXT, TagId TEXT, AssetId TEXT, Checksum TEXT, StoredSize INTEGER, PRIMARY KEY(Package,Path,Digest,Length,Item)); " +
                "CREATE INDEX IF NOT EXISTS NativeIdentity ON Entries(Package,TagGroup,TagId,AssetId);";
            c.ExecuteNonQuery(); c.CommandText = "SELECT Id FROM Owner";
            var owner = c.ExecuteScalar() as string;
            if (owner is null) { c.CommandText = "INSERT INTO Owner VALUES($id)"; c.Parameters.AddWithValue("$id", cache.Id); c.ExecuteNonQuery(); }
            else if (owner != cache.Id) throw new CacheException("FORGE_CATALOG_OWNER", "This Forge catalog belongs to another cache.");
        }
        catch { db.Dispose(); throw; }
    }
    public bool Checkpoint(string package, string path, FileMetadata metadata, CancellationToken cancellation)
    {
        using var c = db.CreateCommand();
        c.CommandText = "SELECT Tables FROM Modules WHERE Package=$p AND Path=$f AND Digest=$d AND Length=$l";
        c.Parameters.AddWithValue("$p", package); c.Parameters.AddWithValue("$f", path); c.Parameters.AddWithValue("$d", metadata.Digest); c.Parameters.AddWithValue("$l", metadata.FileLength);
        if (c.ExecuteScalar() is byte[] saved)
        {
            if (Convert.ToHexString(SHA256.HashData(saved)) != metadata.Digest)
                throw new CacheException("FORGE_CATALOG_DAMAGED", "Saved Forge tables were modified. Choose a new cache and keep this one for diagnosis.");
            return true;
        }
        using var transaction = db.BeginTransaction(); c.Transaction = transaction;
        c.CommandText = "INSERT INTO Modules VALUES($p,$f,$d,$l,$bytes)"; c.Parameters.AddWithValue("$bytes", metadata.Tables); c.ExecuteNonQuery();
        c.CommandText = "INSERT INTO Entries VALUES($p,$f,$d,$l,$i,$n,$g,$t,$a,$s,$z)";
        foreach (var key in new[] { "$i", "$n", "$g", "$t", "$a", "$s", "$z" }) c.Parameters.AddWithValue(key, "");
        for (var i = 0; i < metadata.ItemCount; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var entry = metadata.Entry(i); c.Parameters["$i"].Value = i; c.Parameters["$n"].Value = entry.Name;
            c.Parameters["$g"].Value = entry.Group; c.Parameters["$t"].Value = entry.TagId; c.Parameters["$a"].Value = entry.AssetId;
            c.Parameters["$s"].Value = entry.Checksum; c.Parameters["$z"].Value = entry.StoredSize; c.ExecuteNonQuery();
        }
        transaction.Commit();
        return false;
    }
    public void Dispose() => db.Dispose();
}

public static class ForgeReportStore
{
    private sealed record Saved(string CacheId, ForgeSummary Summary);
    internal static ForgeSummary Publish(CacheLocation cache, ForgeReport report, int reused, CancellationToken cancellation)
    {
        Directory.CreateDirectory(SafePaths.Child(cache.Root, "forge"));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report); var id = Convert.ToHexString(SHA256.HashData(bytes));
        var relative = "forge/" + id.ToLowerInvariant() + ".json";
        Write(cache.Root, relative, bytes, immutable: true); cancellation.ThrowIfCancellationRequested();
        var matches = report.MissingReferences.Count(x => x.Candidates.Length > 0);
        var summary = new ForgeSummary(id, relative, report.PackageFullName, report.PlanId, report.CatalogId, report.Modules.Length,
            report.Modules.Sum(x => x.Tags), report.Modules.Sum(x => (long)x.TableBytes), report.MissingReferences.Length, matches,
            report.MissingReferences.Length - matches, reused, report.Probe, report.ReaderMode,
            report.MissingReferences.Count(x => x.Candidates.Length == 0 && x.DifferentAssetIds?.Length > 0));
        Write(cache.Root, "forge-summary.json", JsonSerializer.SerializeToUtf8Bytes(new Saved(cache.Id, summary)), immutable: false);
        return summary;
    }
    public static ForgeSummary? Read(CacheLocation cache, string package, string planId)
    {
        var path = SafePaths.Child(cache.Root, "forge-summary.json"); if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path); if (stream.Length > 16384) throw new InvalidDataException("Forge summary is too large.");
        var saved = JsonSerializer.Deserialize<Saved>(stream); var summary = saved?.Summary;
        if (saved?.CacheId != cache.Id || summary is null || summary.PackageFullName != package || summary.PlanId != planId) return null;
        if (summary.ReportId.Length != 64 || summary.ReportId.Any(x => !Uri.IsHexDigit(x)) || summary.RelativePath != "forge/" + summary.ReportId.ToLowerInvariant() + ".json")
            throw new InvalidDataException("Forge report path is invalid.");
        using var report = File.OpenRead(SafePaths.Child(cache.Root, summary.RelativePath));
        if (report.Length > 128L * 1024 * 1024 || Convert.ToHexString(SHA256.HashData(report)) != summary.ReportId)
            throw new CacheException("FORGE_REPORT_DAMAGED", "The saved Forge report was changed. Prepare Forge data again.");
        report.Position = 0; var content = JsonSerializer.Deserialize<ForgeReport>(report);
        if (content is null || content.Format != 1 || (content.Rules != ForgePreparation.Rules && content.Rules != "forge-global-candidates-1") || content.CacheId != cache.Id ||
            content.PlanId != planId || content.PackageFullName != package || content.CatalogId != summary.CatalogId)
            throw new CacheException("FORGE_REPORT_DAMAGED", "The saved Forge report does not match this plan and package. Prepare Forge data again.");
        var matches = content.MissingReferences.Count(x => x.Candidates.Length > 0);
        return summary with { Modules = content.Modules.Length, Tags = content.Modules.Sum(x => x.Tags), TableBytes = content.Modules.Sum(x => (long)x.TableBytes),
            MissingIdentities = content.MissingReferences.Length, NativeMatches = matches, Unmatched = content.MissingReferences.Length - matches,
            Probe = content.Probe, ReaderMode = content.ReaderMode,
            DifferentAssetIds = content.MissingReferences.Count(x => x.Candidates.Length == 0 && x.DifferentAssetIds?.Length > 0) };
    }
    private static void Write(string root, string relative, byte[] bytes, bool immutable)
    {
        var target = SafePaths.Child(root, relative); var temporary = SafePaths.Child(root, "forge/work-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            if (immutable && File.Exists(target))
            {
                using var stream = File.OpenRead(target);
                if (!SHA256.HashData(stream).AsSpan().SequenceEqual(SHA256.HashData(bytes))) throw new CacheException("FORGE_REPORT_DAMAGED", "A Forge report was modified. Choose a new cache.");
            }
            else File.Move(temporary, target, overwrite: !immutable);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
