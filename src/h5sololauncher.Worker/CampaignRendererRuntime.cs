using System.Buffers.Binary;
using System.Text;
using H5SoloLauncher.Core;

namespace H5SoloLauncher.Worker;

public sealed record CampaignRendererStatus(uint Phase,uint Callbacks,uint Batches,uint BlockedFrames,uint PublishedBanks,uint Fault,string Message="");
public static class CampaignRendererRuntime
{
    public static CampaignRendererStatus Run(string forgeRoot,string package,bool activate,CancellationToken cancellation)
    {
        var target=PackageTarget.Forge(forgeRoot,package);var running=target.Find()??throw new CacheException("FORGE_NOT_RUNNING","Forge closed before renderer preparation.");
        using var session=PackageProcessSession.Attach(target,"h5sololauncher.CampaignRenderer.dll","H5Renderer",cancellation);
        byte[] Call(uint operation)
        {
            if(target.Find()!=running)throw new CacheException("FORGE_PROCESS_CHANGED","Forge restarted during renderer preparation.");
            var bytes=new byte[560];W(bytes,0,0x44523548);W(bytes,4,1);W(bytes,8,operation);BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16),running.Created);var result=session.Call(bytes,cancellation);
            if(U(result,12)!=0){var end=Array.IndexOf(result,(byte)0,48,512);throw new CacheException("CAMPAIGN_RENDERER_FAILED",Encoding.UTF8.GetString(result,48,(end<0?560:end)-48));}return result;
        }
        var observed=Call(0);if(activate && U(observed,24)==0)observed=Call(1);
        var messageEnd=Array.IndexOf(observed,(byte)0,48,512);
        return new(U(observed,24),U(observed,28),U(observed,32),U(observed,36),U(observed,40),U(observed,44),Encoding.UTF8.GetString(observed,48,(messageEnd<0?560:messageEnd)-48));
    }
    private static uint U(byte[] bytes,int at)=>BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
    private static void W(byte[] bytes,int at,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at),value);
}
