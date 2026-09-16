using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Conversion;
using H5SoloLauncher.Core.Preparation;
using H5SoloLauncher.Core.Runtime;
using H5SoloLauncher.Core.Storage;
using H5SoloLauncher.Services;
using H5SoloLauncher.ViewModels;
using Xunit;

namespace H5SoloLauncher.Tests;

public sealed class PlayableCacheTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(19)]
    public void SealedAndLegacyCachesAcceptAllSupportedCampaignBundles(int available)
    {
        using var f=new Fixture(available);
        Assert.Equal(available,PlayableCache.Open(f.Root,"",Fixture.Package).Manifest.Maps.Count(x=>x.Available));
        f.MakeLegacy();
        Assert.Equal(available,PlayableCache.Open(f.Root,"",Fixture.Package).Manifest.Maps.Count(x=>x.Available));
    }
    [Fact]
    public void RejectsAnIncompleteGlassedBundleOrAReplacedScenario()
    {
        using var f=new Fixture(4);
        Assert.Throws<CacheException>(()=>PlayableCache.Open(f.Root,"",Fixture.Package));
        f.Manifest=f.Manifest with {Maps=f.Manifest.Maps.Select((m,i)=>i==4?m with {Available=true,Scenario="levels/unprepared"}:m).ToArray()};f.Seal();
        Assert.Throws<CacheException>(()=>PlayableCache.Open(f.Root,"",Fixture.Package));
    }
    [Fact]
    public void MovedCacheWithoutDumpIndexOrOldManifestsResolvesNewPaths()
    {
        using var f = new Fixture();
        var before = PlayableCache.Open(f.Root, "", Fixture.Package);
        var first = PlayableCache.Resolve(before);
        var moved = f.Root + " relocated";
        Directory.Move(f.Root, moved); f.Root = moved;
        var after = PlayableCache.Open(moved, "", Fixture.Package);
        var runtime = PlayableCache.Resolve(after);
        Assert.Equal(before.Id, after.Id);
        Assert.NotEqual(first.ContentId, runtime.ContentId);
        Assert.NotEqual(first.Prepared.MovieConfigId, runtime.Prepared.MovieConfigId);
        Assert.Equal(first.Prepared.MenuConfigId, runtime.Prepared.MenuConfigId);
        Assert.Contains(moved, Encoding.UTF8.GetString(File.ReadAllBytes(runtime.ContentPath)));
        using var wire = new BinaryReader(File.OpenRead(runtime.ContentPath));
        wire.ReadUInt32(); wire.ReadInt32(); wire.ReadBytes(wire.ReadInt32());
        Assert.Equal(moved, Encoding.UTF8.GetString(wire.ReadBytes(wire.ReadInt32())));
        Assert.False(File.Exists(Path.Combine(moved, "catalog.sqlite")));
        Assert.False(File.Exists(Path.Combine(moved, "prepared.json")));
        Assert.False(Directory.Exists(f.Source));
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("metadata")]
    [InlineData("config")]
    public void MissingOrChangedFilesFailBeforeForgeStarts(string kind)
    {
        using var f = new Fixture();
        var path = kind switch
        {
            "payload" => Path.Combine(f.Root, f.Manifest.Files[0].CachePath),
            "metadata" => Path.Combine(f.Root, "game/metadata/" + f.Manifest.Maps[0].MetadataSha256.ToLowerInvariant() + ".mapinfo"),
            _ => PreparedCampaignStore.ConfigPath(f.Root, "menu-config", f.Manifest.Prepared.MenuConfigId)
        };
        if (kind == "payload") File.Delete(path);
        else { var bytes = File.ReadAllBytes(path); bytes[0] ^= 1; File.WriteAllBytes(path, bytes); }
        var error = Assert.Throws<CacheException>(() => PlayableCache.Open(f.Root, "", Fixture.Package));
        Assert.Contains("dump", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../outside.bin")]
    [InlineData("game/test:stream")]
    [InlineData("C:/outside.bin")]
    public void RejectsNonPortablePaths(string path)
    {
        using var f = new Fixture();
        f.Manifest = f.Manifest with { Files = f.Manifest.Files.Select((x, i) => i == 0 ? x with { CachePath = path } : x).ToArray() };
        f.Seal();
        Assert.Throws<CacheException>(() => PlayableCache.Open(f.Root, "", Fixture.Package));
    }

    [Fact]
    public void RequiresMatchingForgeVersion()
    {
        using var f = new Fixture();
        Assert.Equal("PLAYABLE_FORGE_MISMATCH", Assert.Throws<CacheException>(() => PlayableCache.Open(f.Root, "", "other-package")).Code);
    }

    [Fact]
    public void RegistryUsesOnlyCachedMetadataAndRejectsDifferentGameResults()
    {
        using var f = new Fixture(); var cache = PlayableCache.Open(f.Root, "", Fixture.Package);
        using var registry = new Registry(f.Manifest.Maps);
        PlayableCache.Register(cache, registry, new Progress<IndexProgress>(), default);
        Assert.All(registry.Inputs!, x => Assert.True(File.Exists(Path.Combine(f.Root, x.CachePath))));
        Assert.Equal(registry.Inputs!.Select(x=>x.SourcePath).Order(StringComparer.Ordinal),registry.Inputs.Select(x=>x.SourcePath));
        registry.Wrong = true;
        Assert.Equal("CAMPAIGN_CATALOGUE_CHANGED", Assert.Throws<CacheException>(() => PlayableCache.Register(cache, registry, new Progress<IndexProgress>(), default)).Code);
    }

    [Fact]
    public async Task CacheOnlyUiPlaysWithoutPreparingAndRestoresSelection()
    {
        using var f = new Fixture(); var worker = new Worker(); var settings = new LauncherSettingsStore(Path.Combine(f.Root, "settings.json"));
        var model = new CacheViewModel(settings, worker); model.Configure("", "", false, Fixture.Package, true);
        Assert.True(model.CanChoose); await model.ChooseAsync(f.Root);
        Assert.Equal("Play", model.PrimaryLabel); Assert.True(model.CanPrimary); Assert.False(model.CanPrepare); Assert.False(model.CanIndex);
        await model.PrimaryAsync(); Assert.Equal("", worker.Request!.Locations.SourceRoot);
        Assert.Contains("No dump", model.PreparationMessage);
        var restored = new CacheViewModel(settings, worker); restored.Configure("", "", false, Fixture.Package, true);
        await restored.RestoreAsync(); Assert.True(restored.CanPrimary); Assert.Equal("Play", restored.PrimaryLabel);
        await model.PauseAndWaitAsync(); await restored.PauseAndWaitAsync();
    }

    [Fact]
    public async Task IncompleteCacheCannotPrepareWithoutDumpButCanWithDump()
    {
        using var f = new Fixture(); File.Delete(Path.Combine(f.Root, f.Manifest.Files[0].CachePath));
        var model = new CacheViewModel(new LauncherSettingsStore(Path.Combine(f.Root, "settings.json")), new Worker());
        model.Configure("", "", false, Fixture.Package, true); await model.ChooseAsync(f.Root);
        Assert.False(model.CanPrimary); Assert.Contains("dump", model.PreparationMessage);
        model.Configure(f.Source, "", true, Fixture.Package, true);
        Assert.True(model.CanPrepare); await model.PauseAndWaitAsync();
    }

    [Fact]
    public void CompleteLegacyCacheUpgradesWithoutSourceAndThenNeedsNoLegacyManifests()
    {
        using var f = new Fixture(); f.MakeLegacy();
        var cache = PlayableCache.Open(f.Root, "", Fixture.Package);
        Assert.Equal(5, cache.Manifest.Files.Length);
        foreach (var folder in new[] { "prepared-manifests", "game-manifests", "audio-manifests", "catalogue-manifests", "menu-artwork-manifests" })
            Directory.Delete(Path.Combine(f.Root, "inputs", folder), true);
        File.Delete(Path.Combine(f.Root, "prepared.json"));
        Assert.Equal(cache.Id, PlayableCache.Open(f.Root, "", Fixture.Package).Id);
    }

    [Fact]
    public void LegacyCacheWithMissingVariantsRequiresRepair()
    {
        using var f = new Fixture(); f.MakeLegacy();
        File.Delete(Path.Combine(f.Root, f.Manifest.Files[3].CachePath));
        Assert.Contains("variant", Assert.Throws<CacheException>(() => PlayableCache.Open(f.Root, "", Fixture.Package)).Message);
        Assert.False(File.Exists(Path.Combine(f.Root, "playable.json")));
    }

    [Fact]
    public void PreparationStagesVariantsWithoutPlayingAndSealsCache()
    {
        using var f = new Fixture(); f.MakeLegacy();
        foreach (var file in f.Manifest.Files.Where(x => x.OriginalPath.EndsWith(".bin")))
        {
            var target = Path.Combine(f.Source, file.OriginalPath); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(f.Root, file.CachePath), target); File.Delete(Path.Combine(f.Root, file.CachePath));
        }
        var id = JsonDocument.Parse(File.ReadAllText(Path.Combine(f.Root, "prepared.json"))).RootElement.GetProperty("Id").GetString()!;
        var result = PlayableCache.Publish(new(f.Source, f.Root, "", Fixture.Package, f.Manifest.Prepared.SourcePlanId), id, default);
        Directory.Delete(f.Source, true);
        Assert.Equal(result.Id, PlayableCache.Open(f.Root, "", Fixture.Package).Id);
    }

    private sealed class Registry(CampaignRoute[] maps) : ICampaignRegistry
    {
        public CampaignMapInput[]? Inputs; public bool Wrong;
        public CampaignMapMetadata[] Read(CampaignMapInput[] inputs, IProgress<IndexProgress> progress, CancellationToken cancellation)
        { Inputs = inputs; return inputs.Select(input=>maps.Single(x=>"__cms__/rtx/"+x.Scenario+".mapinfo"==input.SourcePath)).Select(x => new CampaignMapMetadata(x.MapId + (Wrong ? 1u : 0u), x.Mission, x.Sublevel, x.SourceModules)).ToArray(); }
        public void Dispose() { }
    }
    private sealed class Worker : IIndexWorker, IPreparationWorker, ICampaignPlayWorker
    {
        public CampaignPlayRequest? Request;
        public Task<IndexResult> RunAsync(IndexRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation) => throw new Exception("Unexpected indexing");
        public Task<PreparationResult> PrepareAsync(PrepareRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation) => throw new Exception("Unexpected preparation");
        public Task<CampaignPlayResult> PlayAsync(CampaignPlayRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation)
        { Request = request; return Task.FromResult(new CampaignPlayResult("Ready", "Solo", "No dump used.")); }
    }

    private sealed class Fixture : IDisposable
    {
        public const string Package = "fixture-package";
        private readonly string owned = Path.Combine(Path.GetTempPath(), "h5sololauncher-tests", Guid.NewGuid().ToString("N"));
        public string Root; public string Source => Path.Combine(owned, "absent-dump");
        public PlayableManifest Manifest;
        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        private void Write(string relative, byte[] bytes)
        { var path = Path.Combine(Root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes); }
        private string Save(string category, object value)
        { var bytes = JsonSerializer.SerializeToUtf8Bytes(value); var id = Hash(bytes); Write("inputs/" + category + "/" + id.ToLowerInvariant() + ".json", bytes); return id; }
        public void Seal()
        { var id = Save("playable-manifests", Manifest); Write("playable.json", JsonSerializer.SerializeToUtf8Bytes(new { Id = id })); }
        public Fixture(int available=3)
        {
            Directory.CreateDirectory(owned); Root = CacheFolders.Select(owned, Source, "").Root;
            CampaignFile FileEntry(string original, string name)
            {
                var bytes = Encoding.UTF8.GetBytes("fixture payload " + name); var sha = Hash(bytes);
                var relative = "game/" + (name.EndsWith(".bin") ? "variants/" : "files/") + sha.ToLowerInvariant() + "/" + name;
                Write(relative, bytes); return new(original, relative, bytes.Length, sha);
            }
            var files = new[] { FileEntry("deploy/any/levels/test.module", "test.module"), FileEntry("sound/win/h5solo-campaign.pck", "audio.pck"),
                FileEntry("deploy/pc/levels/h5solo-menu.module", "menu.module"), FileEntry("__cms__/campaign/campaignnormal.bin", "campaignnormal.bin"), FileEntry("__cms__/campaign/campaignarcade.bin", "campaignarcade.bin") };
            var movie = FileEntry("bink/cin_010_halsey_60.bk2", "cin_010_halsey_60.bk2");
            string Config(string category)
            { var bytes = Encoding.UTF8.GetBytes("fixture config " + category); var id = Hash(bytes); Write("inputs/" + category + "/" + id.ToLowerInvariant() + ".bin", bytes); return id; }
            var unused = new string('A', 64);
            var prepared = new PreparedCampaign(1, PreparedCampaignStore.Rules, unused, Package, unused, unused, unused, unused, unused, unused, unused, unused, unused,
                Config("menu-config"), Config("ui-residency"), unused, Config("completion-config"), Config("display-config"));
            var modules = Encoding.ASCII.GetBytes("?a\0?b\0?c\0?d\0<0>1\0");
            var maps = Enumerable.Range(0, 19).Select(i =>
            {
                var bytes = Encoding.UTF8.GetBytes("metadata " + i); var sha = Hash(bytes); Write("game/metadata/" + sha.ToLowerInvariant() + ".mapinfo", bytes);
                var scenarios=H5SoloLauncher.Core.Planning.ContentBundles.Get("full-campaign").Scenarios;
                return new CampaignRoute((uint)i, i-scenarios.Take(i).Count(x=>x.Contains("/cinematics/")), i>0 && scenarios[i-1].Contains("/cinematics/") ? 1 : 0, (uint)(i + 1), i<scenarios.Length?scenarios[i]:"levels/mission"+i, sha, modules, modules, [files[0].OriginalPath], i < available);
            }).ToArray();
            Manifest = new(1, "English(US)", prepared, maps, files, movie); Seal();
        }
        public void MakeLegacy()
        {
            var p = Manifest.Prepared; var files = Manifest.Files;
            ModuleWriteResult Proof(CampaignFile f) => new(f.Bytes, f.Sha256, f.Sha256, 1, 1, 1);
            var modules = Save("game-manifests", new AssembledCampaign(1, ModuleAssembly.Rules, p.EffectivePlanId, p.AssetsId, p.ShadersId, Package,
                [new(files[0].OriginalPath, files[0].CachePath, p.AssetsId, Proof(files[0]), [])], []));
            var audio = Save("audio-manifests", new PreparedAudio(1, AudioPreparation.Rules, p.EffectivePlanId, p.AssetsId, Package, Manifest.Language, files[1].CachePath, files[1].Bytes, files[1].Sha256, [], [], [], [], []));
            var artwork = Save("menu-artwork-manifests", new MenuArtworkManifest(1, MenuArtwork.Rules, p.EffectivePlanId, p.SourceFingerprint, files[2].OriginalPath, files[2].CachePath, Proof(files[2]), []));
            var catalogue = Save("catalogue-manifests", new CampaignCatalogue(1, CampaignMetadata.Rules, p.SourceFingerprint, Package, modules, Manifest.Maps));
            var runtime = PlayableCache.Resolve(new("unused", Root, Manifest));
            p = p with { ModulesId = modules, AudioId = audio, ArtworkId = artwork, CatalogueId = catalogue, MovieConfigId = runtime.Prepared.MovieConfigId };
            var id = Save("prepared-manifests", p); Write("prepared.json", JsonSerializer.SerializeToUtf8Bytes(new { Id = id }));
            File.Delete(Path.Combine(Root, "playable.json"));
        }
        public void Dispose() => Directory.Delete(owned, true);
    }
}
