using System.Buffers.Binary;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Forge;
using H5SoloLauncher.Core.Planning;
using H5SoloLauncher.Core.Preparation;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Core.Conversion;

public sealed record NativeStringCandidate(string File, ModuleEntry Entry, Func<byte[]> Read);
public sealed record NativeStringSelection(AssetReference Identity, string File, int Item, string Checksum, string PayloadSha256);

/// <summary>
/// Native globals retain shared UI strings across scenario transitions. Campaign
/// overlays must publish the same payload AND asset checksum, or the loader's
/// looming-scenario check halts when returning to the menu.
/// </summary>
public sealed class NativeSharedStrings
{
    private sealed record Selected(NativeStringCandidate Candidate, byte[] Bytes);
    private readonly Dictionary<AssetReference, Selected> selected = [];
    public NativeStringSelection[] Selections { get; }

    public NativeSharedStrings(IEnumerable<NativeStringCandidate> candidates)
    {
        foreach (var group in candidates.Where(c => ForgePaths.IsGlobal(c.File) && IsShared(c.Entry.Group, c.Entry.Name) &&
                     c.Entry.Parent == -1 && c.Entry.StoredSize > 0).GroupBy(c => Identity(c.Entry)))
        {
            if (group.Select(c => DependencyNames.Canonical(c.Entry.Name)).Distinct(StringComparer.Ordinal).Count() != 1)
                throw Invalid("Native shared string candidates have conflicting names for the same identity.");
            var winner = group.OrderBy(c => EffectivePlanBuilder.Rank(c.File, c.Entry.Index)).Last();
            if (winner.Entry.ResourceCount != 0) throw Invalid("A shared string table unexpectedly owns external resources.");
            var bytes = winner.Read();
            // No new dependency/resource routes may be introduced by this replacement.
            if (new TagDocument(bytes).Metadata.Dependencies.Length != 0)
                throw Invalid("A shared string table unexpectedly has tag dependencies.");
            selected.Add(group.Key, new(winner, (byte[])bytes.Clone()));
        }
        Selections = selected.OrderBy(x => x.Key.TagId, StringComparer.Ordinal).ThenBy(x => x.Key.AssetId, StringComparer.Ordinal)
            .Select(x => new NativeStringSelection(x.Key, x.Value.Candidate.File, x.Value.Candidate.Entry.Index,
                x.Value.Candidate.Entry.Checksum, InputFiles.Hash(x.Value.Bytes))).ToArray();
    }

    public static bool IsShared(string group, string name) => group == "unic" &&
        DependencyNames.Canonical(name).StartsWith("ui/strings/", StringComparison.Ordinal);

    public bool TryReplace(byte[] row, string name, out byte[] bytes)
    {
        bytes = [];
        if (row.Length != 88) throw Invalid("A module entry has an unsupported size.");
        var group = System.Text.Encoding.ASCII.GetString(row.AsSpan(64, 4).ToArray().Reverse().ToArray());
        if (!IsShared(group, name)) return false;
        var key = new AssetReference(group, BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(44)).ToString("x8"),
            BinaryPrimitives.ReadUInt64LittleEndian(row.AsSpan(48)).ToString("x16"));
        if (!selected.TryGetValue(key, out var value)) return false;
        if (DependencyNames.Canonical(name) != DependencyNames.Canonical(value.Candidate.Entry.Name) ||
            BinaryPrimitives.ReadInt32LittleEndian(row.AsSpan(4)) != -1 || BinaryPrimitives.ReadInt32LittleEndian(row.AsSpan(8)) != 0)
            throw Invalid("A campaign shared string entry differs in name, ownership or resource layout.");
        // Keep the campaign's table positions and identity. The checksum is copied
        // only together with the complete corresponding native tag payload.
        value.Candidate.Entry.Raw.AsSpan(56, 8).CopyTo(row.AsSpan(56, 8));
        bytes = (byte[])value.Bytes.Clone();
        return true;
    }

    public static NativeSharedStrings Load(string cacheRoot, NativeModuleManifest native, IEnumerable<EffectiveTag> tags,
        StructuralConverter converter, CancellationToken cancellation)
    {
        var wanted = tags.Where(t => IsShared(t.Identity.Group, t.Name)).Select(t => t.Identity).ToHashSet();
        List<NativeStringCandidate> candidates = [];
        foreach (var module in native.Modules.Where(m => ForgePaths.IsGlobal(m.Path)))
        {
            cancellation.ThrowIfCancellationRequested();
            var path = SafePaths.Child(cacheRoot, module.RelativePath);
            var metadata = FileMetadata.Read(path, "Module", cancellation);
            if (metadata.FileLength != module.Length || metadata.Digest != module.TableDigest ||
                ModuleChecksum.Metadata(metadata.Tables) != BinaryPrimitives.ReadUInt64LittleEndian(metadata.Header.AsSpan(48)))
                throw InputFiles.Damaged();
            for (var i = 0; i < metadata.ItemCount; i++)
            {
                var entry = metadata.Entry(i);
                if (!wanted.Contains(Identity(entry))) continue;
                candidates.Add(new(module.Path, entry, () =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    using var stream = File.OpenRead(path);
                    var payload = ModulePayloadReader.Read(stream, metadata, entry, cancellation).Bytes;
                    var check = converter.Convert("unic", new TagDocument(payload));
                    if (!check.Bytes.AsSpan().SequenceEqual(payload)) throw Invalid("A native shared string table unexpectedly requires conversion.");
                    return payload;
                }));
            }
        }
        return new(candidates);
    }

    private static AssetReference Identity(ModuleEntry entry) => new(entry.Group, entry.TagId, entry.AssetId);
    private static CacheException Invalid(string message) => new("SHARED_UI_STRINGS_INCOMPATIBLE", message);
}
