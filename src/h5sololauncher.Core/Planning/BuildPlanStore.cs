using System.Security.Cryptography;
using System.Text.Json;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Storage;
using Microsoft.Data.Sqlite;

namespace H5SoloLauncher.Core.Planning;

internal sealed class BuildPlanStore : IDisposable
{
    private readonly SqliteConnection db;
    private readonly CacheLocation cache;
    private sealed record SavedSummary(string CacheId, PlanSummary Summary);
    public BuildPlanStore(CacheLocation cache)
    {
        this.cache = cache;
        foreach (var path in new[] { "analysis.sqlite", "analysis.sqlite-wal", "analysis.sqlite-shm", "analysis.sqlite-journal" }) SafePaths.Child(cache.Root, path);
        db = new(new SqliteConnectionStringBuilder { DataSource = SafePaths.Child(cache.Root, "analysis.sqlite"), Pooling = false }.ToString());
        try
        {
            db.Open();
            using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA application_id"; var app = Convert.ToInt32(cmd.ExecuteScalar());
            cmd.CommandText = "PRAGMA user_version"; var version = Convert.ToInt32(cmd.ExecuteScalar());
            cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table'"; var tables = Convert.ToInt32(cmd.ExecuteScalar());
            if ((app != 0x4835504c || version != 1) && !(app == 0 && version == 0 && tables == 0))
                throw new CacheException("PLAN_DATABASE_UNSUPPORTED", "The dependency checkpoint format is not supported. Choose another cache or update the launcher.");
            cmd.CommandText = "PRAGMA application_id=1211453516; PRAGMA user_version=1; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE IF NOT EXISTS Analysis(Hash TEXT NOT NULL, Reader TEXT NOT NULL, Dependencies TEXT NOT NULL, PRIMARY KEY(Hash,Reader));";
            cmd.ExecuteNonQuery();
        }
        catch { db.Dispose(); throw; }
    }
    public AssetReference[] Analyze(TagPayload payload, out bool reused)
    {
        using var c = db.CreateCommand(); c.CommandText = "SELECT Dependencies FROM Analysis WHERE Hash=$hash AND Reader=$reader";
        c.Parameters.AddWithValue("$hash", payload.Sha256); c.Parameters.AddWithValue("$reader", TagDependencyReader.Version);
        if (c.ExecuteScalar() is string json)
        { reused = true; return JsonSerializer.Deserialize<AssetReference[]>(json) ?? throw new InvalidDataException("Dependency checkpoint is invalid."); }
        var dependencies = TagDependencyReader.Read(payload.Bytes);
        c.CommandText = "INSERT INTO Analysis VALUES($hash,$reader,$deps)"; c.Parameters.AddWithValue("$deps", JsonSerializer.Serialize(dependencies)); c.ExecuteNonQuery();
        reused = false; return dependencies;
    }
    public PlanSummary Publish(SourceBuildPlan plan, int reused, CancellationToken cancellation)
    {
        var directory = SafePaths.Child(cache.Root, "plans"); Directory.CreateDirectory(directory);
        var temporary = SafePaths.Child(cache.Root, "plans/work-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, plan); stream.Flush(true); }
            cancellation.ThrowIfCancellationRequested();
            string id; using (var stream = File.OpenRead(temporary)) id = Convert.ToHexString(SHA256.HashData(stream));
            var relative = "plans/" + id.ToLowerInvariant() + ".json"; var target = SafePaths.Child(cache.Root, relative);
            if (File.Exists(target))
            {
                using var check = File.OpenRead(target);
                if (Convert.ToHexString(SHA256.HashData(check)) != id) throw new CacheException("PLAN_FILE_DAMAGED", "A saved plan was modified. Keep it for diagnosis and choose a new cache.");
            }
            else File.Move(temporary, target);
            var summary = new PlanSummary(id, plan.InputFingerprint, plan.Bundle.Id, plan.Bundle.Title, plan.Language,
                plan.RootModules.Length, plan.Tags.Length, plan.Tags.Sum(x => x.Dependencies.Length), plan.Tags.Sum(x => x.Resources.Length),
                plan.Issues.Length, plan.Issues.Count(x => x.Code == "DEPENDENCY_MISSING"), plan.Tags.Sum(x => x.StoredBytesRead),
                plan.Tags.Sum(x => x.LogicalBytes), relative, reused,
                plan.Issues.Count(x => x.Code == "LAYER_SELECTION_PENDING"), plan.Issues.Count(x => x.Code == "RESOURCE_UNRESOLVED"), plan.Rules, plan.Bundle.Revision);
            var marker = SafePaths.Child(cache.Root, "plans/state-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { JsonSerializer.Serialize(stream, new SavedSummary(cache.Id, summary)); stream.Flush(true); }
                File.Move(marker, SafePaths.Child(cache.Root, "plan-summary.json"), overwrite: true);
            }
            finally { if (File.Exists(marker)) File.Delete(marker); }
            return summary;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static PlanSummary? ReadSummary(CacheLocation cache, string fingerprint)
    {
        var path = SafePaths.Child(cache.Root, "plan-summary.json"); if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path); if (stream.Length > 16384) throw new InvalidDataException("Plan summary is too large.");
        var saved = JsonSerializer.Deserialize<SavedSummary>(stream);
        if (saved?.CacheId != cache.Id || saved.Summary.InputFingerprint != fingerprint) return null;
        if (saved.Summary.RulesVersion != BuildPlanner.Rules ||
            !ContentBundles.All.Any(x => x.Id == saved.Summary.BundleId && x.Revision == saved.Summary.BundleRevision)) return null;
        if (saved.Summary.PlanId.Length != 64 || saved.Summary.PlanId.Any(x => !Uri.IsHexDigit(x)) ||
            saved.Summary.RelativePath != "plans/" + saved.Summary.PlanId.ToLowerInvariant() + ".json")
            throw new InvalidDataException("The saved plan path does not match its identity.");
        if (!File.Exists(SafePaths.Child(cache.Root, saved.Summary.RelativePath))) return null;
        return saved.Summary;
    }
    internal static SourceBuildPlan ReadVerified(CacheLocation cache, CatalogSnapshot snapshot, string planId)
    {
        var summary = ReadSummary(cache, snapshot.Fingerprint);
        if (summary is null || summary.PlanId != planId) throw new CacheException("PLAN_REQUIRED", "Check campaign dependencies again before preparing Forge data.");
        using var stream = File.OpenRead(SafePaths.Child(cache.Root, summary.RelativePath));
        if (stream.Length > 256L * 1024 * 1024 || Convert.ToHexString(SHA256.HashData(stream)) != summary.PlanId)
            throw new CacheException("PLAN_FILE_DAMAGED", "The saved plan was changed or is too large. Check dependencies again or choose a new cache.");
        stream.Position = 0;
        var plan = JsonSerializer.Deserialize<SourceBuildPlan>(stream);
        if (plan is null || plan.Format != 1 || plan.Rules != BuildPlanner.Rules || plan.InputFingerprint != snapshot.Fingerprint ||
            plan.Bundle.Id != summary.BundleId || plan.Bundle.Revision != summary.BundleRevision || plan.Language != summary.Language ||
            !plan.Inputs.SequenceEqual(snapshot.Files))
            throw new CacheException("PLAN_FILE_DAMAGED", "The saved plan does not agree with the current source index.");
        return plan;
    }
    public void Dispose() => db.Dispose();
}
