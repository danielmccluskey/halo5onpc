using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Forge;
using H5SoloLauncher.Core.Planning;
using H5SoloLauncher.Core.Preparation;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Core.Conversion;

public sealed record AudioPreparationRequest(ConversionAuditRequest Conversion, string AssetsId);
public sealed record AudioPackageProof(string Path, long Length, string TableSha256, long Modified);
public sealed record PreparedAudioEntry(string Kind, string Id, long Language, string Source, long SourceOffset, int SourceBytes, string SourceSha256,
    long Offset, int Bytes, string Sha256, int PreservedXma, int PreservedConvolution = 0);
public sealed record PreparedAudio(int Format, string Rules, string EffectivePlanId, string AssetsId, string PackageFullName, string Language,
    string RelativePath, long Bytes, string Sha256, uint[] RequiredBanks, uint[] NativeBanks, AudioPackageProof[] NativePackages, AudioPackageProof[] SourcePackages, PreparedAudioEntry[] Entries, SourceAudioGap[]? SourceMissingBanks = null);
public sealed record AudioPreparationResult(string State, string? ManifestId = null, int Banks = 0, int Media = 0, long Bytes = 0, bool Reused = false,
    int PreservedXma = 0, string? Code = null, string? Message = null, string? Details = null, int PreservedConvolution = 0);

