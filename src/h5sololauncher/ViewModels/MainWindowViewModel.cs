using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using H5SoloLauncher.Models;
using H5SoloLauncher.Services;
using H5SoloLauncher.Core.Forge;

namespace H5SoloLauncher.ViewModels;

public sealed partial class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly IForgeInstallationChecker checker;
    private bool isChecking;
    private ForgeCheckResult? result;
    private DateTimeOffset? checkedAt;
    private string copyFeedback = string.Empty;

    public MainWindowViewModel(IForgeInstallationChecker checker, CampaignDumpViewModel campaign, CacheViewModel? cache = null, IForgeLaunchWorker? launcher = null)
    {
        this.checker = checker;
        this.launcher = launcher;
        Campaign = campaign;
        Cache = cache;
        if (Cache is not null) Cache.PropertyChanged += (_, _) => RefreshCacheSignals();
        Campaign.PropertyChanged += (_, _) =>
        {
            ConfigureCache();
            OnPropertyChanged(nameof(CanCheck));
            OnPropertyChanged(nameof(CanCopy));
            OnPropertyChanged(nameof(CanChooseCampaignFolder));
            OnPropertyChanged(nameof(CanRecheckCampaignFolder));
            OnPropertyChanged(nameof(Details));
        };
    }

    public CampaignDumpViewModel Campaign { get; }
    public CacheViewModel? Cache { get; }
    public bool IsChecking => isChecking;
    public bool CanCheck => !isChecking && !IsStartingForge && !Campaign.IsChecking && Cache?.IsBusy != true;
    public bool CanCopy => !isChecking && !Campaign.IsChecking && result is not null;
    public bool CanChooseCampaignFolder => CanCheck && result?.Status == ForgeCheckStatus.Installed;
    public bool CanRecheckCampaignFolder => CanChooseCampaignFolder && Campaign.HasDirectory;
    public bool HasInstallation => result?.Installation is not null;
    public string Version => result?.Installation?.Version.ToString() ?? string.Empty;
    public string InstallDirectory => result?.Installation?.InstallDirectory ?? string.Empty;
    public string CopyFeedback => copyFeedback;

    public string Status => isChecking ? "Checking for Forge…" : result?.Status switch
    {
        ForgeCheckStatus.Installed => "Forge is installed.",
        ForgeCheckStatus.NotInstalled => "Forge wasn’t found.",
        ForgeCheckStatus.NeedsAttention => "Forge needs attention.",
        ForgeCheckStatus.CheckFailed => "Couldn’t check for Forge.",
        _ => "Halo 5 Forge"
    };

    public string Message => isChecking ? "Looking at the apps installed for this Windows account." : result?.Status switch
    {
        ForgeCheckStatus.Installed => "Windows reports a healthy installation for this account.",
        ForgeCheckStatus.NotInstalled => "Install Halo 5: Forge for this Windows account, then check again.",
        ForgeCheckStatus.NeedsAttention => "Windows found Forge, but its installation isn’t ready. Check it in Windows Settings → Apps, then try again.",
        ForgeCheckStatus.CheckFailed => "Windows couldn’t return the installation details. Try again, or copy the details below for a bug report.",
        _ => "Check whether Halo 5: Forge is installed."
    };

    public string Details
    {
        get
        {
            if (result is null) return string.Empty;
            var appVersion = Assembly.GetExecutingAssembly().GetName().Version;
            var installation = result.Installation;
            return $"h5sololauncher {appVersion}\n" +
                $"Checked: {checkedAt:O}\nWindows: {Environment.OSVersion.VersionString}\n" +
                $"Result: {result.Status}\nPackage family: {WindowsForgePackageSource.PackageFamilyName}\n" +
                (installation is null ? string.Empty :
                    $"Package: {installation.PackageFullName}\nVersion: {installation.Version}\n" +
                    $"Folder: {installation.InstallDirectory}\nWindows package healthy: {installation.IsHealthy}\n") +
                (result.ErrorDetails is null ? string.Empty : $"\n{result.ErrorDetails}") +
                (Campaign.HasDetails ? $"\n\n{Campaign.Details}" : string.Empty) +
                (Cache is null ? string.Empty : $"\n\n{Cache.Details}") + "\n\n" + LaunchDetails;
        }
    }

    public async Task InitializeAsync()
    {
        await CheckAsync();
        await Campaign.RestoreAsync();
        if (Cache is not null) await Cache.RestoreAsync();
    }

    public async Task CheckAsync()
    {
        if (!CanCheck) return;
        isChecking = true;
        result = null;
        copyFeedback = string.Empty;
        Refresh();
        try
        {
            result = await checker.CheckAsync();
        }
        catch (Exception exception)
        {
            result = new ForgeCheckResult(ForgeCheckStatus.CheckFailed, ErrorDetails: exception.ToString());
        }
        finally
        {
            checkedAt = DateTimeOffset.Now;
            isChecking = false;
            Refresh();
        }
    }

    public void SetCopyFeedback(string message)
    {
        copyFeedback = message;
        OnPropertyChanged(nameof(CopyFeedback));
    }

    private void Refresh()
    {
        ConfigureCache();
        foreach (var property in new[]
        {
            nameof(IsChecking), nameof(CanCheck), nameof(CanCopy), nameof(HasInstallation), nameof(CanChooseCampaignFolder), nameof(CanRecheckCampaignFolder),
            nameof(Version), nameof(InstallDirectory), nameof(Status), nameof(Message), nameof(Details), nameof(CopyFeedback),
            nameof(IsStartingForge), nameof(CanStartForge), nameof(CanStopForgeStartup), nameof(LaunchStatus), nameof(LaunchMessage), nameof(NeedsHaloApp)
        }) OnPropertyChanged(property);
    }

    private void ConfigureCache() => Cache?.Configure(Campaign.RecognizedRoot ?? Campaign.Directory, InstallDirectory,
        !isChecking && !IsStartingForge && !Campaign.IsChecking && Campaign.IsRecognized && result?.Status == ForgeCheckStatus.Installed,
        result?.Installation?.PackageFullName ?? string.Empty,
        !isChecking && !IsStartingForge && !Campaign.IsChecking && result?.Status == ForgeCheckStatus.Installed);

    private void RefreshCacheSignals()
    {
        foreach (var name in new[] { nameof(CanCheck), nameof(CanCopy), nameof(CanChooseCampaignFolder), nameof(CanRecheckCampaignFolder), nameof(Details), nameof(CanStartForge), nameof(NeedsHaloApp) })
            OnPropertyChanged(name);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
