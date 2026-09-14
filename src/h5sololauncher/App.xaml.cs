using System.Windows;
using H5SoloLauncher.Services;
using H5SoloLauncher.ViewModels;

namespace H5SoloLauncher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException+=(_,failure)=>
        {
            failure.Handled=true;
            var details=UnexpectedErrorLog.Save(failure.Exception);
            MessageBox.Show("The launcher stopped because of an unexpected error. Restart it to continue.\n\n"+details,
                "h5sololauncher",MessageBoxButton.OK,MessageBoxImage.Error);
            Shutdown(1);
        };
        AppDomain.CurrentDomain.UnhandledException+=(_,failure)=>
        { if(failure.ExceptionObject is Exception error)UnexpectedErrorLog.Save(error); };
        // This small utility shares the desktop with a GPU-heavy game. Keep its controls independent of the game's rendering state.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        base.OnStartup(e);
        var checker = new ForgeInstallationChecker(new WindowsForgePackageSource());
        var settings = LauncherSettingsStore.ForCurrentUser();
        var campaign = new CampaignDumpViewModel(new CampaignDumpValidator(), settings);
        var worker = new IndexWorkerClient();
        var cache = new CacheViewModel(settings, worker);
        MainWindow = new MainWindow(new MainWindowViewModel(checker, campaign, cache, worker));
        MainWindow.Show();
    }
}
