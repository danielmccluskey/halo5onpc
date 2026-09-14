using System.IO;
using System.Text.Json;

namespace H5SoloLauncher.Services;

public interface ILauncherSettingsStore
{
    Task<string?> LoadCampaignDirectoryAsync();
    Task SaveCampaignDirectoryAsync(string directory);
    Task<string?> LoadCacheDirectoryAsync();
    Task SaveCacheDirectoryAsync(string directory);
}

public sealed class LauncherSettingsStore(string settingsPath) : ILauncherSettingsStore
{
    public static LauncherSettingsStore ForCurrentUser() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "h5sololauncher", "settings.json"));

    public async Task<string?> LoadCampaignDirectoryAsync() => (await LoadAsync()).CampaignDirectory;
    public async Task<string?> LoadCacheDirectoryAsync() => (await LoadAsync()).CacheDirectory;

    private async Task<Settings> LoadAsync()
    {
        try
        {
            using var stream = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 16 * 1024) throw new InvalidDataException("Saved settings are too large.");
            var settings = await JsonSerializer.DeserializeAsync<Settings>(stream)
                ?? throw new InvalidDataException("Saved settings are empty.");
            if (settings.SchemaVersion is not (1 or 2)) throw new InvalidDataException("Saved settings use an unknown version.");
            foreach (var path in new[] { settings.CampaignDirectory, settings.CacheDirectory })
                if (!string.IsNullOrWhiteSpace(path) && !Path.IsPathFullyQualified(path))
                    throw new InvalidDataException("A saved folder is not an absolute path.");
            return settings with { SchemaVersion = 2 };
        }
        catch (FileNotFoundException) { return new(2, null, null); }
        catch (DirectoryNotFoundException) { return new(2, null, null); }
    }

    public async Task SaveCampaignDirectoryAsync(string directory) => await SaveAsync((await LoadAsync()) with { CampaignDirectory = directory });
    public async Task SaveCacheDirectoryAsync(string directory) => await SaveAsync((await LoadAsync()) with { CacheDirectory = directory });

    private async Task SaveAsync(Settings settings)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(settingsPath))!;
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, $"settings-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(settings));
            File.Move(temporary, settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed record Settings(int SchemaVersion, string? CampaignDirectory, string? CacheDirectory = null);
}
