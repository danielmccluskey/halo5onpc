using System.Text.Json;
using System.Buffers.Binary;
using H5SoloLauncher.Core.Content;
using Microsoft.Data.Sqlite;

namespace H5SoloLauncher.Core.Storage;

public sealed class IndexCatalog : IDisposable
{
    private const int ApplicationId = 0x48354958;
    private const int SchemaVersion = 2;
    private readonly SqliteConnection db;

    public IndexCatalog(CacheLocation cache, SourceDescriptor source)
    {
        var path = SafePaths.Child(cache.Root, "catalog.sqlite");
        CheckSidecars(cache.Root);
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        try
        {
            db.Open();
            var app = Convert.ToInt32(Scalar("PRAGMA application_id"));
            var version = Convert.ToInt32(Scalar("PRAGMA user_version"));
            var empty = Convert.ToInt32(Scalar("SELECT count(*) FROM sqlite_master WHERE type='table'")) == 0;
            if (!(empty && app == 0 && version == 0) && (app != ApplicationId || version != SchemaVersion))
                throw new CacheException("CACHE_DATABASE_UNSUPPORTED", "The cache database format is not supported. Choose another cache or update the launcher.");
            Execute("PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA wal_autocheckpoint=1000;");
            Execute($"PRAGMA application_id={ApplicationId}; PRAGMA user_version={SchemaVersion};");
            Execute("""
                CREATE TABLE IF NOT EXISTS Meta(Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS Files(
                  Path TEXT PRIMARY KEY, Kind TEXT NOT NULL, Length INTEGER NOT NULL, Ticks INTEGER NOT NULL,
                  Digest TEXT NOT NULL, MetadataBytes INTEGER NOT NULL, Revision INTEGER NOT NULL, Header BLOB NOT NULL, Epoch TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS Entries(
                  File TEXT NOT NULL REFERENCES Files(Path) ON DELETE CASCADE, Item INTEGER NOT NULL,
                  Name TEXT NOT NULL, Parent INTEGER NOT NULL, ResourceStart INTEGER NOT NULL, ResourceCount INTEGER NOT NULL,
                  BlockStart INTEGER NOT NULL, BlockCount INTEGER NOT NULL, DataOffset INTEGER NOT NULL,
                  StoredSize INTEGER NOT NULL, LogicalSize INTEGER NOT NULL, Flags INTEGER NOT NULL,
                  TagGroup TEXT NOT NULL, TagId TEXT NOT NULL, AssetId TEXT NOT NULL, AssetChecksum TEXT NOT NULL,
                  PayloadState TEXT NOT NULL, Raw BLOB NOT NULL,
                  PRIMARY KEY(File,Item));
                CREATE INDEX IF NOT EXISTS AssetIdentity ON Entries(TagGroup,TagId,AssetId,AssetChecksum);
                CREATE TABLE IF NOT EXISTS Blocks(
                  File TEXT NOT NULL REFERENCES Files(Path) ON DELETE CASCADE, Item INTEGER NOT NULL,
                  StoredOffset INTEGER NOT NULL, StoredSize INTEGER NOT NULL, LogicalOffset INTEGER NOT NULL,
                  LogicalSize INTEGER NOT NULL, Flags INTEGER NOT NULL, Raw BLOB NOT NULL, PRIMARY KEY(File,Item));
                CREATE TABLE IF NOT EXISTS Resources(
                  File TEXT NOT NULL REFERENCES Files(Path) ON DELETE CASCADE, Item INTEGER NOT NULL, Target INTEGER NOT NULL, PRIMARY KEY(File,Item));
                CREATE TABLE IF NOT EXISTS AudioEntries(
                  File TEXT NOT NULL REFERENCES Files(Path) ON DELETE CASCADE, Kind TEXT NOT NULL, Id TEXT NOT NULL,
                  Language INTEGER NOT NULL, BlockSize INTEGER NOT NULL, Offset INTEGER NOT NULL, Length INTEGER NOT NULL,
                  PRIMARY KEY(File,Kind,Id,Language));
                """);
            var owner = Get("cache-id");
            if (owner is not null && owner != cache.Id) throw new CacheException("CACHE_ID_MISMATCH", "The index does not belong to this cache folder.");
            var identity = Get("source-identity");
            if (identity is not null && identity != source.Identity) throw new CacheException("CACHE_SOURCE_MISMATCH", "This cache belongs to another dump version. Choose a new cache folder.");
            Set("cache-id", cache.Id); Set("source-identity", source.Identity);
        }
        catch { db.Dispose(); throw; }
    }

