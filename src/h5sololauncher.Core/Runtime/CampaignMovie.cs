using System.Buffers.Binary;
using System.Text;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Preparation;
using H5SoloLauncher.Core.Storage;
using H5SoloLauncher.Core.Planning;

namespace H5SoloLauncher.Core.Runtime;
public sealed record CampaignMovieResult(string State,string? ConfigId=null,long Bytes=0,string? Code=null,string? Message=null,string? Details=null);
public static class CampaignMovie
{
    public static CampaignMovieResult Prepare(InputRequest input,IProgress<IndexProgress> progress,CancellationToken cancellation)
    {
        string? temporary=null;
        try
        {
            var source=SourceDiscovery.Describe(input.SourceRoot);var cache=CacheFolders.Open(input.CacheRoot,source.Root,input.ForgeRoot);
            using var lease=new FileStream(SafePaths.Child(cache.Root,"index.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            using var snapshot=new CatalogSnapshot(cache,source);BuildPlanStore.ReadVerified(cache,snapshot,input.PlanId);
            using var stream=File.OpenRead(SafePaths.Child(source.Root,"bink/cin_010_halsey_60.bk2"));
            var header=new byte[44];stream.ReadExactly(header);
            if(!header.AsSpan(0,4).SequenceEqual("KB2i"u8) || stream.Length!=355451652 || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8))!=7966)
                throw new CacheException("CAMPAIGN_MOVIE_UNSUPPORTED","This dump uses an opening movie that needs additional playback support.");
            stream.Position=0;progress.Report(new("Checking the campaign opening movie",0,0));var sha=InputFiles.Digest(stream,cancellation);
            var relative="game/movies/"+sha.ToLowerInvariant()+"/cin_010_halsey_60.bk2";var target=SafePaths.Child(cache.Root,relative);var reusable=false;
            if(File.Exists(target)){using var saved=File.OpenRead(target);reusable=saved.Length==stream.Length && InputFiles.Digest(saved,cancellation)==sha;}
            if(!reusable)
            {
                if(new DriveInfo(Path.GetPathRoot(cache.Root)!).AvailableFreeSpace<stream.Length+128L*1024*1024)throw new CacheException("CAMPAIGN_MOVIE_SPACE","The cache drive needs another 470 MiB free for the opening movie.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);temporary=target+"."+Guid.NewGuid().ToString("N")+".tmp";stream.Position=0;
                using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,1024*1024))
                {
                    var buffer=new byte[1024*1024];int read;while((read=stream.Read(buffer))>0){cancellation.ThrowIfCancellationRequested();output.Write(buffer,0,read);progress.Report(new("Copying the campaign opening movie",checked((int)(output.Position/1048576)),checked((int)(stream.Length/1048576))));}output.Flush(true);
                }
                using(var verify=File.OpenRead(temporary))if(InputFiles.Digest(verify,cancellation)!=sha)throw new CacheException("CAMPAIGN_MOVIE_VERIFY","The opening movie failed verification after copying. Check the cache drive and retry.");
                File.Move(temporary,target,true);temporary=null;
            }
            using var wire=new MemoryStream();using var writer=new BinaryWriter(wire,Encoding.UTF8,true);
            void Text(string value){var bytes=Encoding.UTF8.GetBytes(value);writer.Write(bytes.Length);writer.Write(bytes);}
            writer.Write(0x564d3548u);writer.Write(1);Text(input.PackageFullName);Text(cache.Root);Text(relative);writer.Write(stream.Length);writer.Write(32);writer.Write(Convert.FromHexString(sha));writer.Flush();
            var bytes=wire.ToArray();var id=InputFiles.Hash(bytes);InputFiles.Write(cache.Root,InputFiles.PathFor("movie-config",id,".bin"),bytes);
            return new("Prepared",id,stream.Length,Message:"The campaign opening movie is cached and verified.");
        }
        catch(OperationCanceledException){return new("Paused",Message:"Opening movie preparation paused.");}
        catch(Exception error){return new("Failed",Code:error is CacheException known?known.Code:"CAMPAIGN_MOVIE_FAILED",Message:error.Message,Details:error.ToString());}
        finally{if(temporary is not null && File.Exists(temporary))File.Delete(temporary);}
    }
}
