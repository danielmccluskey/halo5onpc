using System.Buffers.Binary;
using System.Text;
using H5SoloLauncher.Core;
namespace H5SoloLauncher.Worker;
public sealed record CampaignDeformationStatus(uint Phase,uint Draws,uint Errors,string Message);
public static class CampaignDeformationRuntime
{
    public static CampaignDeformationStatus Run(string forgeRoot,string package,bool activate,CancellationToken cancellation,string helper="h5sololauncher.CampaignDeformation.dll")
    {
        var target=PackageTarget.Forge(forgeRoot,package);var running=target.Find()??throw new CacheException("FORGE_NOT_RUNNING","Forge closed before facial deformation preparation.");
        if(Path.GetFileName(helper)!=helper || !helper.StartsWith("h5sololauncher.CampaignDeformation",StringComparison.Ordinal) || !helper.EndsWith(".dll",StringComparison.Ordinal))throw new ArgumentException("Invalid deformation helper name.",nameof(helper));
        using var session=PackageProcessSession.Attach(target,helper,"H5Deformation",cancellation);
        byte[] Call(uint operation)
        {
            if(target.Find()!=running)throw new CacheException("FORGE_PROCESS_CHANGED","Forge restarted during facial deformation preparation.");
            var bytes=new byte[552];W(bytes,0,0x46443548);W(bytes,4,1);W(bytes,8,operation);BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16),running.Created);var result=session.Call(bytes,cancellation);
            if(U(result,12)!=0)throw new CacheException("CAMPAIGN_DEFORMATION_FAILED",Message(result));return result;
        }
        var observed=Call(0);if(activate && U(observed,24)==0)observed=Call(1);
        return new(U(observed,24),U(observed,28),U(observed,32),Message(observed));
    }
    private static string Message(byte[] bytes){var end=Array.IndexOf(bytes,(byte)0,40,512);return Encoding.UTF8.GetString(bytes,40,(end<0?552:end)-40);}
    private static uint U(byte[] bytes,int at)=>BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
    private static void W(byte[] bytes,int at,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at),value);
}
