using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Forge;
using H5SoloLauncher.Core.Planning;
using H5SoloLauncher.Core.Preparation;
using H5SoloLauncher.Core.Storage;
using H5SoloLauncher.Services;
using H5SoloLauncher.ViewModels;
using Xunit;

namespace H5SoloLauncher.Tests;

public sealed class InputCacheTests
{
    private static InputRequest Request(ForgePreparationTests.Fixture f) =>
        new(f.Request.SourceRoot, f.Request.CacheRoot, f.ForgeRoot, f.Request.PackageFullName, f.Request.PlanId);
    private static InputResult Run(ForgePreparationTests.Fixture f, Action<IndexProgress>? report = null, CancellationToken cancellation = default, InputRequest? request = null) =>
        new InputCacheBuilder().Run(request ?? Request(f), new PlanFixture.Callback(report ?? (_ => { })), cancellation);
    private static InputManifest Manifest(ForgePreparationTests.Fixture f, InputResult result) => InputCacheStore.ReadManifest(f.Request.CacheRoot, result.Summary!);

    [Fact]
    public void CachesDeduplicatedPayloadsAndReaderRoundTripsEveryPlannedPhysicalTag()
    {
        using var f = new ForgePreparationTests.Fixture(); f.Run(); var result = Run(f);
        Assert.Equal("Cached", result.State); var summary = result.Summary!;
        Assert.Equal(4, summary.Tags); Assert.Equal(3, summary.UniquePayloads); Assert.Equal(1, summary.Packs);
        var manifest = Manifest(f, result); Assert.Equal(f.Request.PlanId, manifest.PlanId);
        Assert.Contains(manifest.RemainingSteps, x => x.Contains("resources", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(f.Request.SourceRoot, JsonSerializer.Serialize(manifest));
        var batch = InputCacheStore.ReadBatch(f.Request.CacheRoot, Assert.Single(manifest.Batches));
        var plan = JsonSerializer.Deserialize<SourceBuildPlan>(File.ReadAllText(Path.Combine(f.Request.CacheRoot, "plans", f.Request.PlanId.ToLowerInvariant() + ".json")))!;
        foreach (var tag in plan.Tags)
        {
            var path = Path.Combine(f.Request.SourceRoot, tag.File); var module = FileMetadata.Read(path, "Module", default); using var stream = File.OpenRead(path);
            var original = ModulePayloadReader.Read(stream, module, module.Entry(tag.Item), default);
            Assert.Equal(original.Bytes, InputPack.Read(f.Request.CacheRoot, batch, batch.Records.Single(x => x.Sha256 == tag.PayloadSha256)));
        }
        Assert.Empty(Directory.GetFiles(Path.Combine(f.Request.CacheRoot, "inputs", "work")));
    }

    [Fact]
    public void RepeatedAndRelocatedInputsHaveIdenticalManifestAndPackIds()
    {
        using var f = new ForgePreparationTests.Fixture(); f.Run(); var first = Run(f); var second = Run(f);
        Assert.Equal("Cached", second.State); Assert.Equal(first.Summary!.ManifestId, second.Summary!.ManifestId);
        Assert.Equal(second.Summary.Packs, second.Summary.ReusedPacks); Assert.True(second.Summary.SourceBytesRead > 0);
        using var other = new ForgePreparationTests.Fixture(); other.Run();
        Assert.Equal(first.Summary.ManifestId, Run(other).Summary!.ManifestId);
    }

    [Theory]
    [InlineData("pack")]
    [InlineData("batch")]
    [InlineData("checkpoint")]
    public void DamagedCompletedPackOrMetadataIsRebuiltBeforeReuse(string kind)
    {
        using var f = new ForgePreparationTests.Fixture(); f.Run(); var original = Run(f); var manifest = Manifest(f, original);
        var reference = Assert.Single(manifest.Batches); var batch = InputCacheStore.ReadBatch(f.Request.CacheRoot, reference);
        var relative = kind == "pack" ? $"packs/{batch.PackId.ToLowerInvariant()}.pack" : kind == "batch"
            ? $"batches/{reference.Id.ToLowerInvariant()}.json" : $"checkpoints/{reference.Key.ToLowerInvariant()}.json";
        var path = Path.Combine(f.Request.CacheRoot, "inputs", relative); var bytes = File.ReadAllBytes(path); bytes[^1] ^= 1; File.WriteAllBytes(path, bytes);
        if (kind == "pack") Assert.Throws<CacheException>(() => InputPack.Read(f.Request.CacheRoot, batch, batch.Records[^1]));
        var repaired = Run(f); Assert.Equal("Cached", repaired.State); Assert.Equal(0, repaired.Summary!.ReusedPacks);
        Assert.Equal(original.Summary!.ManifestId, repaired.Summary.ManifestId); Assert.Equal(1, Run(f).Summary!.ReusedPacks);
    }

    [Fact]
    public void SameLengthSameTimestampSourceChangeFailsEvenWhenPackExists()
    {
        using var f = new ForgePreparationTests.Fixture();
        var path = Path.Combine(f.Plan.Files.Root, f.Plan.RootModule);
        var metadata = FileMetadata.Read(path, "Module", default); using (var stream = File.OpenRead(path))
        {
            var entry = metadata.Entry(0); var payload = ModulePayloadReader.Read(stream, metadata, entry, default).Bytes;
            stream.Dispose();
            File.WriteAllBytes(path, PlanFixture.Module(new PlanFixture.TestTag(entry.Name, entry.Group, Convert.ToUInt32(entry.TagId, 16), Convert.ToUInt64(entry.AssetId, 16), 1, payload, Compressed: false)));
        }
        f.Plan.Reindex(); var plan = f.Plan.Run(); var request = f.Request with { PlanId = plan.Summary!.PlanId }; f.Run(request: request);
        var input = Request(f) with { PlanId = request.PlanId }; Assert.Equal("Cached", Run(f, request: input).State);
        var marker = Path.Combine(f.Request.CacheRoot, "inputs", "summary.json"); var before = File.ReadAllBytes(marker);
        var stamp = File.GetLastWriteTimeUtc(path); var bytes = File.ReadAllBytes(path); metadata = FileMetadata.Read(path, "Module", default);
        bytes[metadata.TableBytes + 8] ^= 1; File.WriteAllBytes(path, bytes); File.SetLastWriteTimeUtc(path, stamp);
        Assert.Equal("DUMP_CHANGED", Run(f, request: input).Code); Assert.Equal(before, File.ReadAllBytes(marker));
    }

    [Fact]
    public void PauseAfterSealingKeepsPacksWithoutPublishingAndResumeReusesThem()
    {
        using var f = new ForgePreparationTests.Fixture(); f.Run(); using var stop = new CancellationTokenSource();
        var paused = Run(f, p => { if (p.Stage == "Input pack saved") stop.Cancel(); }, stop.Token);
        Assert.Equal("Paused", paused.State); Assert.False(File.Exists(Path.Combine(f.Request.CacheRoot, "inputs", "summary.json")));
        Assert.Single(Directory.GetFiles(Path.Combine(f.Request.CacheRoot, "inputs", "packs")));
        Assert.Empty(Directory.GetFiles(Path.Combine(f.Request.CacheRoot, "inputs", "work")));
        var resumed = Run(f); Assert.Equal("Cached", resumed.State); Assert.Equal(1, resumed.Summary!.ReusedPacks);
    }

    [Fact]
    public void RecoveryRemovesOnlyOwnedAbandonedTemporaryFiles()
    {
        using var f = new ForgePreparationTests.Fixture(); f.Run();
        var work = Path.Combine(f.Request.CacheRoot, "inputs", "work"); Directory.CreateDirectory(work);
        var temporary = Path.Combine(work, Guid.NewGuid().ToString("N") + ".tmp"); var unrelated = Path.Combine(work, "notes.tmp");
        File.WriteAllBytes(temporary, [1, 2, 3]); File.WriteAllBytes(unrelated, [7]);
        Assert.Equal("Cached", Run(f).State); Assert.False(File.Exists(temporary)); Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(unrelated));
    }

    [Fact]
    public void PauseMidPackDiscardsOnlyTemporaryWorkAndKeepsPreviousManifest()
    {
        using var f = new ForgePreparationTests.Fixture(); f.Run(); var initial = Run(f);
        var manifest = Manifest(f, initial); var batch = InputCacheStore.ReadBatch(f.Request.CacheRoot, manifest.Batches[0]);
        File.WriteAllBytes(Path.Combine(f.Request.CacheRoot, "inputs", "packs", batch.PackId.ToLowerInvariant() + ".pack"), []);
        var marker = Path.Combine(f.Request.CacheRoot, "inputs", "summary.json"); var before = File.ReadAllBytes(marker);
        using var stop = new CancellationTokenSource();
        var paused = Run(f, p => { if (p.Stage == "Caching verified campaign tags" && p.Completed == 1) stop.Cancel(); }, stop.Token);
        Assert.Equal("Paused", paused.State); Assert.Equal(before, File.ReadAllBytes(marker));
        Assert.Empty(Directory.GetFiles(Path.Combine(f.Request.CacheRoot, "inputs", "work")));
        Assert.Equal(initial.Summary!.ManifestId, Run(f).Summary!.ManifestId);
    }

    [Fact]
    public void BatchesRemainBoundedAndRetainDifferentPatchPayloads()
    {
        using var f = new ForgePreparationTests.Fixture();
        var payload1 = new byte[17 * 1024 * 1024]; PlanFixture.Header([]).CopyTo(payload1, 0); IndexFixture.I(payload1, 64, payload1.Length - 80);
        var payload2 = (byte[])payload1.Clone(); payload2[8] = 1;
        var root = ContentBundles.All[0].Scenarios[0];
        f.Plan.Files.Write(f.Plan.RootModule, PlanFixture.Module(new PlanFixture.TestTag(root.Replace('/', '\\') + ".scenario", "scnr", 100, 1001, 1, payload1)));
        f.Plan.Files.Write("deploy/x1/" + root + "-rtx-20-1.module", PlanFixture.Module(new PlanFixture.TestTag(root.Replace('/', '\\') + ".scenario", "scnr", 100, 1001, 2, payload2)));
        f.Plan.Reindex(); var plan = f.Plan.Run(); f.Run(request: f.Request with { PlanId = plan.Summary!.PlanId });
        var result = Run(f, request: Request(f) with { PlanId = plan.Summary.PlanId }); Assert.Equal("Cached", result.State);
        Assert.True(result.Summary!.Packs >= 2); Assert.Equal(plan.Summary.Tags, result.Summary.Tags);
        Assert.Equal(plan.Summary.PatchChoices, result.Summary.PatchChoices);
        var manifest = Manifest(f, result);
        Assert.All(manifest.Batches, reference => Assert.True(InputCacheStore.ReadBatch(f.Request.CacheRoot, reference).PackBytes <= InputCacheBuilder.TargetPackBytes));
    }

    [Fact]
    public void MissingForgeReportTamperedPlanAndCacheLockFailBeforeWritingInputs()
    {
        using var f = new ForgePreparationTests.Fixture(); Assert.Equal("FORGE_DATA_REQUIRED", Run(f).Code);
        f.Run();
        using (var lease = new FileStream(Path.Combine(f.Request.CacheRoot, "index.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal("CACHE_IN_USE", Run(f).Code);
        File.AppendAllText(Path.Combine(f.Request.CacheRoot, "plans", f.Request.PlanId.ToLowerInvariant() + ".json"), " ");
        Assert.Equal("PLAN_FILE_DAMAGED", Run(f).Code); Assert.False(Directory.Exists(Path.Combine(f.Request.CacheRoot, "inputs")));
    }

    [Fact]
    public void ReadsNamesAndSchemaRootsWithStrictStringBounds()
    {
        var bytes = NamedHeader("objects\\example.bitmap"); var metadata = TagMetadataReader.Read(bytes);
        Assert.Equal("objects\\example.bitmap", Assert.Single(metadata.Dependencies).Name);
        Assert.Equal(PlanFixture.Missing, metadata.Dependencies[0].Identity); Assert.Equal("000000000000002a", metadata.Schema);
        Assert.Equal("0102030405060708090a0b0c0d0e0f10", Assert.Single(metadata.RootGuids));
        Assert.Equal(TagDependencyReader.Read(bytes), metadata.Dependencies.Select(x => x.Identity));
        IndexFixture.I(bytes, 84, int.MaxValue); Assert.Throws<CacheException>(() => TagMetadataReader.Read(bytes));
        bytes = NamedHeader("valid"); bytes[136] = 255; Assert.Equal("TAG_NAME_INVALID", Assert.Throws<CacheException>(() => TagMetadataReader.Read(bytes)).Code);
        bytes = NamedHeader("valid"); IndexFixture.I(bytes, 64, 1); Assert.Equal("TAG_LAYOUT_INVALID", Assert.Throws<CacheException>(() => TagMetadataReader.Read(bytes)).Code);
    }

    [Fact]
    public void NamedPlatformEvidenceNeverResolvesAnIdentityAndConflictsAreRetained()
    {
        var match = new ForgeMatch(PlanFixture.Missing, [], [new("deploy/pc/levels/globals.module", 4, "__chore\\pc__\\objects\\example{pc}.bitmap", "0123456789abcdef", "0000000000000001")]);
        var evidence = DependencyNames.Compare(match, [new(PlanFixture.Missing, "__chore\\x1__\\objects\\example{x1}.bitmap")]);
        Assert.Equal("PotentialPlatformMatch", evidence.State); Assert.Single(evidence.Candidates); Assert.Equal(match.Identity, evidence.Identity);
        Assert.Equal("ConflictingNames", DependencyNames.Compare(match, [new(PlanFixture.Missing, "objects\\example.bitmap"), new(PlanFixture.Missing, "objects\\different.bitmap")]).State);
        Assert.Equal("NameUnavailable", DependencyNames.Compare(match, []).State);
        Assert.Empty(DependencyNames.Compare(match with { DifferentAssetIds = [match.DifferentAssetIds![0] with { Name = "objects\\example.bitmap" }] },
            [new(PlanFixture.Missing, "objects\\example.bitmap")]).Candidates);
    }

    [Fact]
    public async Task WorkerBuildsInputsAndReturnsTypedFailuresWithoutOpeningForge()
    {
        using var f = new ForgePreparationTests.Fixture(); f.Run();
        var worker = new IndexWorkerClient(Environment.GetEnvironmentVariable("H5SOLO_TEST_WORKER"));
        var result = await worker.BuildInputsAsync(Request(f), new PlanFixture.Callback(_ => { }), default);
        Assert.Equal("Cached", result.State); Assert.Equal(Run(f).Summary!.ManifestId, result.Summary!.ManifestId);
        var failed = await worker.BuildInputsAsync(Request(f) with { PackageFullName = "bad" }, new PlanFixture.Callback(_ => { }), default);
        Assert.Equal("FORGE_IDENTITY_INVALID", failed.Code);
    }

    [Fact]
    public async Task WorkerInputPauseCanResumeInAnotherProcess()
    {
        using var f = new ForgePreparationTests.Fixture(); f.Run(); using var stop = new CancellationTokenSource();
        var worker = new IndexWorkerClient(Environment.GetEnvironmentVariable("H5SOLO_TEST_WORKER"));
        var paused = await worker.BuildInputsAsync(Request(f), new PlanFixture.Callback(_ => stop.Cancel()), stop.Token);
        Assert.Equal("Paused", paused.State);
        Assert.Equal("Cached", (await worker.BuildInputsAsync(Request(f), new PlanFixture.Callback(_ => { }), default)).State);
    }

    [Fact]
    public async Task ViewModelRestoresGatesOtherWorkAndWaitsForPauseOnClose()
    {
        using var f = new ForgePreparationTests.Fixture(); f.Run(); var cached = Run(f); var worker = new PendingWorker(); var model = await Model(f, worker);
        Assert.True(model.CanBuildInputs); Assert.True(model.CanOpenInputs); Assert.Contains(cached.Summary!.ManifestId, model.Details);
        var pending = model.BuildInputsAsync(); await model.BuildInputsAsync(); await model.IndexAsync();
        Assert.Equal(1, worker.Calls); Assert.True(model.IsBuildingInputs); Assert.False(model.CanPlan); Assert.False(model.CanPrepareForge);
        Assert.False(model.CanChoose); Assert.False(model.CanOpenInputs); var closing = model.PauseAndWaitAsync();
        Assert.True(worker.Token.IsCancellationRequested); Assert.False(closing.IsCompleted);
        worker.Done.SetResult(new("Paused", Message: "Test pause")); await pending; await closing;
        Assert.Equal("Input caching paused.", model.InputStatus); Assert.True(model.CanOpenInputs);
        model.SelectedLanguage = "different"; Assert.False(model.CanBuildInputs); Assert.False(model.CanOpenInputs);
        model.SelectedLanguage = "English(US)"; Assert.True(model.CanOpenInputs);
        model.Configure(f.Request.SourceRoot, f.ForgeRoot, true, "different-package"); Assert.False(model.CanOpenInputs);
    }

    [Fact]
    public async Task InputFailureHasCopyableDetailsAndLeavesBuildEnabledForRepair()
    {
        using var f = new ForgePreparationTests.Fixture(); f.Run(); var worker = new PendingWorker(); var model = await Model(f, worker);
        worker.Done.SetResult(new("Failed", Code: "CACHE_NEEDS_SPACE", Message: "Free space", Details: "stage detail"));
        await model.BuildInputsAsync(); Assert.True(model.CanBuildInputs); Assert.Contains("CACHE_NEEDS_SPACE", model.Details); Assert.Contains("stage detail", model.Details);
    }
    private static async Task<CacheViewModel> Model(ForgePreparationTests.Fixture f, PendingWorker worker)
    {
        var settings = new LauncherSettingsStore(Path.Combine(f.Plan.Files.Destination, "settings.json")); await settings.SaveCacheDirectoryAsync(f.Request.CacheRoot);
        var model = new CacheViewModel(settings, worker); model.Configure(f.Request.SourceRoot, f.ForgeRoot, true, f.Request.PackageFullName); await model.RestoreAsync(); return model;
    }
    private sealed class PendingWorker : IIndexWorker, IInputWorker
    {
        public int Calls { get; private set; }
        public CancellationToken Token { get; private set; }
        public TaskCompletionSource<InputResult> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<InputResult> BuildInputsAsync(InputRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation)
        { Calls++; Token = cancellation; return Done.Task; }
        public Task<IndexResult> RunAsync(IndexRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation) => throw new InvalidOperationException();
    }
    private static byte[] NamedHeader(string name)
    {
        var text = Encoding.UTF8.GetBytes(name + '\0'); var bytes = new byte[136 + text.Length]; PlanFixture.Header([PlanFixture.Missing]).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), 42); IndexFixture.I(bytes, 36, 1); IndexFixture.I(bytes, 52, text.Length);
        IndexFixture.I(bytes, 60, bytes.Length); IndexFixture.I(bytes, 84, 0); for (var i = 0; i < 16; i++) bytes[104 + i] = (byte)(i + 1);
        text.CopyTo(bytes, 136); return bytes;
    }
}