    public static IndexSummary? ReadSummary(string root)
    {
        var path = SafePaths.Child(root, "catalog.sqlite");
        if (!File.Exists(path)) return null;
        CheckSidecars(root);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA application_id";
        if (Convert.ToInt32(command.ExecuteScalar()) != ApplicationId) throw new CacheException("CACHE_DATABASE_UNSUPPORTED", "This database is not a supported launcher index.");
        command.CommandText = "PRAGMA user_version";
        if (Convert.ToInt32(command.ExecuteScalar()) != SchemaVersion) throw new CacheException("CACHE_DATABASE_UNSUPPORTED", "Update the launcher or choose a new cache to use this index.");
        command.CommandText = "SELECT Value FROM Meta WHERE Key='summary'";
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<IndexSummary>(json) : null;
    }

    private static void CheckSidecars(string root)
    {
        foreach (var name in new[] { "catalog.sqlite-wal", "catalog.sqlite-shm", "catalog.sqlite-journal" }) SafePaths.Child(root, name);
    }

    public void SaveSummary(IndexSummary summary) => Set("summary", JsonSerializer.Serialize(summary));

    public IndexSummary Summary(SourceDescriptor source, string state) => new(state, source.Root, source.PackageVersion,
        Convert.ToInt32(Scalar("SELECT count(*) FROM Files")), Convert.ToInt32(Scalar("SELECT count(*) FROM Files WHERE Kind='Module'")),
        Convert.ToInt64(Scalar("SELECT count(*) FROM Entries")), Convert.ToInt64(Scalar("SELECT count(*) FROM Blocks")),
        Convert.ToInt64(Scalar("SELECT count(*) FROM AudioEntries")), Convert.ToInt64(Scalar("SELECT coalesce(sum(Length),0) FROM Files")),
        Convert.ToInt64(Scalar("SELECT coalesce(sum(MetadataBytes),0) FROM Files")), state == "Indexed" ? DateTimeOffset.UtcNow.ToString("O") : null,
        Convert.ToInt64(Scalar("SELECT count(*) FROM Entries WHERE PayloadState='Unresolved'")));

