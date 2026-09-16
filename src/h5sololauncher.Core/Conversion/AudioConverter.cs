using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using H5SoloLauncher.Core.Preparation;

namespace H5SoloLauncher.Core.Conversion;

public sealed record AudioEdit(int Offset, string Before, string After, string Reason);
public sealed record PreservedAudioMedia(uint Id, int Bytes, string Sha256, string Kind);
public sealed record AudioRecipe(uint Id, string InputSha256, string OutputSha256, int InputBytes, int OutputBytes, int Objects, int HircObjects, AudioEdit[] Edits, PreservedAudioMedia[]? PreservedMedia = null);
public sealed record AudioProfiles(int Format, int SourceVersion, int NativeVersion, AudioRecipe[] Recipes);
public sealed record AudioConversion(byte[] Bytes, int Media, int PreservedXma, int PreservedConvolution = 0);

/// <summary>Versioned, fully hashed field recipes; compressed audio is never recompressed.</summary>
public sealed class AudioConverter
{
    public const string Rules = "audio-118-to-112-2";
    private readonly Dictionary<string, AudioRecipe> recipes;
    public AudioConverter()
    {
        using var stream = typeof(AudioConverter).Assembly.GetManifestResourceStream("H5SoloLauncher.Core.Conversion.audio-profiles.json")!;
        var profile = JsonSerializer.Deserialize<AudioProfiles>(stream) ?? throw Invalid("Audio compatibility rules are missing.");
        if (profile.Format != 1 || profile.SourceVersion != 118 || profile.NativeVersion != 112) throw Invalid("Audio compatibility rules have an unknown version.");
        recipes = profile.Recipes.ToDictionary(x => x.InputSha256);
    }
    public AudioConversion Bank(byte[] source, uint expectedId)
    {
        var before = InspectBank(source, 118, expectedId);
        if (!recipes.TryGetValue(InputFiles.Hash(source), out var recipe) || recipe.Id != expectedId || recipe.InputBytes != source.Length)
            throw new CacheException("AUDIO_BANK_UNSUPPORTED", $"Audio bank {expectedId} differs from the supported dump. Its data was kept; conversion needs a matching compatibility rule.");
        if (before.Objects.Length != recipe.HircObjects || recipe.OutputBytes is < 8 or > 64 * 1024 * 1024) throw Invalid("Audio recipe size or object count is inconsistent.");
        using var output = new MemoryStream(recipe.OutputBytes); var cursor = 0;
        foreach (var edit in recipe.Edits)
        {
            var old = Convert.FromHexString(edit.Before); var replacement = Convert.FromHexString(edit.After);
            if (old.Length > 5 || replacement.Length > 8 || edit.Offset < cursor || edit.Offset > source.Length - old.Length ||
                !source.AsSpan(edit.Offset, old.Length).SequenceEqual(old)) throw Invalid("An audio conversion field failed its source check.");
            output.Write(source.AsSpan(cursor, edit.Offset - cursor)); output.Write(replacement); cursor = edit.Offset + old.Length;
        }
        output.Write(source.AsSpan(cursor)); var bytes = output.ToArray();
        if (bytes.Length != recipe.OutputBytes || InputFiles.Hash(bytes) != recipe.OutputSha256) throw Invalid("Converted audio bank failed its expected checksum.");
        var after = InspectBank(bytes, 112, expectedId);
        if (!before.Objects.SequenceEqual(after.Objects)) throw Invalid("Audio conversion changed bank object identities.");
        var media = 0; var xma = 0; var convolution = 0;
        var preserved=(recipe.PreservedMedia??[]).ToDictionary(x=>x.Id);
        if (after.Index is { } index)
        {
            if (after.Data is not { } data || index.Length % 12 != 0) throw Invalid("Resident media has no valid data index.");
            for (var at = index.Offset; at < index.Offset + index.Length; at += 12)
            {
                var offset = U(bytes, at + 4); var length = U(bytes, at + 8);
                if (offset > data.Length || length > data.Length - offset) throw Invalid("Resident audio lies outside its bank.");
                var payload=bytes.AsSpan(checked(data.Offset + (int)offset), checked((int)length));
                if(preserved.TryGetValue(U(bytes,at),out var proof))
                {
                    // Convolution effects carry plugin data, not WEM audio. The
                    // recipe compiler verifies the effect's media-map reference.
                    if(proof.Kind!="WwiseConvolutionReverb" || proof.Bytes!=payload.Length || payload.Length<12 || payload[..4].SequenceEqual("RIFF"u8) ||
                        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload))!=proof.Sha256)
                        throw Invalid("A convolution media payload differs from its verified recipe.");
                    convolution++;
                }
                else if (ConvertWem(payload)) xma++;
                media++;
            }
        }
        if(convolution!=preserved.Count)throw Invalid("The bank does not contain every verified convolution payload.");
        return new(bytes, media, xma, convolution);
    }
    public static AudioConversion Wem(byte[] source)
    {
        var bytes = (byte[])source.Clone(); var xma = ConvertWem(bytes); return new(bytes, 1, xma ? 1 : 0);
    }
    private static bool ConvertWem(Span<byte> bytes)
    {
        if (bytes.Length < 12 || !bytes[..4].SequenceEqual("RIFF"u8) || !bytes.Slice(8, 4).SequenceEqual("WAVE"u8)) throw Invalid("A WEM file has no RIFF/WAVE header.");
        for (var at = 12; at <= bytes.Length - 8;)
        {
            var size = U(bytes, at + 4); var begin = at + 8;
            if (bytes.Slice(at, 4).SequenceEqual("fmt "u8))
            {
                if (size < 24 || size > bytes.Length - begin) throw Invalid("A WEM format chunk is truncated.");
                var codec = BinaryPrimitives.ReadUInt16LittleEndian(bytes[begin..]); var channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(begin + 2)..]);
                if (codec == 0x166) return true; // Existing XMA packets have a different header and remain explicitly recorded.
                if (codec is not (0xffff or 0xfffe or 2)) throw Invalid($"WEM codec {codec:x4} has no supported conversion.");
                var configuration = U(bytes, begin + 20); var mask = configuration >> 12;
                if ((configuration & 255) != channels || ((configuration >> 8) & 15) != 1 || BitOperations.PopCount(mask) != channels)
                    throw Invalid("A WEM channel layout is not the expected Xbox format.");
                BinaryPrimitives.WriteUInt32LittleEndian(bytes[(begin + 20)..], mask); return false;
            }
            if (size > bytes.Length - begin) throw Invalid("A WEM chunk before its format is truncated.");
            at = checked(begin + (int)size + ((int)size & 1));
        }
        throw Invalid("A WEM format chunk is missing.");
    }
    private sealed record Chunk(int Offset, int Length);
    private sealed record BankInfo((byte Type, uint Id)[] Objects, Chunk? Index, Chunk? Data);
    private static BankInfo InspectBank(byte[] bytes, uint version, uint id)
    {
        if (bytes.Length < 20 || !bytes.AsSpan(0, 4).SequenceEqual("BKHD"u8) || U(bytes, 8) != version || U(bytes, 12) != id) throw Invalid("A bank header has the wrong version or identity.");
        List<(byte, uint)> objects = []; Chunk? index = null, data = null; HashSet<string> seen = [];
        for (var at = 0; at < bytes.Length;)
        {
            if (at > bytes.Length - 8) throw Invalid("A bank chunk header is truncated.");
            var name = System.Text.Encoding.ASCII.GetString(bytes, at, 4); var size = U(bytes, at + 4); var start = at + 8;
            if (size > bytes.Length - start || !seen.Add(name)) throw Invalid("A bank chunk is truncated or duplicated.");
            var length = checked((int)size);
            if (name == "DIDX") index = new(start, length);
            if (name == "DATA") data = new(start, length);
            if (name == "HIRC")
            {
                if (length < 4) throw Invalid("Bank object table is truncated.");
                var count = U(bytes, start); var cursor = start + 4;
                if (count > length / 9) throw Invalid("Bank object count exceeds its chunk.");
                for (var i = 0; i < count; i++)
                {
                    if (cursor > start + length - 9) throw Invalid("Bank object header is truncated.");
                    var objectSize = U(bytes, cursor + 1);
                    if (objectSize < 4 || objectSize > start + length - cursor - 5) throw Invalid("Bank object data is truncated.");
                    objects.Add((bytes[cursor], U(bytes, cursor + 5))); cursor = checked(cursor + 5 + (int)objectSize);
                }
                if (cursor != start + length) throw Invalid("Bank object lengths do not fill their chunk.");
            }
            at = start + length;
        }
        return new(objects.ToArray(), index, data);
    }
    private static uint U(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
    private static CacheException Invalid(string message) => new("AUDIO_FORMAT_INVALID", message);
}
