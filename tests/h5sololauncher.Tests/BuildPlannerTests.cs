using System.IO;
using System.Text.Json;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Planning;
using H5SoloLauncher.Core.Storage;
using H5SoloLauncher.Services;
using Xunit;

namespace H5SoloLauncher.Tests;

public sealed class BuildPlannerTests
{
    [Fact]
    public void ResolvesExactDependenciesAndCyclesWithoutNativeCaptures()
    {
        using var f = new PlanFixture(); var result = f.Run(); Assert.Equal("Planned", result.State);
        var plan = f.Read(result); Assert.Equal(4, plan.Tags.Length); Assert.Empty(plan.Issues);
        Assert.Contains(plan.Tags, x => x.Identity == PlanFixture.Model && !x.Root);
        Assert.All(plan.Tags, x => Assert.Equal(64, x.PayloadSha256.Length));
        Assert.All(plan.Inputs, x => Assert.False(Path.IsPathRooted(x.Path)));
        Assert.Contains(plan.RemainingSteps, x => x.Contains("Forge"));
        Assert.DoesNotContain(f.Files.Root, JsonSerializer.Serialize(plan));
    }

    [Fact]
    public void RepeatedAndRelocatedRunsHaveTheSamePlanIdentity()
    {
        using var f = new PlanFixture(); var first = f.Run(); var second = f.Run();
        Assert.Equal(first.Summary!.PlanId, second.Summary!.PlanId); Assert.Equal(second.Summary.Tags, second.Summary.ReusedAnalyses);
        using var other = new PlanFixture(); Assert.Equal(first.Summary.PlanId, other.Run().Summary!.PlanId);
        Assert.Equal(first.Summary.PlanId, BuildPlanner.Inspect(f.Files.Cache, f.Files.Root).SavedPlan!.PlanId);
    }

    [Fact]
    public void ReportsMissingReferencesVersionsAndUnresolvedResourcesWithoutGuessing()
    {
        using var f = new PlanFixture(missing: true, variants: true, resource: true); var result = f.Run();
        Assert.Equal("Planned", result.State); var plan = f.Read(result);
        Assert.Contains(plan.Issues, x => x.Code == "DEPENDENCY_MISSING" && x.Identity == PlanFixture.Missing);
        Assert.Contains(plan.Issues, x => x.Code == "LAYER_SELECTION_PENDING" && x.Identity == PlanFixture.Model);
        Assert.Contains(plan.Issues, x => x.Code == "RESOURCE_UNRESOLVED");
        Assert.Equal(2, plan.Tags.Count(x => x.Identity == PlanFixture.Model));
    }

    [Fact]
    public void PauseRetainsOldPlanAndResumeReusesAnalysisCheckpoints()
    {
        using var f = new PlanFixture(); var original = f.Run(); using var stop = new CancellationTokenSource();
        var paused = f.Run(p => { if (p.Stage == "Reading campaign dependencies" && p.Completed == 2) stop.Cancel(); }, stop.Token);
        Assert.Equal("Paused", paused.State);
        Assert.Equal(original.Summary!.PlanId, BuildPlanner.Inspect(f.Files.Cache, f.Files.Root).SavedPlan!.PlanId);
        Assert.Equal(original.Summary.PlanId, f.Run().Summary!.PlanId);
    }

    [Fact]
    public void TableChangesRequireReindexingEvenWithOriginalTimestamp()
    {
        using var f = new PlanFixture(); var path = Path.Combine(f.Files.Root, f.RootModule);
        var stamp = File.GetLastWriteTimeUtc(path); var bytes = File.ReadAllBytes(path); bytes[48 + 56] ^= 1;
        File.WriteAllBytes(path, bytes); File.SetLastWriteTimeUtc(path, stamp);
        Assert.Equal("DUMP_CHANGED", f.Run().Code);
    }

    [Fact]
    public void SameTimestampPayloadChangesAreRehashedBeforeAnalysisReuse()
    {
        using var f = new PlanFixture();
        var payload = PlanFixture.Header([]).Concat(new byte[] { 0 }).ToArray();
        f.Files.Write(f.RootModule, PlanFixture.Module(new PlanFixture.TestTag("levels\\campaignworld030\\w3_halsey\\w3_halsey.scenario", "scnr", 100, 1001, 1, payload, Compressed: false)));
        f.Reindex(); var before = f.Run();
        var path = Path.Combine(f.Files.Root, f.RootModule); var stamp = File.GetLastWriteTimeUtc(path);
        var bytes = File.ReadAllBytes(path); bytes[^1] = 1; File.WriteAllBytes(path, bytes); File.SetLastWriteTimeUtc(path, stamp);
        var after = f.Run(); Assert.Equal("Planned", after.State);
        Assert.NotEqual(before.Summary!.PlanId, after.Summary!.PlanId);
    }

    [Fact]
    public void ModifiedSavedPlanCannotBeSilentlyReused()
    {
        using var f = new PlanFixture(); var original = f.Run();
        File.WriteAllText(SafePaths.Child(f.Files.Cache, original.Summary!.RelativePath), "{}");
        Assert.Equal("PLAN_FILE_DAMAGED", f.Run().Code);
    }

