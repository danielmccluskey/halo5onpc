using System.Text;
using System.Text.Json;
using H5SoloLauncher.Core.Conversion;
using H5SoloLauncher.Core.Preparation;
using H5SoloLauncher.Core.Planning;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Core.Runtime;

// Playback needs only these relative files and configurations. Source plans and
// conversion checkpoints are deliberately not part of the read path.
public sealed record PlayableManifest(int Format, string Language, PreparedCampaign Prepared,
    CampaignRoute[] Maps, CampaignFile[] Files, CampaignFile Movie);
public sealed record PlayableCacheSelection(string Id, string Root, PlayableManifest Manifest);
public sealed record PlayableRuntime(PreparedCampaign Prepared, string ContentPath, string ContentId);

public static class PlayableCache
{
    private sealed record Pointer(string Id);
    private static CacheException Incomplete(string detail) => new("PLAYABLE_CACHE_INCOMPLETE",
        "This cache is incomplete or damaged. Choose the campaign dump to build or repair it, or select a complete cache. " + detail);

    public static PlayableCacheSelection Open(string selected, string forge, string package)
    {
        var cache = CacheFolders.OpenExisting(selected, forge);
        using var lease = new FileStream(SafePaths.Child(cache.Root, "index.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(SafePaths.Child(cache.Root, "playable.json")))
        {
            var pointer = InputFiles.Read<Pointer>(cache.Root, "playable.json", 4096);
            return Read(cache.Root, forge, package, pointer.Id);
        }
        try { return Import(cache.Root, forge, package); }
        catch (CacheException e) when (e.Code == "INPUT_CACHE_DAMAGED") { throw Incomplete(e.Message); }
        catch (Exception e) when (e is IOException or JsonException or ArgumentException)
        { throw Incomplete(e.Message); }
    }

    public static PlayableCacheSelection Read(string root, string forge, string package, string id)
    {
        root = CacheFolders.OpenExisting(root, forge).Root;
        try
        {
            var manifest = InputFiles.Read<PlayableManifest>(root, InputFiles.PathFor("playable-manifests", id), 4 * 1024 * 1024, id);
            Validate(root, package, manifest);
            return new(id, root, manifest);
        }
        catch (CacheException e) when (e.Code == "INPUT_CACHE_DAMAGED") { throw Incomplete(e.Message); }
        catch (Exception e) when (e is IOException or JsonException or ArgumentException) { throw Incomplete(e.Message); }
    }

    // Used at the end of generation. Variant files must be staged before sealing,
    // even when the user chooses Prepare cache only and never launches a mission.
    public static PlayableCacheSelection Publish(InputRequest input, string preparedId, CancellationToken cancellation)
    {
        var cache = CacheFolders.Open(input.CacheRoot, input.SourceRoot, input.ForgeRoot);
        using var lease = new FileStream(SafePaths.Child(cache.Root, "index.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var variants = new List<CampaignFile>();
        foreach (var name in new[] { "campaignnormal.bin", "campaignarcade.bin" })
        {
            cancellation.ThrowIfCancellationRequested();
            using var source = File.OpenRead(SafePaths.Child(input.SourceRoot, "__cms__/campaign/" + name));
            if (source.Length is <= 0 or > 1024 * 1024) throw Incomplete("Unsupported campaign variant: " + name);
            var bytes = new byte[source.Length]; source.ReadExactly(bytes);
            var sha = InputFiles.Hash(bytes); var relative = "game/variants/" + sha.ToLowerInvariant() + "/" + name;
            var destination=SafePaths.Child(cache.Root,relative);
            // Forge can retain read handles on an older cache's identical
            // variants. Expanding the mission bundle need not replace them.
            if(!File.Exists(destination) || !File.ReadAllBytes(destination).AsSpan().SequenceEqual(bytes))
                InputFiles.Write(cache.Root, relative, bytes);
            variants.Add(new("__cms__/campaign/" + name, relative, bytes.Length, sha));
        }
        return Import(cache.Root, input.ForgeRoot, input.PackageFullName, preparedId, variants.ToArray());
    }

    private static PlayableCacheSelection Import(string root, string forge, string package, string? preparedId = null, CampaignFile[]? variants = null)
    {
        preparedId ??= InputFiles.Read<Pointer>(root, "prepared.json", 4096).Id;
        var prepared = InputFiles.Read<PreparedCampaign>(root, InputFiles.PathFor("prepared-manifests", preparedId), 128 * 1024, preparedId);
        var catalogue = InputFiles.Read<CampaignCatalogue>(root, InputFiles.PathFor("catalogue-manifests", prepared.CatalogueId), 1024 * 1024, prepared.CatalogueId);
        var modules = InputFiles.Read<AssembledCampaign>(root, InputFiles.PathFor("game-manifests", prepared.ModulesId), 4 * 1024 * 1024, prepared.ModulesId);
        var audio = InputFiles.Read<PreparedAudio>(root, InputFiles.PathFor("audio-manifests", prepared.AudioId), 32 * 1024 * 1024, prepared.AudioId);
        var artwork = InputFiles.Read<MenuArtworkManifest>(root, InputFiles.PathFor("menu-artwork-manifests", prepared.ArtworkId), 1024 * 1024, prepared.ArtworkId);
        if (catalogue.Format != 1 || catalogue.Rules != CampaignMetadata.Rules || catalogue.SourceFingerprint != prepared.SourceFingerprint || catalogue.PackageFullName != package || catalogue.ModulesId != prepared.ModulesId ||
            modules.Format != 1 || modules.Rules != ModuleAssembly.Rules || modules.PackageFullName != package || modules.EffectivePlanId != prepared.EffectivePlanId || modules.AssetsId != prepared.AssetsId || modules.ShadersId != prepared.ShadersId ||
            audio.Format != 1 || audio.Rules != AudioPreparation.Rules || audio.PackageFullName != package || audio.EffectivePlanId != prepared.EffectivePlanId || audio.AssetsId != prepared.AssetsId ||
            artwork.Format != 1 || artwork.Rules != MenuArtwork.Rules || artwork.SourceFingerprint != prepared.SourceFingerprint || artwork.EffectivePlanId != prepared.EffectivePlanId)
            throw Incomplete("The saved preparation manifests do not agree.");
        var files = modules.Modules.Select(x => new CampaignFile(x.OriginalPath, x.RelativePath, x.Verified.Bytes, x.Verified.Sha256)).ToList();
        files.Add(new("sound/win/h5solo-campaign.pck", audio.RelativePath, audio.Bytes, audio.Sha256));
        files.Add(new(artwork.OriginalPath, artwork.RelativePath, artwork.Verified.Bytes, artwork.Verified.Sha256));
        files.AddRange(variants ?? FindVariants(root));
        var movieBytes = ConfigBytes(root, "movie-config", prepared.MovieConfigId, 16384);
        using var movieStream = new MemoryStream(movieBytes); using var reader = new BinaryReader(movieStream, new UTF8Encoding(false, true));
        string Text() { var length = reader.ReadInt32(); if (length <= 0 || length > movieStream.Length - movieStream.Position) throw Incomplete("Invalid movie configuration."); return new UTF8Encoding(false, true).GetString(reader.ReadBytes(length)); }
        if (reader.ReadUInt32() != 0x564d3548 || reader.ReadInt32() != 1 || Text() != package) throw Incomplete("Unsupported movie configuration.");
        _ = Text(); // Previous machine's cache root is intentionally discarded.
        var path = Text(); var size = reader.ReadInt64();
        if (reader.ReadInt32() != 32) throw Incomplete("Invalid movie digest.");
        var sha = Convert.ToHexString(reader.ReadBytes(32));
        if (movieStream.Position != movieStream.Length) throw Incomplete("Invalid movie configuration length.");
        var manifest = new PlayableManifest(1, audio.Language, prepared, catalogue.Maps, files.ToArray(), new("bink/cin_010_halsey_60.bk2", path, size, sha));
        Validate(root, package, manifest);
        var id = InputFiles.Save(root, "playable-manifests", manifest);
        InputFiles.Write(root, "playable.json", JsonSerializer.SerializeToUtf8Bytes(new Pointer(id)));
        return new(id, root, manifest);
    }

    private static CampaignFile[] FindVariants(string root)
    {
        var result = new List<CampaignFile>();
        var folder = SafePaths.Child(root, "game/variants");
        foreach (var name in new[] { "campaignnormal.bin", "campaignarcade.bin" })
        {
            var candidates = new List<CampaignFile>();
            var directories = Directory.EnumerateDirectories(folder).Take(1025).ToArray();
            if (directories.Length > 1024) throw Incomplete("Too many legacy variant candidates. Rebuild to select the correct variants.");
            foreach (var directory in directories)
            {
                var digest = Path.GetFileName(directory);
                if (digest.Length != 64 || digest.Any(c => !Uri.IsHexDigit(c))) continue;
                var relative = "game/variants/" + digest + "/" + name; var path = SafePaths.Child(root, relative);
                if (!File.Exists(path)) continue;
                using var stream = File.OpenRead(path);
                if (stream.Length is > 0 and <= 1024 * 1024 && InputFiles.Digest(stream, default).Equals(digest, StringComparison.OrdinalIgnoreCase))
                    candidates.Add(new("__cms__/campaign/" + name, relative, stream.Length, digest.ToUpperInvariant()));
            }
            if (candidates.Count != 1) throw Incomplete("Missing or ambiguous cached variant: " + name);
            result.Add(candidates[0]);
        }
        return result.ToArray();
    }

    private static byte[] ConfigBytes(string root, string category, string id, int limit)
    {
        if (id is null || id.Length != 64 || id.Any(c => !Uri.IsHexDigit(c))) throw Incomplete("Invalid configuration ID: " + category);
        using var stream = File.OpenRead(PreparedCampaignStore.ConfigPath(root, category, id));
        if (stream.Length < 8 || stream.Length >= limit || InputFiles.Digest(stream, default) != id) throw Incomplete("Required configuration: " + category);
        stream.Position = 0; var bytes = new byte[stream.Length]; stream.ReadExactly(bytes); return bytes;
    }

    private static void Validate(string root, string package, PlayableManifest manifest)
    {
        if (manifest.Prepared is null || manifest.Maps is null || manifest.Files is null || manifest.Movie is null || manifest.Maps.Any(x => x is null) || manifest.Files.Any(x => x is null))
            throw Incomplete("The playback manifest has missing fields.");
        var p = manifest.Prepared;
        if (manifest.Format != 1 || p.Format != 1 || p.Rules != PreparedCampaignStore.Rules || manifest.Language != "English(US)")
            throw new CacheException("PLAYABLE_CACHE_UNSUPPORTED", "This playable cache format or language needs a different launcher version.");
        if (p.PackageFullName != package) throw new CacheException("PLAYABLE_FORGE_MISMATCH", "This cache was built for " + p.PackageFullName + ". Install the matching Forge version or choose the dump to rebuild it.");
        var available=manifest.Maps.Where(x=>x.Available).Select(x=>x.Scenario).ToArray();
        if (manifest.Maps.Length != 19 || manifest.Maps.Select(x => x.MapId).Distinct().Count() != 19 ||
            manifest.Maps.Select(x=>x.Scenario).Distinct(StringComparer.Ordinal).Count()!=19 ||
            !ContentBundles.All.Any(bundle=>available.Length==bundle.Scenarios.Length && available.ToHashSet(StringComparer.Ordinal).SetEquals(bundle.Scenarios)))
            throw Incomplete("The mission catalogue is incomplete.");
        foreach (var (category, id, limit) in new[] { ("menu-config", p.MenuConfigId, 16 * 1024 * 1024), ("ui-residency", p.UiConfigId, 512 * 1024), ("completion-config", p.CompletionConfigId, 4 * 1024 * 1024), ("display-config", p.DisplayConfigId, 4 * 1024 * 1024) })
            ConfigBytes(root, category, id, limit);
        foreach (var file in manifest.Files.Append(manifest.Movie))
        {
            Relative(file.OriginalPath); Relative(file.CachePath);
            if (!file.CachePath.StartsWith("game/", StringComparison.Ordinal)) throw Incomplete("Runtime files must be inside the cache game folder.");
            SafePaths.Child(root, file.OriginalPath);
            if (file.Bytes <= 0 || file.Sha256 is null || file.Sha256.Length != 64 || file.Sha256.Any(c => !Uri.IsHexDigit(c))) throw Incomplete("Invalid file entry: " + file.CachePath);
            var info = new FileInfo(SafePaths.Child(root, file.CachePath));
            if (!info.Exists || info.Length != file.Bytes) throw Incomplete("Missing or wrong-sized file: " + file.CachePath);
        }
        foreach (var map in manifest.Maps)
        {
            Relative(map.Scenario);
            if (map.MetadataSha256 is null || map.MetadataSha256.Length != 64 || map.MetadataSha256.Any(c => !Uri.IsHexDigit(c)) || map.SourceModules is null || map.TargetModules is null || map.ModulePaths is null)
                throw Incomplete("Invalid mission metadata entry.");
            if (map.Available && map.ModulePaths.Any(path => !manifest.Files.Any(file => file.OriginalPath == path))) throw Incomplete("A playable mission is missing required module entries.");
            CampaignMetadata.ModuleList(map.SourceModules); CampaignMetadata.ModuleList(map.TargetModules);
            var path = Metadata(map).CachePath;
            using var stream = File.OpenRead(SafePaths.Child(root, path));
            if (stream.Length is <= 0 or > 1024 * 1024 || InputFiles.Digest(stream, default) != map.MetadataSha256) throw Incomplete("Mission metadata changed: " + path);
        }
        foreach (var required in new[] { "__cms__/campaign/campaignnormal.bin", "__cms__/campaign/campaignarcade.bin", "sound/win/h5solo-campaign.pck", "deploy/pc/levels/h5solo-menu.module" })
            if (!manifest.Files.Any(x => x.OriginalPath == required)) throw Incomplete("Missing runtime entry: " + required);
        _ = CampaignContent.Encode(new(package, root, manifest.Files, manifest.Maps.Where(x => x.Available).ToArray()));
    }

    private static void Relative(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains(':') || path.Contains('\\') || path.Split('/').Any(x => x is "" or "." or ".."))
            throw Incomplete("A playback path is not a portable relative path.");
    }

    private static CampaignMapInput Metadata(CampaignRoute map) => new("__cms__/rtx/" + map.Scenario + ".mapinfo", "game/metadata/" + map.MetadataSha256.ToLowerInvariant() + ".mapinfo", map.MetadataSha256);
    public static void Register(PlayableCacheSelection cache, ICampaignRegistry registry, IProgress<IndexProgress> progress, CancellationToken cancellation)
    {
        // The native importer retains its input order for the process lifetime.
        // Match CampaignMetadata.Stage so Prepare -> Play can reuse that import.
        var maps = cache.Manifest.Maps.OrderBy(x=>Metadata(x).SourcePath,StringComparer.Ordinal).ToArray();
        var actual = registry.Read(maps.Select(Metadata).ToArray(), progress, cancellation);
        if (actual.Length != maps.Length || actual.Where((x, i) => x.MapId != maps[i].MapId || x.Mission != maps[i].Mission || x.Sublevel != maps[i].Sublevel || !x.Modules.AsSpan().SequenceEqual(maps[i].SourceModules)).Any())
            throw new CacheException("CAMPAIGN_CATALOGUE_CHANGED", "Forge returned different campaign metadata. Restart Forge; if this persists, rebuild the cache from the dump.");
    }

    public static PlayableRuntime Resolve(PlayableCacheSelection cache)
    {
        var m = cache.Manifest; var movie = m.Movie;
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        void Text(string value) { var bytes = Encoding.UTF8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
        writer.Write(0x564d3548u); writer.Write(1); Text(m.Prepared.PackageFullName); Text(cache.Root); Text(movie.CachePath);
        writer.Write(movie.Bytes); writer.Write(32); writer.Write(Convert.FromHexString(movie.Sha256)); writer.Flush();
        var bytes = stream.ToArray(); var movieId = InputFiles.Hash(bytes);
        InputFiles.Write(cache.Root, InputFiles.PathFor("movie-config", movieId, ".bin"), bytes);
        var content = CampaignContent.Encode(new(m.Prepared.PackageFullName, cache.Root, m.Files, m.Maps.Where(x => x.Available).ToArray()));
        var id = InputFiles.Hash(content); var relative = InputFiles.PathFor("runtime-config", id, ".bin"); InputFiles.Write(cache.Root, relative, content);
        return new(m.Prepared with { MovieConfigId = movieId }, SafePaths.Child(cache.Root, relative), id);
    }
}
