using System.Buffers.Binary;
using System.Text;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Runtime;

namespace H5SoloLauncher.Worker;

public sealed class CampaignContentRuntime(string forgeRoot,string package) : ICampaignContent
{
    public static uint Check(string forgeRoot,string package,CancellationToken cancellation)
    {
        var target=PackageTarget.Forge(forgeRoot,package);
        var running=target.Find()??throw new CacheException("FORGE_NOT_RUNNING","Forge has closed.");
        using var probe=PackageProcessSession.Attach(target,"h5sololauncher.CampaignRegistry.dll","H5Content",cancellation);
        var request=new byte[4720];W(request,0,0x35435448);W(request,4,1);W(request,8,4);
        BinaryPrimitives.WriteInt64LittleEndian(request.AsSpan(16),running.Created);
        var result=probe.Call(request,cancellation);
        if(U(result,12)!=0){var end=Array.IndexOf(result,(byte)0,40,512);throw new CacheException("CAMPAIGN_CONTENT_FAILED",Encoding.UTF8.GetString(result,40,(end<0?552:end)-40));}
        return U(result,24);
    }
    private PackageProcessSession? session;
    public void Activate(string configuration,string sha256,IProgress<IndexProgress> progress,CancellationToken cancellation)
    {
        var target=PackageTarget.Forge(forgeRoot,package);var running=target.Find()??throw new CacheException("FORGE_NOT_RUNNING","Forge closed before campaign content setup.");
        session=PackageProcessSession.Attach(target,"h5sololauncher.CampaignRegistry.dll","H5Content",cancellation);
        var path=Encoding.Unicode.GetBytes(configuration+'\0');
        if(path.Length>4096 || sha256.Length!=64 || sha256.Any(c=>!Uri.IsHexDigit(c)))throw new CacheException("CAMPAIGN_CONTENT_PATH","The campaign configuration path or digest exceeds its supported size.");
        byte[] Call(uint operation,CancellationToken token)
        {
            if(target.Find()!=running)throw new CacheException("FORGE_PROCESS_CHANGED","Forge restarted during campaign content setup.");
            var bytes=new byte[4720];W(bytes,0,0x35435448);W(bytes,4,1);W(bytes,8,operation);BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16),running.Created);
            path.CopyTo(bytes,552);Encoding.ASCII.GetBytes(sha256).CopyTo(bytes,4648);var result=session.Call(bytes,token);
            if(U(result,12)!=0){var end=Array.IndexOf(result,(byte)0,40,512);var message=Encoding.UTF8.GetString(result,40,(end<0?552:end)-40);throw new CacheException("CAMPAIGN_CONTENT_FAILED",string.IsNullOrWhiteSpace(message)?"Forge could not activate the campaign cache.":message);}
            return result;
        }
        var observed=Call(0,cancellation);if(U(observed,24)==0)observed=Call(1,cancellation);
        while(true)
        {
            var phase=U(observed,24);
            if(phase==4)return;
            if(phase==2)observed=Call(2,cancellation);
            else
            {
                progress.Report(new(phase==1?"Checking generated game files inside Forge":"Activating campaign mission routes",checked((int)U(observed,32)),checked((int)U(observed,28))));
                if(cancellation.WaitHandle.WaitOne(200))
                {
                    if(phase<=2)Call(3,CancellationToken.None);
                    cancellation.ThrowIfCancellationRequested();
                }
                observed=Call(0,cancellation);
            }
        }
    }
    private static uint U(byte[] bytes,int at)=>BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
    private static void W(byte[] bytes,int at,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at),value);
    public void Dispose(){session?.Dispose();session=null;}
}
