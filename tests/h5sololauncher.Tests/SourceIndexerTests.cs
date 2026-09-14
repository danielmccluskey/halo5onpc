using System.IO;
using System.Security.Cryptography;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace H5SoloLauncher.Tests;

public sealed class SourceIndexerTests
{
    private sealed class Progress(Action<IndexProgress> callback) : IProgress<IndexProgress>
    { public void Report(IndexProgress value) => callback(value); }
    private static IndexResult Run(IndexFixture f, Action<IndexProgress>? report = null, CancellationToken cancellation = default)
    {
        CacheFolders.Select(f.Destination, f.Root, null);
        return new SourceIndexer().Run(new(f.Root, f.Cache), new Progress(report ?? (_ => { })), cancellation);
    }

    [Fact]
    public void IndexesSyntheticTablesWithoutChangingOrCopyingGamePayloads()
    {
        using var f = new IndexFixture();
        var hash = SHA256.HashData(File.ReadAllBytes(f.ModulePath));
        var result = Run(f);
        Assert.Equal("Indexed", result.State); Assert.Equal(2, result.Summary!.Modules);
        Assert.Equal(2, result.Summary.Entries); Assert.Equal(2, result.Summary.Blocks); Assert.Equal(1, result.Summary.AudioEntries);
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(f.ModulePath)));
        Assert.Empty(Directory.GetFiles(f.Cache, "*.module", SearchOption.AllDirectories));
        Assert.Equal("Indexed", IndexCatalog.ReadSummary(f.Cache)!.State);
        using var db = Open(f);
        Assert.Equal("scnr", Scalar(db, "SELECT TagGroup FROM Entries LIMIT 1"));
        Assert.Equal("000000000000002a", Scalar(db, "SELECT Id FROM AudioEntries"));
        Assert.Equal(0L, Scalar(db, "SELECT count(*) FROM Files WHERE instr(Path,':')>0"));
        Assert.Empty(ReadForeignKeys(db));
    }

    [Fact]
    public void ReusesUnchangedFilesButDetectsSameSizeSameTimestampTableChanges()
    {
        using var f = new IndexFixture(); Assert.Equal("Indexed", Run(f).State);
        var reused = 0;
        var warm = Run(f, p => reused = Math.Max(reused, p.Reused));
        Assert.Equal(warm.Summary!.Files, reused);
        var stamp = File.GetLastWriteTimeUtc(f.ModulePath);
        var bytes = File.ReadAllBytes(f.ModulePath); IndexFixture.I(bytes, 48 + 44, 321);
        File.WriteAllBytes(f.ModulePath, bytes); File.SetLastWriteTimeUtc(f.ModulePath, stamp);
        reused = 0; Assert.Equal("Indexed", Run(f, p => reused = Math.Max(reused, p.Reused)).State);
        Assert.Equal(warm.Summary.Files - 1, reused);
        using var db = Open(f); Assert.Equal("00000141", Scalar(db, "SELECT TagId FROM Entries WHERE File LIKE 'deploy/any/%'"));
    }

    [Fact]
    public void PausedRunResumesAndKeepsCompletedCheckpoints()
    {
        using var f = new IndexFixture(); using var stop = new CancellationTokenSource();
        var paused = Run(f, p => { if (p.Completed == 3) stop.Cancel(); }, stop.Token);
        Assert.Equal("Paused", paused.State); Assert.Equal(3, paused.Summary!.Files);
        var reused = 0; var resumed = Run(f, p => reused = Math.Max(reused, p.Reused));
        Assert.Equal("Indexed", resumed.State); Assert.True(reused >= 3);
    }

    [Fact]
    public void InvalidChangedFileRollsBackItsTransactionAndCanBeRepaired()
    {
        using var f = new IndexFixture(); Assert.Equal("Indexed", Run(f).State);
        var original = File.ReadAllBytes(f.ModulePath);
        var damaged = original.ToArray(); IndexFixture.I(damaged, 48, int.MaxValue); File.WriteAllBytes(f.ModulePath, damaged);
        var failed = Run(f); Assert.Equal("METADATA_INVALID", failed.Code); Assert.Equal("Failed", IndexCatalog.ReadSummary(f.Cache)!.State);
        using (var db = Open(f)) Assert.Equal(2L, Scalar(db, "SELECT count(*) FROM Entries"));
        File.WriteAllBytes(f.ModulePath, original); Assert.Equal("Indexed", Run(f).State);
    }

    [Fact]
    public void SectionSizesPermitZeroAggregateResourceSizes()
    {
        using var f = new IndexFixture(); File.WriteAllBytes(f.ModulePath, IndexFixture.Module(23, sectionOnly: true));
        Assert.Equal("Indexed", Run(f).State);
        using var db = Open(f); Assert.Equal(0L, Scalar(db, "SELECT LogicalSize FROM Entries WHERE File LIKE 'deploy/any/%'"));
    }

    [Fact]
    public void ZeroSizeEntriesWithUnavailablePayloadRemainIndexedAndExplicitlyUnresolved()
    {
        using var f = new IndexFixture(); var bytes = IndexFixture.Module(23, sectionOnly: true);
        IndexFixture.I(bytes, 48 + 24, bytes.Length); File.WriteAllBytes(f.ModulePath, bytes);
        var result = Run(f); Assert.Equal("Indexed", result.State); Assert.Equal(1, result.Summary!.UnresolvedPayloads);
        using var db = Open(f); Assert.Equal("Unresolved", Scalar(db, "SELECT PayloadState FROM Entries WHERE File LIKE 'deploy/any/%'"));
    }

    [Fact]
    public void UncompressedBlockUsesLogicalSizeForPhysicalRangeValidation()
    {
        using var f = new IndexFixture(); var bytes = IndexFixture.Module(23, sectionOnly: true);
        IndexFixture.I(bytes, 48 + 68, 100); // Larger declared logical section.
        IndexFixture.I(bytes, bytes.Length - 4 - 20 + 12, 100); // Stored field stays 4, flag stays 0.
        File.WriteAllBytes(f.ModulePath, bytes);
        Assert.Equal(1, Run(f).Summary!.UnresolvedPayloads);
    }

    [Fact]
    public void RemovedAndAddedFilesAreReconciledOnlyOnCompletedRuns()
    {
        using var f = new IndexFixture(); Assert.Equal("Indexed", Run(f).State);
        File.Delete(Path.Combine(f.Root, "__cms__/rtx/levels/campaignworld010/example/example.mapinfo"));
        f.Write("__cms__/rtx/replacement.mapinfo", [6, 7]);
        Assert.Equal("Indexed", Run(f).State);
        using var db = Open(f); Assert.Equal(0L, Scalar(db, "SELECT count(*) FROM Files WHERE Path LIKE '%example.mapinfo'"));
        Assert.Equal(1L, Scalar(db, "SELECT count(*) FROM Files WHERE Path LIKE '%replacement.mapinfo'"));
    }

    [Fact]
    public void SourceChangesDuringRunPreventIndexedStatus()
    {
        using var f = new IndexFixture();
        var result = Run(f, p => { if (p.Stage == "Checking source snapshot") f.Write("__cms__/new.mapinfo", [1]); });
        Assert.Equal("DUMP_CHANGED", result.Code); Assert.Equal("Failed", result.State);
    }

    [Fact]
    public void PackageIdentityChangeBeforeInventoryCannotPublishTheOldIdentity()
    {
        using var f = new IndexFixture();
        var result = Run(f, p =>
        {
            if (p.Stage != "Finding source files") return;
            var manifest = Path.Combine(f.Root, "AppxManifest.xml");
            File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("1.1.31695.21", "2.0.0.0"));
        });
        Assert.Equal("DUMP_CHANGED", result.Code); Assert.Equal("Failed", result.State);
    }

    [Fact]
    public void CacheLockAndSpaceFailuresDoNotDestroyCheckpoints()
    {
        using var f = new IndexFixture(); Assert.Equal("Indexed", Run(f).State);
        using (var lease = new FileStream(Path.Combine(f.Cache, "index.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal("CACHE_IN_USE", Run(f).Code);
        var lowSpace = new SourceIndexer(_ => 0).Run(new(f.Root, f.Cache), new Progress(_ => { }), default);
        Assert.Equal("CACHE_NEEDS_SPACE", lowSpace.Code);
        Assert.Equal("Indexed", IndexCatalog.ReadSummary(f.Cache)!.State);
    }

    [Theory]
    [InlineData("counts")]
    [InlineData("truncated")]
    [InlineData("block")]
    [InlineData("audio")]
    public void MalformedMetadataFailsWithFileDetails(string damage)
    {
        using var f = new IndexFixture();
        var bytes = File.ReadAllBytes(f.ModulePath);
        if (damage == "counts") IndexFixture.I(bytes, 16, int.MaxValue);
        if (damage == "truncated") bytes = bytes[..20];
        if (damage == "block") IndexFixture.I(bytes, bytes.Length - 4 - 20 + 8, int.MaxValue);
        if (damage == "audio")
        { var audio = IndexFixture.Audio(); IndexFixture.I(audio, 44, int.MaxValue); f.Write("sound/english/soundbank.pck", audio); }
        else File.WriteAllBytes(f.ModulePath, bytes);
        var result = Run(f); Assert.Equal("Failed", result.State); Assert.Contains("File:", result.Details);
    }

    [Fact]
    public void RefusesAnotherSourceVersionInExistingCache()
    {
        using var f = new IndexFixture(); Assert.Equal("Indexed", Run(f).State);
        var manifest = Path.Combine(f.Root, "AppxManifest.xml"); File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("1.1.31695.21", "2.0.0.0"));
        Assert.Equal("CACHE_SOURCE_MISMATCH", Run(f).Code);
    }

    private static SqliteConnection Open(IndexFixture f)
    { var db = new SqliteConnection($"Data Source={Path.Combine(f.Cache, "catalog.sqlite")};Mode=ReadOnly;Pooling=False"); db.Open(); return db; }
    private static object? Scalar(SqliteConnection db, string sql) { using var c = db.CreateCommand(); c.CommandText = sql; return c.ExecuteScalar(); }
    private static List<string> ReadForeignKeys(SqliteConnection db) { using var c = db.CreateCommand(); c.CommandText = "PRAGMA foreign_key_check"; using var r = c.ExecuteReader(); List<string> errors = []; while (r.Read()) errors.Add(r.GetString(0)); return errors; }
}
