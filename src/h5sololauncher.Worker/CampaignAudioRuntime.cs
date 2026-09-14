using System.Buffers.Binary;
using System.Text;
using H5SoloLauncher.Core;

namespace H5SoloLauncher.Worker;
public sealed record CampaignAudioStatus(uint Phase,uint PackageId,uint Result);
public static class CampaignAudioRuntime
{
    public static CampaignAudioStatus Run(string forgeRoot,string package,bool activate,CancellationToken cancellation)
    {
        var target=PackageTarget.Forge(forgeRoot,package);var running=target.Find()??throw new CacheException("FORGE_NOT_RUNNING","Forge closed before campaign audio preparation.");
        using var session=PackageProcessSession.Attach(target,"h5sololauncher.CampaignAudio.dll","H5Audio",cancellation);
        byte[] Call(uint operation)
        {
            if(target.Find()!=running)throw new CacheException("FORGE_PROCESS_CHANGED","Forge restarted during campaign audio preparation.");
            var bytes=new byte[552];W(bytes,0,0x55413548);W(bytes,4,1);W(bytes,8,operation);BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16),running.Created);var result=session.Call(bytes,cancellation);
            if(U(result,12)!=0){var end=Array.IndexOf(result,(byte)0,40,512);throw new CacheException("CAMPAIGN_AUDIO_FAILED",Encoding.UTF8.GetString(result,40,(end<0?552:end)-40));}return result;
        }
        var observed=Call(0);if(activate && U(observed,24)==0)observed=Call(1);var began=DateTime.UtcNow;
        while(activate && U(observed,24)==1){if(DateTime.UtcNow-began>TimeSpan.FromSeconds(45))throw new CacheException("CAMPAIGN_AUDIO_TIMEOUT","Forge is still registering campaign audio. Its existing request was kept; close Forge before retrying.");if(cancellation.WaitHandle.WaitOne(200))cancellation.ThrowIfCancellationRequested();observed=Call(0);}
        return new(U(observed,24),U(observed,28),U(observed,32));
    }
    private static uint U(byte[] bytes,int at)=>BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
    private static void W(byte[] bytes,int at,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at),value);
}
