using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Forge;
using H5SoloLauncher.Models;

namespace H5SoloLauncher.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly IForgeLaunchWorker? launcher;
    private CancellationTokenSource? launchStop;
    private Task? launchTask;
    private ForgeLaunchResult? launchResult;
    private string launchStage = string.Empty;
    public bool IsStartingForge { get; private set; }
    public bool CanStartForge => CanCheck && launcher is not null && result?.Status == ForgeCheckStatus.Installed;
    public bool CanStopForgeStartup => IsStartingForge && launchStop?.IsCancellationRequested == false;
    public bool NeedsHaloApp => !IsStartingForge && Cache?.IsBusy != true && (launchResult?.Code == "HALO_APP_MISSING" || Cache?.NeedsHaloApp == true);
    public string LaunchStatus => IsStartingForge ? launchStage : launchResult?.State switch
    {
        "Running" => "Forge startup checked.", "Failed" => "Forge startup needs attention.", "Paused" => "Stopped waiting for Forge.", _ => "Start Forge here."
    };
    public string LaunchMessage => launchResult?.Message ?? "Play starts the Halo app and Forge automatically, prepares campaign support and opens Solo.";
    public string LaunchDetails => $"Forge startup: {LaunchStatus}\n{LaunchMessage}\nProcess ID: {launchResult?.ProcessId}\nError: {launchResult?.Code}\n{launchResult?.Details}\n" +
        "Startup log: " + System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "h5sololauncher", "logs", "launch.log");
    public Task StartForgeAsync()
    {
        if (!CanStartForge) return Task.CompletedTask;
        launchTask = StartForgeCoreAsync(); return launchTask;
    }
    private async Task StartForgeCoreAsync()
    {
        var installation = result!.Installation!;
        launchStop = new(); IsStartingForge = true; launchResult = null; launchStage = "Starting Forge…"; Refresh();
        try
        {
            launchResult = await launcher!.StartForgeAsync(new(installation.InstallDirectory, installation.PackageFullName),
                new Progress<IndexProgress>(p => { launchStage = p.Stage; Refresh(); }), launchStop.Token);
        }
        catch (Exception e) { launchResult = new("Failed", Code: "FORGE_LAUNCH_FAILED", Message: e.Message, Details: e.ToString()); }
        finally { launchStop.Dispose(); launchStop = null; IsStartingForge = false; Refresh(); }
    }
    public void StopForgeStartup() { launchStop?.Cancel(); Refresh(); }
    public async Task StopForgeStartupAndWaitAsync() { StopForgeStartup(); if (launchTask is not null) await launchTask; }
}
