using System.ComponentModel;
using H5SoloLauncher.Models;
using H5SoloLauncher.Services;

namespace H5SoloLauncher.ViewModels;

public sealed class CampaignDumpViewModel(ICampaignDumpValidator validator, ILauncherSettingsStore settings) : INotifyPropertyChanged
{
    private readonly CancellationTokenSource cancellation = new();
    private CampaignDumpCheckResult? result;
    private string directory = string.Empty;
    private string settingsError = string.Empty;
    public bool IsChecking { get; private set; }
    public string Directory => result?.SelectedDirectory ?? directory;
    public bool HasDirectory => !string.IsNullOrWhiteSpace(Directory);
    public bool IsRecognized => !IsChecking && result?.Status == CampaignDumpStatus.Recognized;
    public string? RecognizedRoot => IsRecognized ? result!.Dump!.RootDirectory : null;
    public bool HasDetails => result is not null || settingsError.Length > 0;
    public string SettingsNotice { get; private set; } = string.Empty;

    public string Status => IsChecking ? "Checking campaign files…" : result?.Status switch
    {
        CampaignDumpStatus.Recognized => "Campaign dump found.",
        CampaignDumpStatus.MissingFolder => "Campaign folder is unavailable.",
        CampaignDumpStatus.WrongFolder => "Choose the exported game folder.",
        CampaignDumpStatus.Incomplete => "Some campaign files are missing.",
        CampaignDumpStatus.UnsupportedFormat => "Some files aren’t recognized.",
        CampaignDumpStatus.CheckFailed => "Couldn’t check the campaign folder.",
        _ => "Choose your extracted Halo 5: Guardians dump."
    };

    public string Message => IsChecking ? "Reading the export’s manifest and module headers. Your files stay in place." :
        result?.Status == CampaignDumpStatus.Recognized
            ? $"Export version {result.Dump!.PackageVersion}. Folder layout and module headers checked. Full content checks will run during cache generation."
            : result?.Problems is { Count: > 0 } ? string.Join(Environment.NewLine, result.Problems) :
                "Select the folder containing AppxManifest.xml and deploy.";

    public string Details => (result is null ? string.Empty :
        $"Campaign result: {result.Status}\nSelected folder: {Directory}\n" +
        (result.Dump is not { } dump ? string.Empty :
            $"Export version: {dump.PackageVersion}\nModules: {dump.ModuleCount}\nCampaign metadata files: {dump.CampaignMetadataCount}\n" +
            $"Audio packages: {dump.AudioPackageCount}\nMovies: {dump.MovieCount}\n") +
        (result.Problems is null ? string.Empty : string.Join("\n", result.Problems)) +
        (result.ErrorDetails is null ? string.Empty : $"\n{result.ErrorDetails}")) +
        (settingsError.Length == 0 ? string.Empty : $"\nSettings: {settingsError}");

    public async Task RestoreAsync()
    {
        if (IsChecking || cancellation.IsCancellationRequested) return;
        BeginCheck();
        try
        {
            directory = await settings.LoadCampaignDirectoryAsync() ?? string.Empty;
            if (HasDirectory) await ValidateAsync(remember: false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            SettingsNotice = "Couldn’t read the saved folder. Choose your export again.";
            settingsError = exception.ToString();
        }
        finally { EndCheck(); }
    }

    public async Task CheckAsync(string selectedDirectory)
    {
        if (IsChecking || cancellation.IsCancellationRequested) return;
        directory = selectedDirectory;
        BeginCheck();
        try { await ValidateAsync(remember: true); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { EndCheck(); }
    }

    private async Task ValidateAsync(bool remember)
    {
        try { result = await validator.ValidateAsync(directory, cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            result = new(CampaignDumpStatus.CheckFailed, directory,
                Problems: ["The campaign folder could not be checked. Try again or copy the details."], ErrorDetails: exception.ToString());
        }
        cancellation.Token.ThrowIfCancellationRequested();
        if (!remember || result.Status != CampaignDumpStatus.Recognized) return;
        try { await settings.SaveCampaignDirectoryAsync(result.Dump!.RootDirectory); }
        catch (Exception exception)
        {
            SettingsNotice = "The export was found, but its folder couldn’t be saved. Choose it again next time.";
            settingsError = exception.ToString();
        }
    }

    public void Cancel() => cancellation.Cancel();

    private void BeginCheck()
    {
        result = null;
        settingsError = SettingsNotice = string.Empty;
        IsChecking = true;
        Refresh();
    }

    private void EndCheck() { IsChecking = false; Refresh(); }

    private void Refresh()
    {
        foreach (var name in new[] { nameof(IsChecking), nameof(IsRecognized), nameof(RecognizedRoot), nameof(Directory), nameof(HasDirectory), nameof(HasDetails),
            nameof(Status), nameof(Message), nameof(Details), nameof(SettingsNotice) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
