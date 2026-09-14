using System.Buffers.Binary;
using System.Text;
using H5SoloLauncher.Core;

namespace H5SoloLauncher.Worker;

public sealed record CampaignUiStatus(uint Phase,uint Assets,uint Rebound,uint Rejected);
public static class CampaignUiRuntime
{
    public static CampaignUiStatus Run(string forgeRoot,string package,string configuration,string digest,bool activate,CancellationToken cancellation)
    {
        var target=PackageTarget.Forge(forgeRoot,package);var running=target.Find()??throw new CacheException("FORGE_NOT_RUNNING","Forge closed before native menu retention setup.");
        using var session=PackageProcessSession.Attach(target,"h5sololauncher.CampaignUi.dll","H5Ui",cancellation);
        var path=Encoding.Unicode.GetBytes(configuration+'\0');if(path.Length>4096 || digest.Length!=64 || digest.Any(c=>!Uri.IsHexDigit(c)))throw new CacheException("CAMPAIGN_UI_PATH","The native menu retention path or digest is invalid.");
        byte[] Call(uint operation)
        {
            if(target.Find()!=running)throw new CacheException("FORGE_PROCESS_CHANGED","Forge restarted during native menu retention setup.");
            var bytes=new byte[4720];W(bytes,0,0x52553548);W(bytes,4,1);W(bytes,8,operation);BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16),running.Created);
            path.CopyTo(bytes,552);Encoding.ASCII.GetBytes(digest).CopyTo(bytes,4648);var result=session.Call(bytes,cancellation);
            if(U(result,12)!=0){var end=Array.IndexOf(result,(byte)0,40,512);throw new CacheException("CAMPAIGN_UI_FAILED",Encoding.UTF8.GetString(result,40,(end<0?552:end)-40));}return result;
        }
        var observed=Call(0);if(activate && U(observed,24)==0)observed=Call(1);
        if(activate && U(observed,24)!=1)throw new CacheException("CAMPAIGN_UI_FAILED","Native menu retention did not finish. Restart Forge before retrying.");
        return new(U(observed,24),U(observed,28),U(observed,32),U(observed,36));
    }
    private static uint U(byte[] bytes,int at)=>BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
    private static void W(byte[] bytes,int at,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at),value);
}
