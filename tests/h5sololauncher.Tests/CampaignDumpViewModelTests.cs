using H5SoloLauncher.Models;
using H5SoloLauncher.Services;
using H5SoloLauncher.ViewModels;
using Xunit;

namespace H5SoloLauncher.Tests;

public sealed class CampaignDumpViewModelTests
{
    [Fact]
    public async Task RevalidatesSavedFolderInsteadOfTrustingPriorSuccess()
    {
        var validator = new Validator(new(CampaignDumpStatus.MissingFolder, "missing export"));
        var settings = new MemorySettings { Directory = "missing export" };
        var model = new CampaignDumpViewModel(validator, settings);
        await model.RestoreAsync();
        Assert.Equal("missing export", validator.CheckedDirectory);
        Assert.Contains("unavailable", model.Status);
        Assert.Equal(0, settings.Saves);
    }

    [Fact]
    public async Task InvalidSelectionDoesNotReplaceLastRecognizedFolder()
    {
        var settings = new MemorySettings { Directory = "prior export" };
        var model = new CampaignDumpViewModel(new Validator(new(CampaignDumpStatus.Incomplete, "new export")), settings);
        await model.CheckAsync("new export");
        Assert.Equal("prior export", settings.Directory);
        Assert.Equal(0, settings.Saves);
    }

    [Fact]
    public async Task SaveFailureKeepsRecognizedResultAndExplainsPersistenceProblem()
    {
        var model = new CampaignDumpViewModel(new Validator(Recognized()), new MemorySettings { FailSave = true });
        await model.CheckAsync("export");
        Assert.Equal("Campaign dump found.", model.Status);
        Assert.Contains("couldn’t be saved", model.SettingsNotice);
        Assert.Contains("settings locked", model.Details);
    }

    [Fact]
    public async Task RecognizedSelectionSavesNormalizedRoot()
    {
        var settings = new MemorySettings();
        var model = new CampaignDumpViewModel(new Validator(Recognized()), settings);
        await model.CheckAsync("export/deploy");
        Assert.Equal("export", settings.Directory);
        Assert.Equal(1, settings.Saves);
    }

    [Fact]
    public async Task BadSettingsLeaveFolderSelectionUsable()
    {
        var model = new CampaignDumpViewModel(new Validator(Recognized()), new MemorySettings { FailLoad = true });
        await model.RestoreAsync();
        Assert.False(model.IsChecking);
        Assert.Contains("saved folder", model.SettingsNotice);
        await model.CheckAsync("export");
        Assert.Equal("Campaign dump found.", model.Status);
    }

    [Fact]
    public async Task ClosingDuringValidationDoesNotSaveOrStartAnotherCheck()
    {
        var validator = new PendingValidator();
        var settings = new MemorySettings();
        var model = new CampaignDumpViewModel(validator, settings);
        var running = model.CheckAsync("export");
        await model.CheckAsync("another export");
        Assert.Equal(1, validator.Calls);
        model.Cancel();
        validator.Completion.SetResult(Recognized());
        await running;
        Assert.False(model.IsChecking);
        Assert.Equal(0, settings.Saves);
        await model.CheckAsync("another export");
        Assert.Equal(1, validator.Calls);
    }

    internal static CampaignDumpViewModel Empty() => new(
        new Validator(new(CampaignDumpStatus.WrongFolder, "")), new MemorySettings());

    private static CampaignDumpCheckResult Recognized() => new(CampaignDumpStatus.Recognized, "export", new("export", "1.0.0.0", 4, 1, 1, 1));

    private sealed class PendingValidator : ICampaignDumpValidator
    {
        public int Calls { get; private set; }
        public TaskCompletionSource<CampaignDumpCheckResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<CampaignDumpCheckResult> ValidateAsync(string directory, CancellationToken cancellation = default)
        {
            Calls++;
            return Completion.Task;
        }
    }

    private sealed class Validator(CampaignDumpCheckResult result) : ICampaignDumpValidator
    {
        public string? CheckedDirectory { get; private set; }
        public Task<CampaignDumpCheckResult> ValidateAsync(string directory, CancellationToken cancellation = default)
        {
            CheckedDirectory = directory;
            return Task.FromResult(result);
        }
    }

    private sealed class MemorySettings : ILauncherSettingsStore
    {
        public Task<string?> LoadCacheDirectoryAsync() => Task.FromResult<string?>(null);
        public Task SaveCacheDirectoryAsync(string directory) => Task.CompletedTask;
        public string? Directory { get; set; }
        public int Saves { get; private set; }
        public bool FailSave { get; init; }
        public bool FailLoad { get; init; }
        public Task<string?> LoadCampaignDirectoryAsync() => FailLoad
            ? throw new InvalidOperationException("bad settings") : Task.FromResult(Directory);
        public Task SaveCampaignDirectoryAsync(string directory)
        {
            if (FailSave) throw new InvalidOperationException("settings locked");
            Saves++;
            Directory = directory;
            return Task.CompletedTask;
        }
    }
}
