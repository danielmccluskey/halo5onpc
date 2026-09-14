using System.IO;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Services;

internal static class UnexpectedErrorLog
{
    public static string Save(Exception error)
    {
        try
        {
            var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"h5sololauncher","logs");
            SafePaths.NoLinks(folder);Directory.CreateDirectory(folder);
            var path=SafePaths.Child(folder,"launcher-error.log");
            if(File.Exists(path) && new FileInfo(path).Length>1024*1024)
                File.Move(path,SafePaths.Child(folder,"launcher-error.previous.log"),true);
            File.AppendAllText(path,$"{DateTimeOffset.Now:O} {typeof(App).Assembly.GetName().Version}\n{error}\n\n");
            return $"Error details were saved to:\n{path}";
        }
        catch(Exception logError)
        {
            return $"The error log could not be saved: {logError.Message}\n\nOriginal error: {error.Message}";
        }
    }
}
