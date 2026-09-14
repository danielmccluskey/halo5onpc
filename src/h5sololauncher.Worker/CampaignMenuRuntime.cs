using System.Buffers.Binary;
using System.Text;
using H5SoloLauncher.Core;

namespace H5SoloLauncher.Worker;

public static class CampaignMenuRuntime
{
    public static bool IsReady(string forgeRoot,string package,string digest,CancellationToken cancellation)
    {
        var target=PackageTarget.Forge(forgeRoot,package);var running=target.Find();if(running is null)return false;
        using var session=PackageProcessSession.Attach(target,"h5sololauncher.CampaignRegistry.dll","H5Menu",cancellation);
        var bytes=new byte[4720];W(bytes,0,0x554d3548);W(bytes,4,1);BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16),running.Created);
        if(digest.Length!=64 || digest.Any(c=>!Uri.IsHexDigit(c)))throw new CacheException("CAMPAIGN_MENU_PATH","The menu digest is invalid.");
        Encoding.ASCII.GetBytes(digest).CopyTo(bytes,4648);var result=session.Call(bytes,cancellation);
        if(U(result,12)!=0){var end=Array.IndexOf(result,(byte)0,40,512);throw new CacheException("CAMPAIGN_MENU_FAILED",Encoding.UTF8.GetString(result,40,(end<0?552:end)-40));}
        return U(result,24)==5 && U(result,32)==1;
    }
    public static void Activate(string forgeRoot,string package,string configuration,string digest,IProgress<IndexProgress> progress,CancellationToken cancellation,bool openSolo=false)
    {
        var target=PackageTarget.Forge(forgeRoot,package);var running=target.Find()??throw new CacheException("FORGE_NOT_RUNNING","Forge closed before campaign menu setup.");
        using var session=PackageProcessSession.Attach(target,"h5sololauncher.CampaignRegistry.dll","H5Menu",cancellation);
        var path=Encoding.Unicode.GetBytes(configuration+'\0');if(path.Length>4096 || digest.Length!=64 || digest.Any(c=>!Uri.IsHexDigit(c)))throw new CacheException("CAMPAIGN_MENU_PATH","The campaign menu path or digest is invalid.");
        byte[] Call(uint operation)
        {
            if(target.Find()!=running)throw new CacheException("FORGE_PROCESS_CHANGED","Forge restarted during campaign menu setup.");
            var bytes=new byte[4720];W(bytes,0,0x554d3548);W(bytes,4,1);W(bytes,8,operation);BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16),running.Created);
            path.CopyTo(bytes,552);Encoding.ASCII.GetBytes(digest).CopyTo(bytes,4648);var result=session.Call(bytes,cancellation);
            if(U(result,12)!=0){var end=Array.IndexOf(result,(byte)0,40,512);throw new CacheException("CAMPAIGN_MENU_FAILED",Encoding.UTF8.GetString(result,40,(end<0?552:end)-40));}return result;
        }
        var observed=Call(0);if(U(observed,24)==0)observed=Call(1);
        var began=DateTime.UtcNow;
        while(U(observed,24)!=5)
        {
            if(DateTime.UtcNow-began>TimeSpan.FromMinutes(3))throw new CacheException("CAMPAIGN_MENU_TIMEOUT","Forge did not finish preparing its campaign menu. Restart Forge before retrying.");
            var phase=U(observed,24);progress.Report(new(phase==1?"Loading campaign menu pictures":"Opening the campaign menu",0,0));
            if(cancellation.WaitHandle.WaitOne(200))cancellation.ThrowIfCancellationRequested();observed=Call(phase>=2?2u:0u);
        }
        if(openSolo){began=DateTime.UtcNow;do{
            if(DateTime.UtcNow-began>TimeSpan.FromSeconds(60))throw new CacheException("CAMPAIGN_SOLO_TIMEOUT","Forge did not finish opening Solo. Restart Forge.");
            observed=Call(3);if(U(observed,32)==1)break;
            progress.Report(new("Opening Solo automatically",0,0));if(cancellation.WaitHandle.WaitOne(200))cancellation.ThrowIfCancellationRequested();
        }while(true);}
    }
    private static uint U(byte[] bytes,int at)=>BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
    private static void W(byte[] bytes,int at,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at),value);
}
