using System.Buffers.Binary;
using System.Text;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Runtime;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Worker;

public sealed class CampaignRegistry(string forgeRoot, string package, string cacheRoot) : ICampaignRegistry
{
    private PackageProcessSession? session;
    public CampaignMapMetadata[] Read(CampaignMapInput[] inputs, IProgress<IndexProgress> progress, CancellationToken cancellation)
    {
        if (inputs.Length != 19) throw new CacheException("CAMPAIGN_METADATA_INVALID","The campaign metadata input count is unsupported.");
        var target = PackageTarget.Forge(forgeRoot,package);
        var running = target.Find() ?? throw new CacheException("FORGE_NOT_RUNNING","Forge closed before campaign registration.");
        session = PackageProcessSession.Attach(target,"h5sololauncher.CampaignRegistry.dll","H5Registry",cancellation);
        byte[] Call(uint operation)
        {
            if (target.Find()!=running) throw new CacheException("FORGE_PROCESS_CHANGED","Forge restarted during campaign registration.");
            var bytes = new byte[552+32*4164+32*4112]; W(bytes,0,0x35524748); W(bytes,4,1); W(bytes,8,operation); W(bytes,28,(uint)inputs.Length);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16),running.Created);
            for (var i=0;i<inputs.Length;i++)
            {
                var path = Encoding.Unicode.GetBytes(SafePaths.Child(cacheRoot,inputs[i].CachePath)+'\0');
                if (path.Length>4096 || inputs[i].Sha256.Length!=64 || inputs[i].Sha256.Any(c=>!Uri.IsHexDigit(c))) throw new CacheException("CAMPAIGN_METADATA_PATH","The cache metadata path or digest exceeds its supported size.");
                path.CopyTo(bytes,552+i*4164); Encoding.ASCII.GetBytes(inputs[i].Sha256).CopyTo(bytes,552+i*4164+4096);
            }
            var result = session.Call(bytes,cancellation);
            if (U(result,12)!=0)
            {
                var end = Array.IndexOf(result,(byte)0,40,512); var message = Encoding.UTF8.GetString(result,40,(end<0?552:end)-40);
                throw new CacheException("CAMPAIGN_REGISTRY_FAILED",string.IsNullOrWhiteSpace(message)?"Forge could not register the campaign metadata.":message);
            }
            return result;
        }
        var observed = Call(0);
        var readyLimit = DateTime.UtcNow.AddSeconds(120);
        while (U(observed,24)==0 && U(observed,36)!=15)
        {
            progress.Report(new("Waiting for Forge's campaign registry",0,0));
            if(DateTime.UtcNow>readyLimit)throw new CacheException("CAMPAIGN_REGISTRY_WAIT","Forge did not finish initializing its campaign registry. Check its sign-in screen and retry startup.");
            if(cancellation.WaitHandle.WaitOne(200))cancellation.ThrowIfCancellationRequested();observed=Call(0);
        }
        if (U(observed,24)==0) observed=Call(1);
        var limit = DateTime.UtcNow.AddSeconds(45);
        while (U(observed,24)<3)
        {
            progress.Report(new("Registering campaign missions automatically",0,inputs.Length));
            if (DateTime.UtcNow>limit) throw new CacheException("CAMPAIGN_REGISTRY_TIMEOUT","Forge did not finish campaign registration. Restart it before another attempt.");
            if (cancellation.WaitHandle.WaitOne(100)) cancellation.ThrowIfCancellationRequested(); observed=Call(0);
        }
        if (U(observed,24)!=3 || U(observed,28)!=inputs.Length || U(observed,32)!=15 || U(observed,36)!=15) throw new CacheException("CAMPAIGN_REGISTRY_INCOMPLETE","Forge returned an incomplete campaign registry.");
        return Enumerable.Range(0,inputs.Length).Select(i=>
        {
            var at=552+32*4164+i*4112; var length=checked((int)U(observed,at+12));
            if (length is <=0 or >4096) throw new CacheException("CAMPAIGN_REGISTRY_INCOMPLETE","A native campaign module list exceeds its supported size.");
            return new CampaignMapMetadata(U(observed,at),checked((int)U(observed,at+4)),checked((int)U(observed,at+8)),observed.AsSpan(at+16,length).ToArray());
        }).ToArray();
    }
    private static uint U(byte[] bytes,int at)=>BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
    private static void W(byte[] bytes,int at,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at),value);
    public void Dispose() { session?.Dispose(); session=null; }
}
