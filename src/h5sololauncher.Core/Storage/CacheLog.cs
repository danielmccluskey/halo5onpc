namespace H5SoloLauncher.Core.Storage;

internal static class CacheLog
{
    public static void TryWrite(string root, string message) { try { Write(root, message); } catch { } }
    public static void Write(string root, string message)
    {
        var directory = SafePaths.Child(root, "logs"); Directory.CreateDirectory(directory);
        var path = SafePaths.Child(root, "logs/index.log");
        if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
            File.Move(path, SafePaths.Child(root, "logs/index.previous.log"), overwrite: true);
        File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {message[..Math.Min(message.Length, 32 * 1024)]}{Environment.NewLine}");
    }
}