public static class AudioPreparation
{
    public const string Rules = "campaign-audio-1";
    public static AudioPreparationResult Run(AudioPreparationRequest request, Func<IForgeAudioReader> readerFactory, IProgress<IndexProgress> progress, CancellationToken cancellation)
    {
        string? temporary = null, current = null;
        try
        {
            var input = request.Conversion.Inputs; var source = SourceDiscovery.Describe(input.SourceRoot); var cache = CacheFolders.Open(input.CacheRoot, source.Root, input.ForgeRoot);
            using var lease = new FileStream(SafePaths.Child(cache.Root, "index.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var catalog = new CatalogSnapshot(cache, source); var sourcePlan = BuildPlanStore.ReadVerified(cache, catalog, input.PlanId);
            var plan = InputFiles.Read<EffectivePlan>(cache.Root, InputFiles.PathFor("effective-plans", request.Conversion.EffectivePlanId), 128L * 1024 * 1024, request.Conversion.EffectivePlanId);
            var assets = InputFiles.Read<ConvertedAssetManifest>(cache.Root, InputFiles.PathFor("asset-manifests", request.AssetsId), 8 * 1024 * 1024, request.AssetsId);
            if (plan.SourcePlanId != input.PlanId || plan.InputFingerprint != catalog.Fingerprint || plan.Rules != EffectivePlanBuilder.Rules ||
                assets.Format != 1 || assets.Rules != ConvertedAssets.Rules || assets.EffectivePlanId != request.Conversion.EffectivePlanId) throw InputFiles.Damaged();
            var required = RequiredBanks(cache.Root, assets, cancellation);
            using var reader = readerFactory(); List<AudioPackageProof> nativeProof = [], sourceProof = [];
            Dictionary<(string Kind, string Id, long Language), (string File, AudioEntry Entry)> candidates = [];
            HashSet<(string Kind, string Id, long Language)> native = []; HashSet<uint> nativeBanks = []; byte[]? languages = null;
            var files = catalog.Files.ToDictionary(x => x.Path);
            foreach (var pair in new[] { (Language: "SFX", Package: "soundbank"), (Language: "SFX", Package: "soundstream"), (Language: sourcePlan.Language, Package: "soundvoice") })
            {
                cancellation.ThrowIfCancellationRequested(); var relative = $"sound/win/{pair.Language}/{pair.Package}.pck"; current = relative;
                progress.Report(new("Checking installed audio tables", nativeProof.Count, 3, relative));
                var first = reader.ReadAudio(relative, 0, 28, cancellation); var length = FileMetadata.AudioTableLength(first.Bytes, first.FileLength); var tables = new byte[length];
                for (var offset = 0; offset < length;)
                {
                    var count = Math.Min(1024 * 1024, length - offset); var read = reader.ReadAudio(relative, offset, count, cancellation);
                    if (read.FileLength != first.FileLength || read.LastWriteFileTime != first.LastWriteFileTime || read.Bytes.Length != count) throw Changed();
                    read.Bytes.CopyTo(tables, offset); offset += count;
                }
                var metadata = FileMetadata.FromAudioTables(tables, first.FileLength, first.LastWriteFileTime);
                var proof = new AudioPackageProof(relative, first.FileLength, metadata.Digest, first.LastWriteFileTime); nativeProof.Add(proof);
                foreach (var entry in metadata.AudioEntries())
                { native.Add((entry.Kind, entry.Id, entry.Language)); if (entry.Kind == "Bank") nativeBanks.Add(uint.Parse(entry.Id, NumberStyles.HexNumber)); }
                if (pair.Package == "soundvoice") languages = tables.AsSpan(28, checked((int)U(tables, 12))).ToArray();
                // Keep the collected metadata hash-addressed so diagnostics can reproduce ID comparisons.
                InputFiles.Write(cache.Root, InputFiles.PathFor("native-audio-tables", proof.TableSha256, ".bin"), tables);
                var original = $"sound/Durango/{pair.Language}/{pair.Package}.pck"; current = original;
                if (!sourcePlan.AudioPackages.Contains(original) || !files.TryGetValue(original, out var indexed)) throw new CacheException("AUDIO_SOURCE_MISSING", $"The selected dump has no indexed {original}.");
                var sourceTables = FileMetadata.Read(SafePaths.Child(source.Root, original), "Audio", cancellation);
                if (sourceTables.Digest != indexed.Digest || sourceTables.FileLength != indexed.Length) throw Changed();
                sourceProof.Add(new(original, sourceTables.FileLength, sourceTables.Digest, sourceTables.Ticks));
                foreach (var entry in sourceTables.AudioEntries())
                    if (!candidates.TryAdd((entry.Kind, entry.Id, entry.Language), (original, entry))) throw Invalid("Source audio packages contain competing payload identities.");
            }
            var missingBanks = required.Except(nativeBanks).ToHashSet();
            var availableBanks = candidates.Values.Where(x => x.Entry.Kind == "Bank").Select(x => uint.Parse(x.Entry.Id, NumberStyles.HexNumber)).ToHashSet();
            var sourceGaps=SourceAudioGaps.Resolve(source.PackageVersion,missingBanks.Except(availableBanks).ToArray(),
                assets.BatchIds.SelectMany(id=>InputFiles.Read<ConvertedAssetBatch>(cache.Root,InputFiles.PathFor("asset-batches",id),32*1024*1024,id).Assets));
            if(sourceGaps.Length>0)progress.Report(new("Recording original source audio gaps",sourceGaps.Length,sourceGaps.Length,string.Join(", ",sourceGaps.Select(x=>x.Name))));
            var selected = candidates.Where(x => !native.Contains(x.Key) && (x.Key.Kind == "Media" || x.Key.Kind == "Bank" && missingBanks.Contains(uint.Parse(x.Key.Id, NumberStyles.HexNumber))))
                .Select(x => x.Value).OrderBy(x => x.Entry.Kind, StringComparer.Ordinal).ThenBy(x => x.Entry.Id, StringComparer.Ordinal).ThenBy(x => x.Entry.Language).ToArray();
            if (selected.Any(x => x.Entry.Length is < 12 or > 64 * 1024 * 1024)) throw Invalid("An audio payload exceeds the supported size.");
            var key = InputFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(new { Rules, Converter = AudioConverter.Rules, request.Conversion.EffectivePlanId, request.AssetsId, input.PackageFullName, sourcePlan.Language,
                Required = required, Native = nativeProof, Source = sourceProof, SourceGaps=sourceGaps, GapRules=SourceAudioGaps.Rules }));
            var relativeOutput = "game/audio/" + key.ToLowerInvariant() + "/campaign.pck"; var destination = SafePaths.Child(cache.Root, relativeOutput);
            var checkpoint = InputFiles.PathFor("audio-checkpoints", key);
            if (File.Exists(destination) && File.Exists(SafePaths.Child(cache.Root, checkpoint)))
            {
                try
                {
                    var old = InputFiles.Read<PreparedAudio>(cache.Root, checkpoint, 32 * 1024 * 1024);
                    using var stream = File.OpenRead(destination);
                    if (old.Format == 1 && old.Rules == Rules && old.AssetsId == request.AssetsId && old.EffectivePlanId == request.Conversion.EffectivePlanId &&
                        old.PackageFullName == input.PackageFullName && old.Language == sourcePlan.Language && old.RelativePath == relativeOutput &&
                        old.RequiredBanks.SequenceEqual(required) && old.NativePackages.SequenceEqual(nativeProof) && old.SourcePackages.SequenceEqual(sourceProof) &&
                        old.Bytes == stream.Length && InputFiles.Digest(stream, cancellation) == old.Sha256)
                        return Complete(old, InputFiles.Save(cache.Root, "audio-manifests", old), true);
                }
                catch (Exception e) when (e is IOException or JsonException or CacheException) { }
            }
            var banks = selected.Count(x => x.Entry.Kind == "Bank"); var media = selected.Length - banks;
            var headerLength = checked(28 + languages!.Length + 12 + selected.Length * 20);
            var estimate = selected.Sum(x => x.Entry.Length + 4096) + headerLength + 64L * 1024 * 1024;
            if (new DriveInfo(Path.GetPathRoot(cache.Root)!).AvailableFreeSpace < estimate + 512L * 1024 * 1024) throw new CacheException("AUDIO_CACHE_SPACE", "The cache drive needs more free space to write campaign audio. Free space and resume.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); Directory.CreateDirectory(SafePaths.Child(cache.Root, "inputs/work"));
            temporary = SafePaths.Child(cache.Root, "inputs/work/" + Guid.NewGuid().ToString("N") + ".tmp");
            List<PreparedAudioEntry> written = []; var converter = new AudioConverter();
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1024 * 1024))
            {
                output.SetLength(Align(headerLength)); output.Position = output.Length;
                foreach (var (file, entry) in selected)
                {
                    current = file + "#" + entry.Id; cancellation.ThrowIfCancellationRequested();
                    progress.Report(new("Preparing campaign audio", written.Count, selected.Length, current));
                    var original = ReadSource(source.Root, file, entry, sourceProof.Single(x => x.Path == file));
                    var converted = entry.Kind == "Bank" ? converter.Bank(original, uint.Parse(entry.Id, NumberStyles.HexNumber)) : AudioConverter.Wem(original);
                    var offset = Align(output.Position); output.SetLength(offset); output.Position = offset; output.Write(converted.Bytes);
                    written.Add(new(entry.Kind, entry.Id, entry.Language, file, entry.Offset, original.Length, InputFiles.Hash(original), offset, converted.Bytes.Length, InputFiles.Hash(converted.Bytes), converted.PreservedXma, converted.PreservedConvolution));
                }
                var header = new byte[headerLength]; "AKPK"u8.CopyTo(header); W(header, 4, (uint)headerLength - 8); W(header, 8, 1); W(header, 12, (uint)languages.Length);
                W(header, 16, (uint)(4 + 20 * banks)); W(header, 20, (uint)(4 + 20 * media)); W(header, 24, 4); languages.CopyTo(header, 28);
                var cursor = 28 + languages.Length;
                foreach (var kind in new[] { "Bank", "Media", "External" })
                {
                    var rows = written.Where(x => x.Kind == kind).ToArray(); W(header, cursor, (uint)rows.Length); cursor += 4;
                    foreach (var row in rows)
                    {
                        W(header, cursor, uint.Parse(row.Id, NumberStyles.HexNumber)); W(header, cursor + 4, 4096); W(header, cursor + 8, checked((uint)row.Bytes));
                        W(header, cursor + 12, checked((uint)(row.Offset / 4096))); W(header, cursor + 16, checked((uint)row.Language)); cursor += 20;
                    }
                }
                if (cursor != header.Length) throw Invalid("Audio package table sizes do not agree.");
                output.Position = 0; output.Write(header); output.Flush(true);
            }
            var parsed = FileMetadata.Read(temporary, "Audio", cancellation).AudioEntries().ToArray();
            if (parsed.Length != written.Count || !parsed.Zip(written).All(x => x.First.Kind == x.Second.Kind && x.First.Id == x.Second.Id && x.First.Language == x.Second.Language && x.First.Offset == x.Second.Offset && x.First.Length == x.Second.Bytes)) throw Invalid("Audio package failed table read-back.");
            string digest; long bytes;
            using (var verify = File.OpenRead(temporary))
            {
                foreach (var row in written)
                {
                    cancellation.ThrowIfCancellationRequested(); var buffer = new byte[row.Bytes]; verify.Position = row.Offset; verify.ReadExactly(buffer);
                    if (InputFiles.Hash(buffer) != row.Sha256) throw Invalid("An audio payload failed read-back verification.");
                }
                verify.Position = 0; digest = InputFiles.Digest(verify, cancellation); bytes = verify.Length;
            }
            foreach (var proof in sourceProof)
            {
                var metadata = FileMetadata.Read(SafePaths.Child(source.Root, proof.Path), "Audio", cancellation);
                if (metadata.Digest != proof.TableSha256 || metadata.FileLength != proof.Length || metadata.Ticks != proof.Modified) throw Changed();
            }
            foreach (var proof in nativeProof)
            {
                var read = reader.ReadAudio(proof.Path, 0, 28, cancellation);
                if (read.FileLength != proof.Length || read.LastWriteFileTime != proof.Modified) throw Changed();
            }
            var manifest = new PreparedAudio(1, Rules, request.Conversion.EffectivePlanId, request.AssetsId, input.PackageFullName, sourcePlan.Language,
                  relativeOutput, bytes, digest, required, required.Intersect(nativeBanks).Order().ToArray(), nativeProof.ToArray(), sourceProof.ToArray(), written.ToArray(), sourceGaps);
            cancellation.ThrowIfCancellationRequested(); File.Move(temporary, destination, true); temporary = null;
            InputFiles.Write(cache.Root, checkpoint, JsonSerializer.SerializeToUtf8Bytes(manifest));
            return Complete(manifest, InputFiles.Save(cache.Root, "audio-manifests", manifest), false);
        }
        catch (OperationCanceledException) { return new("Paused", Message: "Audio preparation paused. Other completed cache stages were kept."); }
        catch (Exception e) { return new("Failed", Code: e is CacheException known ? known.Code : "AUDIO_PREPARATION_FAILED", Message: e.Message, Details: $"Audio: {current}\n{e}"); }
        finally { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
    }
    private static AudioPreparationResult Complete(PreparedAudio value, string id, bool reused) => new("Prepared", id, value.Entries.Count(x => x.Kind == "Bank"), value.Entries.Count(x => x.Kind == "Media"), value.Bytes, reused,
        value.Entries.Sum(x => x.PreservedXma), Message: "Campaign audio built and verified.", PreservedConvolution: value.Entries.Sum(x=>x.PreservedConvolution));
    private static byte[] ReadSource(string root, string file, AudioEntry entry, AudioPackageProof proof)
    {
        var path = SafePaths.Child(root, file); using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != proof.Length || File.GetLastWriteTimeUtc(path).Ticks != proof.Modified) throw Changed();
        var bytes = new byte[checked((int)entry.Length)]; stream.Position = entry.Offset; stream.ReadExactly(bytes); return bytes;
    }
    public static uint[] RequiredBanks(string root, ConvertedAssetManifest assets, CancellationToken cancellation)
    {
        HashSet<uint> required = [];
        foreach (var id in assets.BatchIds)
        {
            var batch = InputFiles.Read<ConvertedAssetBatch>(root, InputFiles.PathFor("asset-batches", id), 32 * 1024 * 1024, id);
            if (batch.Rules != ConvertedAssets.Rules) throw InputFiles.Damaged();
            foreach (var asset in batch.Assets.Where(x => x.Entry.AsSpan(64, 4).SequenceEqual("knbs"u8)))
            {
                var tag = new TagDocument(ConvertedAssets.Read(root, batch, asset, cancellation));
                if (tag.Blocks.Length != 2 || tag.Blocks[0].Size != 64 || tag.Blocks[1].Size == 0 || tag.Blocks[1].Size % 4 != 0) throw Invalid("A sound-bank tag has an unsupported layout.");
                var name = Path.GetFileNameWithoutExtension(asset.Name.Replace('\\', '/')).ToLowerInvariant(); uint hash = 2166136261;
                foreach (var c in Encoding.UTF8.GetBytes(name)) hash = unchecked(hash * 16777619) ^ c;
                // This shipped Before the Storm tag retains a bank ID that differs from
                // its filename hash. The referenced 6de0d5f8 bank exists in the source
                // SFX package. Accept only the reviewed payload; do not rename its ID.
                if (name == "sb_120_mus_campaign_campsite_return" &&
                    asset.Sha256 == "0920B2900D5D5D43B5805FDEA49482653A1072BBEC8D12206672492440D8736C")
                    hash = 0x6de0d5f8;
                if (U(tag.Block(0), 56) != hash) throw Invalid($"Sound-bank {asset.Name} ({batch.SourceFile}, item {asset.Item}) has identity {U(tag.Block(0), 56):x8}, expected {hash:x8} from its name.");
                var found = false; var ids = tag.Block(1);
                for (var at = 0; at < ids.Length; at += 4) { var bank = U(ids, at); required.Add(bank); if (bank == hash) found = true; }
                if (!found) throw Invalid("A sound-bank tag omits its own bank identity.");
            }
        }
        return required.Order().ToArray();
    }
    private static long Align(long value) => checked((value + 4095) & ~4095L);
    private static uint U(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
    private static void W(Span<byte> bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes[at..], value);
    private static CacheException Invalid(string message) => new("AUDIO_PREPARATION_INVALID", message);
    private static CacheException Changed() => new("AUDIO_SOURCE_CHANGED", "An audio package changed during preparation. Let updates or extraction finish, then resume.");
}
