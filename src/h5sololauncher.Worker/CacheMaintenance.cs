using System.Diagnostics;
using H5SoloLauncher.Core.Runtime;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Worker;

internal static class CacheMaintenance
{
    public static H5SoloLauncher.Core.Preparation.PreparationResult Prepare(
        H5SoloLauncher.Core.Preparation.PrepareRequest request,IProgress<H5SoloLauncher.Core.IndexProgress> progress,CancellationToken cancellation)
    {
        var result=new H5SoloLauncher.Core.Preparation.PreparationCoordinator(new PreparationServices()).Run(request,progress,cancellation);
        if(result.State!="Ready") return result;
        return result with { Message="The campaign through Guardians is prepared. " + Pending(request.CacheRoot,request.ForgeRoot,request.PackageFullName) };
    }
    public static CacheCleanupResult Run(string root,string forge,string package,bool preview=false)
        => CacheCleanup.Run(root,forge,package,Running,preview);
    private static bool Running()
    {
        var processes=Process.GetProcessesByName("halo5forge");
        try { return processes.Length>0; }
        finally { foreach(var process in processes) process.Dispose(); }
    }
    public static string Pending(string root,string forge,string package)
    {
        try { root=CacheFolders.OpenExisting(root,forge).Root; return File.Exists(SafePaths.Child(root,CacheCleanup.Pending)) ? Run(root,forge,package).Message : ""; }
        catch(Exception error) { return "Cache is ready; cleanup deferred: " + error.Message; }
    }
}
