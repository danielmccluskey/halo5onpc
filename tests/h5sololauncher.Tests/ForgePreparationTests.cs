using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Forge;
using H5SoloLauncher.Core.Storage;
using H5SoloLauncher.Services;
using Xunit;

namespace H5SoloLauncher.Tests;

public sealed class ForgePreparationTests
{
    internal const string Package = "Microsoft.Halo5Forge_1.194.6192.2_x64__8wekyb3d8bbwe";
    internal sealed class Fixture : IDisposable
    {
        public PlanFixture Plan { get; } = new(missing: true);
        public string ForgeRoot { get; }
        public ForgeRequest Request { get; }
        public byte[] Module { get; }
        public string Global => "deploy/any/levels/globals-rtx-1.module";
        public Fixture()
        {
            ForgeRoot = Path.Combine(Plan.Files.Destination, "Forge");
            Directory.CreateDirectory(Path.Combine(ForgeRoot, "deploy", "any", "levels"));
            Directory.CreateDirectory(Path.Combine(ForgeRoot, "deploy", "pc", "levels"));
            var raw = PlanFixture.Module(new("native texture", "bitm", 999, 9999, 1, PlanFixture.Header([])),
                new("native variant", "bitm", 999, 9999, 2, PlanFixture.Header([])),
                new("wrong asset", "bitm", 999, 8888, 3, PlanFixture.Header([])));
            Module = new byte[raw.Length + 8]; raw.AsSpan(0, 48).CopyTo(Module); raw.AsSpan(48).CopyTo(Module.AsSpan(56)); IndexFixture.I(Module, 4, 27);
            File.WriteAllBytes(Path.Combine(ForgeRoot, Global.Replace('/', '\\')), Module);
            var plan = Plan.Run(); Assert.NotNull(plan.Summary);
            Request = new(Plan.Files.Root, Plan.Files.Cache, ForgeRoot, Package, plan.Summary!.PlanId);
        }
        public ForgeResult Run(FakeReader? reader = null, Action<IndexProgress>? report = null, CancellationToken token = default, ForgeRequest? request = null) =>
            new ForgePreparation().Run(request ?? Request, () => reader ?? new FakeReader(this), new PlanFixture.Callback(report ?? (_ => { })), token);
        public ForgeReport Report(ForgeResult result) => JsonSerializer.Deserialize<ForgeReport>(File.ReadAllText(SafePaths.Child(Plan.Files.Cache, result.Summary!.RelativePath)))!;
        public void Dispose() => Plan.Dispose();
    }
    internal sealed class FakeReader(Fixture fixture) : IForgeFileReader
    {
        public string Mode => "Synthetic test reader";
        public CacheProbe ProbeResult { get; set; } = new("Passed", "Synthetic probe passed.");
        public int Calls { get; private set; }
        public Func<int, ForgeRead, ForgeRead>? Transform { get; set; }
        public ForgeRead Read(string relative, long offset, int count, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested(); Calls++;
            var read = new ForgeRead(fixture.Module.AsSpan((int)offset, count).ToArray(), fixture.Module.Length, 42);
            return Transform?.Invoke(Calls, read) ?? read;
        }
        public CacheProbe Probe(string path, byte[] expected, CancellationToken cancellation)
        {
            Assert.True(SafePaths.Within(fixture.Plan.Files.Cache, path)); Assert.Equal(32, expected.Length);
            Assert.Equal(expected, File.ReadAllBytes(path)); return ProbeResult;
        }
        public void Dispose() { }
    }
    [Fact]
    public void SavesExactNativeCandidatesAndReusesVerifiedTables()
    {
        using var f = new Fixture(); var first = f.Run(); Assert.Equal("Prepared", first.State);
        Assert.Equal(1, first.Summary!.NativeMatches); Assert.Equal(3, first.Summary.Tags); Assert.Equal(0, first.Summary.Unmatched);
        var match = Assert.Single(f.Report(first).MissingReferences); Assert.Equal(PlanFixture.Missing, match.Identity);
        Assert.Equal(2, match.Candidates.Length); Assert.Equal(new[] { "0000000000000001", "0000000000000002" }, match.Candidates.Select(x => x.Checksum));
        Assert.Equal("00000000000022b8", Assert.Single(match.DifferentAssetIds!).AssetId);
        Assert.Equal(0, first.Summary.DifferentAssetIds); // Already has exact candidates; never count as an unresolved alternative.
        var second = f.Run(); Assert.Equal(first.Summary.ReportId, second.Summary!.ReportId); Assert.Equal(1, second.Summary.ReusedModules);
        var cache = CacheFolders.Open(f.Request.CacheRoot, f.Request.SourceRoot, f.ForgeRoot);
        Assert.NotNull(ForgeReportStore.Read(cache, Package, f.Request.PlanId));
        Assert.Null(ForgeReportStore.Read(cache, Package.Replace("6192", "6193"), f.Request.PlanId));
        Assert.Null(ForgeReportStore.Read(cache, Package, new string('A', 64)));
        Assert.Empty(Directory.GetFiles(Path.Combine(f.Request.CacheRoot, "forge"), "probe-*.bin"));
    }
    [Fact]
    public void VersionChangeGetsIndependentCheckpoints()
    {
        using var f = new Fixture(); var first = f.Run();
        var next = f.Run(request: f.Request with { PackageFullName = Package.Replace("6192", "6193") });
        Assert.Equal("Prepared", next.State); Assert.Equal(0, next.Summary!.ReusedModules); Assert.NotEqual(first.Summary!.CatalogId, next.Summary.CatalogId);
    }
    [Fact]
    public void SameTagWithDifferentAssetIdStaysUnmatchedAndHasSeparateDiagnostics()
    {
        using var f = new Fixture();
        // Keep the same group/tag ID, but make the two exact candidates use different asset IDs.
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(f.Module.AsSpan(56 + 48), 7777);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(f.Module.AsSpan(56 + 88 + 48), 7778);
        var result = f.Run(); Assert.Equal("Prepared", result.State);
        Assert.Equal(0, result.Summary!.NativeMatches); Assert.Equal(1, result.Summary.Unmatched); Assert.Equal(1, result.Summary.DifferentAssetIds);
        var match = Assert.Single(f.Report(result).MissingReferences); Assert.Empty(match.Candidates); Assert.Equal(3, match.DifferentAssetIds!.Length);
    }
    [Fact]
    public void PreviousVersionReportsRemainReadableIncludingDeniedProbe()
    {
        using var f = new Fixture(); var result = f.Run(new(f) { ProbeResult = new("Denied", "Old denied message", "FORGE_READ_DENIED") });
        var report = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(SafePaths.Child(f.Request.CacheRoot, result.Summary!.RelativePath)))!;
        report["Rules"] = "forge-global-candidates-1";
        foreach (var reference in report["MissingReferences"]!.AsArray()) reference!.AsObject().Remove("DifferentAssetIds");
        report["Probe"]!.AsObject().Remove("Details");
        var bytes = System.Text.Encoding.UTF8.GetBytes(report.ToJsonString()); var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        var relative = "forge/" + id.ToLowerInvariant() + ".json"; File.WriteAllBytes(SafePaths.Child(f.Request.CacheRoot, relative), bytes);
        var markerPath = Path.Combine(f.Request.CacheRoot, "forge-summary.json"); var marker = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(markerPath))!;
        marker["Summary"]!["ReportId"] = id; marker["Summary"]!["RelativePath"] = relative; marker["Summary"]!.AsObject().Remove("DifferentAssetIds");
        File.WriteAllText(markerPath, marker.ToJsonString());
        var restored = ForgeReportStore.Read(CacheFolders.Open(f.Request.CacheRoot, f.Request.SourceRoot, f.ForgeRoot), Package, f.Request.PlanId);
        Assert.NotNull(restored); Assert.Equal("Denied", restored.Probe.State); Assert.Equal(0, restored.DifferentAssetIds);
    }
    [Theory]
    [InlineData("Denied")]
    [InlineData("NotTested")]
    public void MetadataReportDoesNotClaimCacheReadinessWithoutSuccessfulProbe(string state)
    {
        using var f = new Fixture(); var reader = new FakeReader(f) { ProbeResult = new(state, "Test access result", "TEST") };
        var result = f.Run(reader); Assert.Equal("Prepared", result.State); Assert.Equal(state, result.Summary!.Probe.State);
        Assert.Equal(state, f.Report(result).Probe.State);
    }
    [Fact]
    public void TamperedPlanFailsBeforeOpeningForge()
    {
        using var f = new Fixture(); var path = Path.Combine(f.Request.CacheRoot, "plans", f.Request.PlanId.ToLowerInvariant() + ".json");
        File.AppendAllText(path, " "); var reader = new FakeReader(f);
        Assert.Equal("PLAN_FILE_DAMAGED", f.Run(reader).Code); Assert.Equal(0, reader.Calls);
    }
    [Fact]
    public void ChangedTablesAndPausePreservePreviousCompleteReport()
    {
        using var f = new Fixture(); var saved = f.Run(); var marker = Path.Combine(f.Request.CacheRoot, "forge-summary.json"); var before = File.ReadAllBytes(marker);
        var reader = new FakeReader(f) { Transform = (call, read) => call == 2 ? read with { FileLength = read.FileLength + 1 } : read };
        Assert.Equal("FORGE_CHANGED", f.Run(reader).Code); Assert.Equal(before, File.ReadAllBytes(marker));
        using var stop = new CancellationTokenSource();
        var paused = f.Run(report: p => { if (p.Stage == "Checking Forge access to the cache") stop.Cancel(); }, token: stop.Token);
        Assert.Equal("Paused", paused.State); Assert.Equal(before, File.ReadAllBytes(marker));
        var resumed = f.Run(); Assert.Equal(saved.Summary!.ReportId, resumed.Summary!.ReportId); Assert.Equal(1, resumed.Summary.ReusedModules);
    }
    [Fact]
    public void ChangedEarlierModuleIsCaughtByFinalSnapshotVerification()
    {
        using var f = new Fixture(); var reader = new FakeReader(f)
        {
            Transform = (call, read) =>
            {
                if (call == 4) read.Bytes[17] ^= 1;
                return read;
            }
        };
        Assert.Equal("FORGE_CHANGED", f.Run(reader).Code); Assert.False(File.Exists(Path.Combine(f.Request.CacheRoot, "forge-summary.json")));
    }
    [Fact]
    public void TruncatedDetachedTablesAndUnknownRevisionAreRejected()
    {
        using var f = new Fixture(); var size = FileMetadata.ModuleTableLength(f.Module, f.Module.Length);
        Assert.Throws<CacheException>(() => FileMetadata.FromModuleTables(f.Module[..(size - 1)], f.Module.Length, 0));
        var metadata = FileMetadata.FromModuleTables(f.Module[..size], f.Module.Length, 0); Assert.Equal(3, metadata.ItemCount); Assert.True(metadata.Entry(0).StoredSize > 0);
        IndexFixture.I(f.Module, 4, 99); Assert.Equal("METADATA_INVALID", f.Run().Code);
    }
    [Fact]
    public void CacheLockAndTamperedReportAreActionable()
    {
        using var f = new Fixture();
        using (var lease = new FileStream(Path.Combine(f.Request.CacheRoot, "index.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal("CACHE_BUSY", f.Run().Code);
        var result = f.Run(); File.AppendAllText(SafePaths.Child(f.Request.CacheRoot, result.Summary!.RelativePath), " ");
        var cache = CacheFolders.Open(f.Request.CacheRoot, f.Request.SourceRoot, f.ForgeRoot);
        Assert.Equal("FORGE_REPORT_DAMAGED", Assert.Throws<CacheException>(() => ForgeReportStore.Read(cache, Package, f.Request.PlanId)).Code);
    }
    [Theory]
    [InlineData("deploy/any/levels/globals-rtx-1.module", true)]
    [InlineData("deploy/pc/levels/globals.module", true)]
    [InlineData("deploy/pc/levels/globals-rtx-20-1.module", true)]
    [InlineData("deploy/pc/levels/globals/../../secret.module", false)]
    [InlineData("C:/deploy/pc/levels/globals.module", false)]
    [InlineData("deploy/pc/levels/globals.module:secret", false)]
    [InlineData("deploy/pc/levels/globals-.module", false)]
    [InlineData("deploy/x1/levels/globals.module", false)]
    public void OnlyExpectedRelativeGlobalPathsAreAllowed(string path, bool expected) => Assert.Equal(expected, ForgePaths.IsGlobal(path));

    [Fact]
    public async Task WorkerReturnsTypedForgeFailureWithoutAttemptingGameAttachment()
    {
        using var f = new Fixture();
        var result = await new IndexWorkerClient(Environment.GetEnvironmentVariable("H5SOLO_TEST_WORKER"))
            .PrepareForgeAsync(f.Request with { PackageFullName = "wrong-package" }, new PlanFixture.Callback(_ => { }), default);
        Assert.Equal("Failed", result.State); Assert.Equal("FORGE_IDENTITY_INVALID", result.Code);
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint ReadNative(nint request);
    [Fact]
    public void PackagedNativeReaderRejectsBadProtocolAndNonForgeHosts()
    {
        var library = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "h5sololauncher.ForgeReader.dll"));
        var memory = Marshal.AllocHGlobal(1114160);
        try
        {
            Marshal.Copy(new byte[1114160], 0, memory, 1114160);
            var read = Marshal.GetDelegateForFunctionPointer<ReadNative>(NativeLibrary.GetExport(library, "H5Read"));
            Assert.Equal(87u, read(memory));
            Marshal.WriteInt32(memory, 0, 0x48354652); Marshal.WriteInt32(memory, 4, 1); Marshal.WriteInt32(memory, 8, 1); Marshal.WriteInt32(memory, 16, 48);
            Assert.Equal(5u, read(memory)); Assert.Equal(5, Marshal.ReadInt32(memory, 12));
            Marshal.WriteInt32(memory, 16, 1048577); Assert.Equal(87u, read(memory));
        }
        finally { Marshal.FreeHGlobal(memory); NativeLibrary.Free(library); }
    }
}