    [Fact]
    public async Task WorkerPlanCanPauseAndResumeThroughThePipe()
    {
        using var f = new PlanFixture();
        for (var i = 0; i < 200; i++) f.Files.Write($"__cms__/pause/{i:D4}.mapinfo", [1, 2]);
        f.Reindex(); using var stop = new CancellationTokenSource();
        var worker = new IndexWorkerClient(Environment.GetEnvironmentVariable("H5SOLO_TEST_WORKER"));
        var paused = await worker.PlanAsync(f.Request, new PlanFixture.Callback(_ => stop.Cancel()), stop.Token);
        Assert.Equal("Paused", paused.State);
        Assert.Equal("Planned", (await worker.PlanAsync(f.Request, new PlanFixture.Callback(_ => { }), default)).State);
    }

    [Fact]
    public void InvalidTagPayloadIsRecordedWithItsPhysicalSource()
    {
        using var f = new PlanFixture();
        f.Files.Write(f.RootModule, PlanFixture.Module(new PlanFixture.TestTag("levels\\campaignworld030\\w3_halsey\\w3_halsey.scenario", "scnr", 100, 1001, 1, new byte[80])));
        f.Reindex(); var plan = f.Read(f.Run());
        Assert.Contains(plan.Issues, x => x.Code == "TAG_METADATA_INVALID" && x.File == f.RootModule && x.Item == 0);
    }

    [Fact]
    public void StaleSummaryIsNotReopenedAfterIndexChanges()
    {
        using var f = new PlanFixture(); Assert.Equal("Planned", f.Run().State);
        f.Files.Write("__cms__/changed.mapinfo", [5]); f.Reindex();
        Assert.Null(BuildPlanner.Inspect(f.Files.Cache, f.Files.Root).SavedPlan);
    }

    [Fact]
    public void CacheLockMissingIndexAndMissingLanguageAreActionable()
    {
        using var f = new PlanFixture();
        using (var lease = new FileStream(Path.Combine(f.Files.Cache, "index.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal("CACHE_IN_USE", f.Run().Code);
        Assert.Equal("AUDIO_LANGUAGE_MISSING", new BuildPlanner().Run(f.Request with { Language = "missing" }, new PlanFixture.Callback(_ => { }), default).Code);
        using var empty = new IndexFixture(); CacheFolders.Select(empty.Destination, empty.Root, null);
        Assert.Equal("INDEX_REQUIRED", new BuildPlanner().Run(new(empty.Root, empty.Cache), new PlanFixture.Callback(_ => { }), default).Code);
    }

    [Fact]
    public async Task PublishedProtocolSupportsThePlanOperation()
    {
        using var f = new PlanFixture();
        var worker = new IndexWorkerClient(Environment.GetEnvironmentVariable("H5SOLO_TEST_WORKER"));
        var result = await worker.PlanAsync(f.Request, new PlanFixture.Callback(_ => { }), default);
        Assert.Equal("Planned", result.State); Assert.Equal(4, result.Summary!.Tags);
    }

    [Fact]
    public void DependencyBoundsAndSentinelReferencesAreChecked()
    {
        var valid = PlanFixture.Header([PlanFixture.Model]); Assert.Equal(PlanFixture.Model, Assert.Single(TagDependencyReader.Read(valid)));
        var negative = valid.ToArray(); IndexFixture.I(negative, 84, -2);
        Assert.Throws<CacheException>(() => TagDependencyReader.Read(negative));
        IndexFixture.I(valid, 28, int.MaxValue); Assert.Throws<CacheException>(() => TagDependencyReader.Read(valid));
        Assert.Empty(TagDependencyReader.Read(PlanFixture.Header([new("mode", "ffffffff", "0000000000000000")])));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PayloadReaderHandlesRawAndCompressedImplicitBlocks(bool compressed)
    {
        using var f = new IndexFixture(); var payload = PlanFixture.Header([PlanFixture.Model]);
        File.WriteAllBytes(f.ModulePath, PlanFixture.Module(new PlanFixture.TestTag("tag", "scnr", 1, 1, 1, payload, Compressed: compressed)));
        var metadata = FileMetadata.Read(f.ModulePath, "Module", default);
        using var stream = File.OpenRead(f.ModulePath);
        Assert.Equal(payload, ModulePayloadReader.Read(stream, metadata, metadata.Entry(0), default).Bytes);
    }

    [Fact]
    public void ExplicitBlocksRejectGapsAndCorruptZlib()
    {
        using var f = new IndexFixture(); var bytes = IndexFixture.Module(23);
        IndexFixture.I(bytes, bytes.Length - 4 - 20 + 8, 1); File.WriteAllBytes(f.ModulePath, bytes);
        var metadata = FileMetadata.Read(f.ModulePath, "Module", default);
        using (var stream = File.OpenRead(f.ModulePath)) Assert.Equal("TAG_BLOCK_COVERAGE", Assert.Throws<CacheException>(() => ModulePayloadReader.Read(stream, metadata, metadata.Entry(0), default)).Code);
        bytes = IndexFixture.Module(23); IndexFixture.I(bytes, bytes.Length - 4 - 20 + 16, 1); File.WriteAllBytes(f.ModulePath, bytes);
        metadata = FileMetadata.Read(f.ModulePath, "Module", default);
        using var corrupt = File.OpenRead(f.ModulePath);
        Assert.Equal("TAG_DECOMPRESSION_FAILED", Assert.Throws<CacheException>(() => ModulePayloadReader.Read(corrupt, metadata, metadata.Entry(0), default)).Code);
    }
}
