using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;
using H5SoloLauncher.ViewModels;

namespace H5SoloLauncher;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel viewModel;
    private bool closing;
    private bool closeAllowed;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = viewModel;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= Window_Loaded;
        await viewModel.InitializeAsync();
    }

    private async void Check_Click(object sender, RoutedEventArgs e) => await viewModel.CheckAsync();
    private async void StartForge_Click(object sender, RoutedEventArgs e) => await viewModel.StartForgeAsync();
    private void StopForgeStartup_Click(object sender, RoutedEventArgs e) => viewModel.StopForgeStartup();
    private void FindHalo_Click(object sender, RoutedEventArgs e)
    {
        if (!viewModel.NeedsHaloApp) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-windows-store://pdp/?PFN=Microsoft.Tomp_8wekyb3d8bbwe") { UseShellExecute = true }); }
        catch (Exception exception) { viewModel.SetCopyFeedback("Couldn’t open the Halo Store page: " + exception.Message); }
    }

    private async void ChooseCampaign_Click(object sender, RoutedEventArgs e)
    {
        if (!viewModel.CanChooseCampaignFolder) return;
        var dialog = new OpenFolderDialog { Title = "Choose the extracted Halo 5: Guardians game folder", Multiselect = false };
        try
        {
            if (dialog.ShowDialog(this) == true)
                await viewModel.Campaign.CheckAsync(dialog.FolderName);
        }
        catch (Exception exception)
        {
            viewModel.SetCopyFeedback("Couldn’t open the folder picker: " + exception.Message);
        }
    }

    private async void RecheckCampaign_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.CanRecheckCampaignFolder)
            await viewModel.Campaign.CheckAsync(viewModel.Campaign.Directory);
    }

    private void Window_Closed(object? sender, EventArgs e) => viewModel.Campaign.Cancel();

    private async void ChooseCache_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.Cache?.CanChoose != true) return;
        try
        {
            var picker = new OpenFolderDialog { Title = "Choose where to keep the h5sololauncher cache", Multiselect = false };
            if (picker.ShowDialog(this) == true) await viewModel.Cache.ChooseAsync(picker.FolderName);
        }
        catch (Exception exception) { viewModel.SetCopyFeedback("Couldn’t choose the cache folder: " + exception.Message); }
    }

    private async void Index_Click(object sender, RoutedEventArgs e)
    { if (viewModel.Cache is not null) await viewModel.Cache.IndexAsync(); }
    private async void Prepare_Click(object sender, RoutedEventArgs e)
    { if (viewModel.Cache is not null) await viewModel.Cache.PrepareAsync(); }
    private async void Play_Click(object sender,RoutedEventArgs e)
    { if(viewModel.Cache is not null)await viewModel.Cache.PrimaryAsync(); }

    private void Pause_Click(object sender, RoutedEventArgs e) => viewModel.Cache?.Pause();

    private async void Plan_Click(object sender, RoutedEventArgs e)
    { if (viewModel.Cache is not null) await viewModel.Cache.PlanAsync(); }

    private async void PrepareForge_Click(object sender, RoutedEventArgs e)
    { if (viewModel.Cache is not null) await viewModel.Cache.PrepareForgeAsync(); }

    private async void AllowForgeCacheRead_Click(object sender, RoutedEventArgs e)
    { if (viewModel.Cache is not null) await viewModel.Cache.AllowForgeCacheReadAsync(); }

    private async void BuildInputs_Click(object sender, RoutedEventArgs e)
    { if (viewModel.Cache is not null) await viewModel.Cache.BuildInputsAsync(); }

    private void ShowInputs_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.Cache?.CanOpenInputs != true) return;
        try
        {
            var path = viewModel.Cache.InputManifestPath;
            if (!System.IO.File.Exists(path)) throw new System.IO.FileNotFoundException("Build conversion inputs again to restore the manifest.");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true, Arguments = "/select,\"" + path + "\"" });
        }
        catch (Exception exception) { viewModel.SetCopyFeedback("Couldn’t show the input manifest: " + exception.Message); }
    }

    private void ShowForgeReport_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.Cache?.CanOpenForgeReport != true) return;
        try
        {
            var path = viewModel.Cache.ForgeReportPath;
            if (!System.IO.File.Exists(path)) throw new System.IO.FileNotFoundException("Prepare Forge data again to restore the report.");
            var start = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true, Arguments = "/select,\"" + path + "\"" };
            System.Diagnostics.Process.Start(start);
        }
        catch (Exception exception) { viewModel.SetCopyFeedback("Couldn’t show the compatibility report: " + exception.Message); }
    }

    private void ShowPlan_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.Cache?.CanOpenPlan != true) return;
        try
        {
            var path = viewModel.Cache.PlanPath;
            if (!System.IO.File.Exists(path)) throw new System.IO.FileNotFoundException("The saved plan is unavailable. Check dependencies again.");
            var start = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.Arguments = "/select,\"" + path + "\"";
            System.Diagnostics.Process.Start(start);
        }
        catch (Exception exception) { viewModel.SetCopyFeedback("Couldn’t show the plan: " + exception.Message); }
    }

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (closeAllowed) return;
        if (closing) { e.Cancel = true; return; }
        if (viewModel.IsStartingForge)
        {
            e.Cancel = true; closing = true;
            await viewModel.StopForgeStartupAndWaitAsync();
            closeAllowed = true; Close(); return;
        }
        if (viewModel.Cache?.IsBusy != true) return;
        e.Cancel = true;
        if (!viewModel.Cache.IsIndexing) return; // Let a short folder/settings operation finish first.
        closing = true;
        await viewModel.Cache.PauseAndWaitAsync();
        closeAllowed = true;
        Close();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!viewModel.CanCopy) return;
        try
        {
            Clipboard.SetText(viewModel.Details);
            viewModel.SetCopyFeedback("Details copied.");
        }
        catch (ExternalException)
        {
            viewModel.SetCopyFeedback("The clipboard is busy. Try again, or select and copy the text under Details.");
        }
    }
}
