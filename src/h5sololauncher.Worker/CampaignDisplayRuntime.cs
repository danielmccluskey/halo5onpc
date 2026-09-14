using System.Buffers.Binary;
using System.Text;
using H5SoloLauncher.Core;

namespace H5SoloLauncher.Worker;
public sealed record CampaignDisplayStatus(uint Phase,float Fov,uint FrameOption,uint SaveError);
public static class CampaignDisplayRuntime
{
    public static CampaignDisplayStatus Run(string forgeRoot,string package,string configuration,string digest,bool activate,CancellationToken cancellation)
    {
        var target=PackageTarget.Forge(forgeRoot,package);var running=target.Find()??throw new CacheException("FORGE_NOT_RUNNING","Forge closed before display settings setup.");
        using var session=PackageProcessSession.Attach(target,"h5sololauncher.CampaignDisplay.dll","H5Display",cancellation);
        var path=Encoding.Unicode.GetBytes(configuration+'\0');if(path.Length>4096 || digest.Length!=64 || digest.Any(c=>!Uri.IsHexDigit(c)))throw new CacheException("CAMPAIGN_DISPLAY_PATH","The campaign display settings path or digest is invalid.");
        byte[] Call(uint operation)
        {
            if(target.Find()!=running)throw new CacheException("FORGE_PROCESS_CHANGED","Forge restarted during display settings setup.");
            var bytes=new byte[4720];W(bytes,0,0x44523548);W(bytes,4,1);W(bytes,8,operation);BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16),running.Created);
            path.CopyTo(bytes,552);Encoding.ASCII.GetBytes(digest).CopyTo(bytes,4648);var result=session.Call(bytes,cancellation);
            if(U(result,12)!=0){var end=Array.IndexOf(result,(byte)0,40,512);throw new CacheException("CAMPAIGN_DISPLAY_FAILED",Encoding.UTF8.GetString(result,40,(end<0?552:end)-40));}return result;
        }
        var observed=Call(0);if(activate && U(observed,24)==0)observed=Call(1);
        var deadline=DateTime.UtcNow.AddMinutes(2);
        while(activate && U(observed,24)==1 && DateTime.UtcNow<deadline){cancellation.ThrowIfCancellationRequested();cancellation.WaitHandle.WaitOne(250);observed=Call(0);}
        if(activate && U(observed,24)!=2)throw new CacheException("CAMPAIGN_DISPLAY_TIMEOUT","Display settings preparation did not finish. Restart Forge before retrying.");
        return new(U(observed,24),BitConverter.Int32BitsToSingle(unchecked((int)U(observed,28))),U(observed,32),U(observed,36));
    }
    private static uint U(byte[] bytes,int at)=>BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
    private static void W(byte[] bytes,int at,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at),value);
}
