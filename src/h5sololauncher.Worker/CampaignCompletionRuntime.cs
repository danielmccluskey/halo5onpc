using System.Buffers.Binary;
using System.Text;
using H5SoloLauncher.Core;

namespace H5SoloLauncher.Worker;

public sealed record CampaignCompletionStatus(uint Phase,uint Installed,uint Events,uint Map,string Message);
public static class CampaignCompletionRuntime
{
    public static CampaignCompletionStatus Run(string forgeRoot,string package,string configuration,string digest,uint operation,CancellationToken cancellation)
    {
        var target=PackageTarget.Forge(forgeRoot,package);var running=target.Find()??throw new CacheException("FORGE_NOT_RUNNING","Forge closed before mission completion setup.");
        using var session=PackageProcessSession.Attach(target,"h5sololauncher.CampaignCompletion.dll","H5Completion",cancellation);
        var path=Encoding.Unicode.GetBytes(configuration+'\0');if(path.Length>4096 || digest.Length!=64 || digest.Any(c=>!Uri.IsHexDigit(c)))throw new CacheException("CAMPAIGN_COMPLETION_PATH","The mission completion path or digest is invalid.");
        byte[] Call(uint operation)
        {
            if(target.Find()!=running)throw new CacheException("FORGE_PROCESS_CHANGED","Forge restarted during mission completion setup.");
            var bytes=new byte[4720];W(bytes,0,0x52503548);W(bytes,4,1);W(bytes,8,operation);BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16),running.Created);
            path.CopyTo(bytes,552);Encoding.ASCII.GetBytes(digest).CopyTo(bytes,4648);var result=session.Call(bytes,cancellation);
            if(U(result,12)!=0){var end=Array.IndexOf(result,(byte)0,40,512);throw new CacheException("CAMPAIGN_COMPLETION_FAILED",Encoding.UTF8.GetString(result,40,(end<0?552:end)-40));}return result;
        }
        var observed=Call(0);if(operation==1 && U(observed,24)==0)observed=Call(1);else if(operation==2)observed=Call(2);
        var end=Array.IndexOf(observed,(byte)0,40,512);
        return new(U(observed,24),U(observed,28),U(observed,32),U(observed,36),Encoding.UTF8.GetString(observed,40,(end<0?552:end)-40));
    }
    private static uint U(byte[] bytes,int at)=>BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
    private static void W(byte[] bytes,int at,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at),value);
}
