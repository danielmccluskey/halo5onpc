using System.IO;
using System.Text.Json;
using H5SoloLauncher.Services;
using Xunit;

namespace H5SoloLauncher.Tests;

public sealed class LauncherSettingsStoreTests
{
    [Fact]
    public async Task SavesOnlyTheSelectedPathAndCanReplaceIt()
    {
        using var fixture = new ExportFixture();
        var path = Path.Combine(fixture.Root, "settings", "settings.json");
        var store = new LauncherSettingsStore(path);
        Assert.Null(await store.LoadCampaignDirectoryAsync());
        await store.SaveCampaignDirectoryAsync(fixture.Root);
        Assert.Equal(fixture.Root, await store.LoadCampaignDirectoryAsync());
        var moved = Path.Combine(fixture.Root, "second export");
        await store.SaveCampaignDirectoryAsync(moved);
        Assert.Equal(moved, await store.LoadCampaignDirectoryAsync());
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(4, json.RootElement.EnumerateObject().Count());
        Assert.Equal(2, json.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.True(json.RootElement.GetProperty("KeepRebuildData").GetBoolean());
    }

    [Fact]
    public async Task MalformedSettingsAreReportedRatherThanSilentlyTrusted()
    {
        using var fixture = new ExportFixture();
        var path = Path.Combine(fixture.Root, "settings.json");
        await File.WriteAllTextAsync(path, "{broken");
        await Assert.ThrowsAsync<JsonException>(() => new LauncherSettingsStore(path).LoadCampaignDirectoryAsync());
    }

    [Theory]
    [InlineData("{\"SchemaVersion\":99,\"CampaignDirectory\":\"D:\\\\export\"}")]
    [InlineData("{\"SchemaVersion\":1,\"CampaignDirectory\":\"relative-export\"}")]
    public async Task UnknownSettingsVersionOrRelativePathCannotRedirectStartup(string contents)
    {
        using var fixture = new ExportFixture();
        var path = Path.Combine(fixture.Root, "settings.json");
        await File.WriteAllTextAsync(path, contents);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LauncherSettingsStore(path).LoadCampaignDirectoryAsync());
    }
}
