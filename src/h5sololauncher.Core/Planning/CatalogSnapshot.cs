using System.Security.Cryptography;
using System.Text.Json;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Storage;
using Microsoft.Data.Sqlite;

namespace H5SoloLauncher.Core.Planning;

internal sealed record CatalogTag(string File, int Item, string Name, AssetReference Identity, string Checksum);

internal sealed class CatalogSnapshot : IDisposable
{
    private readonly SqliteConnection db;
    public PlanInput[] Files { get; }
    public string Fingerprint { get; }
    public IndexSummary Summary { get; }
    public CatalogSnapshot(CacheLocation cache, SourceDescriptor source)
    {
        Summary = IndexCatalog.ReadSummary(cache.Root) ?? throw new CacheException("INDEX_REQUIRED", "Index the campaign files before checking dependencies.");
        if (Summary.State != "Indexed") throw new CacheException("INDEX_REQUIRED", "Finish or resume indexing before checking dependencies.");
        db = new(new SqliteConnectionStringBuilder { DataSource = SafePaths.Child(cache.Root, "catalog.sqlite"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        try
        {
            db.Open();
            if (Meta("cache-id") != cache.Id || Meta("source-identity") != source.Identity)
                throw new CacheException("CACHE_SOURCE_MISMATCH", "The index belongs to another cache or dump version. Index into a matching cache first.");
            using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT Path,Kind,Length,Digest FROM Files ORDER BY Path";
            using var reader = cmd.ExecuteReader(); List<PlanInput> files = [];
            while (reader.Read()) files.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3)));
            Files = files.ToArray();
            Fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { source.Identity, Files })));
        }
        catch { db.Dispose(); throw; }
    }
    private string? Meta(string key)
    { using var c = db.CreateCommand(); c.CommandText = "SELECT Value FROM Meta WHERE Key=$key"; c.Parameters.AddWithValue("$key", key); return c.ExecuteScalar() as string; }
    public List<CatalogTag> InModule(string file) => Query("File=$file", ("$file", file));
    public List<CatalogTag> AllTags() => Query("1=1");
    public List<CatalogTag> Candidates(AssetReference identity) => Query("TagGroup=$g AND TagId=$t AND AssetId=$a", ("$g", identity.Group), ("$t", identity.TagId), ("$a", identity.AssetId));
    private List<CatalogTag> Query(string where, params (string, object)[] args)
    {
        using var c = db.CreateCommand();
        c.CommandText = "SELECT File,Item,Name,TagGroup,TagId,AssetId,AssetChecksum FROM Entries WHERE StoredSize>0 AND TagId<>'ffffffff' AND (" + where + ") ORDER BY File,Item";
        foreach (var (key, value) in args) c.Parameters.AddWithValue(key, value);
        using var r = c.ExecuteReader(); List<CatalogTag> result = [];
        while (r.Read()) result.Add(new(r.GetString(0), r.GetInt32(1), r.GetString(2), new(r.GetString(3), r.GetString(4), r.GetString(5)), r.GetString(6)));
        return result;
    }
    public string[] Languages() => Files.Where(x => x.Kind == "Audio").Select(x => LanguageOf(x.Path)).Where(x => x is not null && x != "SFX")
        .Cast<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    public static string? LanguageOf(string path)
    { var parts = path.Split('/'); return parts.Length >= 3 ? parts[^2] : null; }
    public void Dispose() => db.Dispose();
}
