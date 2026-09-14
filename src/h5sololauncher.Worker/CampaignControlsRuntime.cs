using System.Buffers.Binary;
using System.Text;
using H5SoloLauncher.Core;

namespace H5SoloLauncher.Worker;

public sealed record CampaignControlsStatus(uint Phase,uint Actions,uint Skulls);
public static class CampaignControlsRuntime
{
    public static CampaignControlsStatus Run(string forgeRoot,string package,bool activate,CancellationToken cancellation)
    {
        var target=PackageTarget.Forge(forgeRoot,package);var running=target.Find()??throw new CacheException("FORGE_NOT_RUNNING","Forge closed before campaign control preparation.");
        using var session=PackageProcessSession.Attach(target,"h5sololauncher.CampaignControls.dll","H5Controls",cancellation);
        byte[] Call(uint operation)
        {
            if(target.Find()!=running)throw new CacheException("FORGE_PROCESS_CHANGED","Forge restarted during campaign control preparation.");
            var bytes=new byte[552];W(bytes,0,0x52433548);W(bytes,4,1);W(bytes,8,operation);BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16),running.Created);var result=session.Call(bytes,cancellation);
            if(U(result,12)!=0){var end=Array.IndexOf(result,(byte)0,40,512);throw new CacheException("CAMPAIGN_CONTROLS_FAILED",Encoding.UTF8.GetString(result,40,(end<0?552:end)-40));}return result;
        }
        var observed=Call(0);if(activate && U(observed,24)==0)observed=Call(1);
        return new(U(observed,24),U(observed,28),U(observed,32));
    }
    private static uint U(byte[] bytes,int at)=>BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
    private static void W(byte[] bytes,int at,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at),value);
}
