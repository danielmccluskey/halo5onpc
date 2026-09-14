using System.Text.Json;
using H5SoloLauncher.Core.Storage;
namespace H5SoloLauncher.Worker;
internal static class PlayLog
{
    public static string PathName=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"h5sololauncher","logs","play.log");
    public static void Write<T>(T entry)
    {
        try{
            var path=PathName;var root=Path.GetDirectoryName(path)!;SafePaths.NoLinks(root);Directory.CreateDirectory(root);SafePaths.NoLinks(path);
            if(File.Exists(path) && new FileInfo(path).Length>4*1024*1024)File.Move(path,SafePaths.Child(root,"play.previous.log"),true);
            var text=JsonSerializer.Serialize(entry);if(text.Length>32768)text=text[..32768];
            File.AppendAllText(path,$"{DateTimeOffset.UtcNow:O} {text}{Environment.NewLine}");
        }catch(Exception){ /* A logging failure must not replace the game result. */ }
    }
}