    public bool Store(string path, FileMetadata metadata, string epoch, CancellationToken cancellation)
    {
        using (var prior = db.CreateCommand())
        {
            prior.CommandText = "SELECT Kind,Length,Digest FROM Files WHERE Path=$p"; prior.Parameters.AddWithValue("$p", path);
            using var reader = prior.ExecuteReader();
            if (reader.Read() && reader.GetString(0) == metadata.Kind && reader.GetInt64(1) == metadata.FileLength && reader.GetString(2) == metadata.Digest)
            {
                reader.Close();
                using var update = db.CreateCommand(); update.CommandText = "UPDATE Files SET Epoch=$e,Ticks=$t WHERE Path=$p";
                update.Parameters.AddWithValue("$e", epoch); update.Parameters.AddWithValue("$t", metadata.Ticks); update.Parameters.AddWithValue("$p", path);
                update.ExecuteNonQuery(); return true;
            }
        }
        using var tx = db.BeginTransaction();
        using (var remove = Command(tx, "DELETE FROM Files WHERE Path=$p", "$p")) { Fill(remove, path); }
        using (var file = Command(tx, "INSERT INTO Files VALUES($p,$k,$l,$t,$d,$m,$r,$h,$e)", "$p", "$k", "$l", "$t", "$d", "$m", "$r", "$h", "$e"))
            Fill(file, path, metadata.Kind, metadata.FileLength, metadata.Ticks, metadata.Digest, metadata.TableBytes, metadata.Revision, metadata.Header, epoch);
        if (metadata.Kind == "Module")
        {
            using var entries = Command(tx, "INSERT INTO Entries VALUES($f,$i,$n,$p,$rs,$rc,$bs,$bc,$o,$s,$l,$flags,$g,$t,$a,$c,$state,$raw)",
                "$f", "$i", "$n", "$p", "$rs", "$rc", "$bs", "$bc", "$o", "$s", "$l", "$flags", "$g", "$t", "$a", "$c", "$state", "$raw");
            for (var i = 0; i < metadata.ItemCount; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var e = metadata.Entry(i);
                // Revision-23 resource entries can leave aggregate sizes zero while
                // declaring their extent in the three section sizes and block table.
                var sectionSize = (long)BinaryPrimitives.ReadUInt32LittleEndian(e.Raw.AsSpan(68)) +
                    BinaryPrimitives.ReadUInt32LittleEndian(e.Raw.AsSpan(72)) + BinaryPrimitives.ReadUInt32LittleEndian(e.Raw.AsSpan(76));
                var logicalLimit = e.LogicalSize > 0 ? e.LogicalSize : sectionSize;
                var payloadState = "Unverified";
                for (var j = 0; j < e.BlockCount; j++)
                {
                    var block = metadata.Block(e.BlockIndex + j);
                    var physicalSize = block.Flags == 0 ? block.LogicalSize : block.StoredSize;
                    if (logicalLimit > 0 && (block.LogicalOffset > logicalLimit || block.LogicalSize > logicalLimit - block.LogicalOffset))
                        throw new CacheException("METADATA_INVALID", $"Entry {i} contains an out-of-range logical block.");
                    if (e.DataOffset < 0 || block.StoredOffset > metadata.FileLength - metadata.TableBytes - e.DataOffset ||
                        physicalSize > metadata.FileLength - metadata.TableBytes - e.DataOffset - block.StoredOffset)
                    {
                        // The extracted 'any' corpus has zero-size records with stale or absent
                        // local payload ranges. Retain their tables for later cross-platform
                        // resolution, without treating these ranges as readable or converted.
                        if (e.StoredSize != 0 || e.LogicalSize != 0)
                            throw new CacheException("METADATA_INVALID", $"Entry {i} points outside the module payload area.");
                        payloadState = "Unresolved";
                    }
                }
                Fill(entries, path, i, e.Name, e.Parent, e.ResourceIndex, e.ResourceCount, e.BlockIndex, e.BlockCount,
                    e.DataOffset, e.StoredSize, e.LogicalSize, e.Flags, e.Group, e.TagId, e.AssetId, e.Checksum, payloadState, e.Raw);
            }
            using var blocks = Command(tx, "INSERT INTO Blocks VALUES($f,$i,$o,$s,$lo,$ls,$flags,$r)", "$f", "$i", "$o", "$s", "$lo", "$ls", "$flags", "$r");
            for (var i = 0; i < metadata.BlockCount; i++)
            {
                cancellation.ThrowIfCancellationRequested(); var b = metadata.Block(i);
                Fill(blocks, path, i, b.StoredOffset, b.StoredSize, b.LogicalOffset, b.LogicalSize, b.Flags, b.Raw);
            }
            using var resources = Command(tx, "INSERT INTO Resources VALUES($f,$i,$t)", "$f", "$i", "$t");
            for (var i = 0; i < metadata.ResourceCount; i++)
            { cancellation.ThrowIfCancellationRequested(); Fill(resources, path, i, metadata.Resource(i)); }
        }
        else if (metadata.Kind == "Audio")
        {
            using var audio = Command(tx, "INSERT INTO AudioEntries VALUES($f,$k,$id,$l,$b,$o,$s)", "$f", "$k", "$id", "$l", "$b", "$o", "$s");
            foreach (var entry in metadata.AudioEntries())
            { cancellation.ThrowIfCancellationRequested(); Fill(audio, path, entry.Kind, entry.Id, entry.Language, entry.BlockSize, entry.Offset, entry.Length); }
        }
        cancellation.ThrowIfCancellationRequested();
        tx.Commit();
        return false;
    }

    public void RemoveAbsent(string epoch)
    {
        using var command = db.CreateCommand(); command.CommandText = "DELETE FROM Files WHERE Epoch<>$e";
        command.Parameters.AddWithValue("$e", epoch); command.ExecuteNonQuery();
    }

    private SqliteCommand Command(SqliteTransaction tx, string sql, params string[] names)
    {
        var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var name in names) command.Parameters.Add(new SqliteParameter(name, DBNull.Value));
        command.Prepare(); return command;
    }
    private static void Fill(SqliteCommand command, params object[] values)
    { for (var i = 0; i < values.Length; i++) command.Parameters[i].Value = values[i]; command.ExecuteNonQuery(); }
    private object? Scalar(string sql) { using var c = db.CreateCommand(); c.CommandText = sql; return c.ExecuteScalar(); }
    private void Execute(string sql) { using var c = db.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
    private string? Get(string key) { using var c = db.CreateCommand(); c.CommandText = "SELECT Value FROM Meta WHERE Key=$k"; c.Parameters.AddWithValue("$k", key); return c.ExecuteScalar() as string; }
    private void Set(string key, string value)
    { using var c = db.CreateCommand(); c.CommandText = "INSERT INTO Meta VALUES($k,$v) ON CONFLICT(Key) DO UPDATE SET Value=$v"; c.Parameters.AddWithValue("$k", key); c.Parameters.AddWithValue("$v", value); c.ExecuteNonQuery(); }
    public void Dispose() => db.Dispose();
}
