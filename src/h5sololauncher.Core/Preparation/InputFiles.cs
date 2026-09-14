using System.Security.Cryptography;
using System.Text.Json;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Core.Preparation;

internal static class InputFiles
{
    public static void CleanWork(string root)
    {
        // The caller holds the cache lease. Only our own abandoned temporary files qualify.
        var work = SafePaths.Child(root, "inputs/work"); if (!Directory.Exists(work)) return;
        foreach (var candidate in Directory.EnumerateFiles(work, "*.tmp", SearchOption.TopDirectoryOnly))
        {
            var name = System.IO.Path.GetFileName(candidate);
            if (Guid.TryParseExact(System.IO.Path.GetFileNameWithoutExtension(name), "N", out _))
                File.Delete(SafePaths.Child(root, "inputs/work/" + name));
        }
    }
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public static string PathFor(string folder, string id, string extension = ".json")
    {
        if (id.Length != 64 || id.Any(x => !Uri.IsHexDigit(x))) throw Damaged();
        return "inputs/" + folder + "/" + id.ToLowerInvariant() + extension;
    }
    public static string Digest(Stream stream, CancellationToken cancellation)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[256 * 1024]; int read;
        while ((read = stream.Read(buffer)) > 0) { cancellation.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    public static T Read<T>(string root, string relative, long maximum, string? digest = null)
    {
        using var stream = File.OpenRead(SafePaths.Child(root, relative));
        if (stream.Length > maximum || (digest is not null && Digest(stream, default) != digest)) throw Damaged();
        stream.Position = 0;
        return JsonSerializer.Deserialize<T>(stream) ?? throw Damaged();
    }
    public static string Save<T>(string root, string folder, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value); var id = Hash(bytes);
        Write(root, PathFor(folder, id), bytes); return id;
    }
    public static void Write(string root, string relative, byte[] bytes)
    {
        var destination = SafePaths.Child(root, relative); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
        Directory.CreateDirectory(SafePaths.Child(root, "inputs/work"));
        var temporary = SafePaths.Child(root, "inputs/work/" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static CacheException Damaged() => new("INPUT_CACHE_DAMAGED", "The conversion input cache is incomplete or damaged. Build conversion inputs again to repair it.");
}
