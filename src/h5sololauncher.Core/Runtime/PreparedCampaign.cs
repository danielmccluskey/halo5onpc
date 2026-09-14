using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Planning;
using H5SoloLauncher.Core.Preparation;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Core.Runtime;
public sealed record PreparedCampaign(int Format,string Rules,string SourceFingerprint,string PackageFullName,string SourcePlanId,string EffectivePlanId,
    string AssetsId,string ShadersId,string ModulesId,string AudioId,string CatalogueId,string MenuSourcesId,string ArtworkId,string MenuConfigId,string UiConfigId,string MovieConfigId,string CompletionConfigId,string DisplayConfigId);
public sealed record CampaignPlayRequest(PrepareRequest Locations,string PreparedId);
public sealed record CampaignPlayResult(string State,string Phase,string Message,int? ProcessId=null,string? Code=null,string? Details=null,long? ProcessCreated=null);
public sealed record CampaignWatchRequest(CampaignPlayRequest Campaign,int ProcessId,long ProcessCreated);
public interface ICampaignPlayWorker
{
    Task<CampaignPlayResult> PlayAsync(CampaignPlayRequest request,IProgress<IndexProgress> progress,CancellationToken cancellation);
}
public interface ICampaignWatchWorker
{
    Task<CampaignPlayResult> WatchAsync(CampaignWatchRequest request,IProgress<IndexProgress> progress,CancellationToken cancellation);
}
public static class PreparedCampaignStore
{
    public const string Rules="prepared-campaign-2";
    private sealed record Latest(string Id);
    public static string Save(InputRequest input,PreparedCampaign prepared)
    {
        var source=SourceDiscovery.Describe(input.SourceRoot);var cache=CacheFolders.Open(input.CacheRoot,source.Root,input.ForgeRoot);using var snapshot=new CatalogSnapshot(cache,source);
        BuildPlanStore.ReadVerified(cache,snapshot,input.PlanId);if(prepared.SourcePlanId!=input.PlanId || prepared.PackageFullName!=input.PackageFullName)throw InputFiles.Damaged();
        prepared=prepared with {SourceFingerprint=snapshot.Fingerprint};var id=InputFiles.Save(cache.Root,"prepared-manifests",prepared);
        InputFiles.Write(cache.Root,"prepared.json",System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new Latest(id)));return id;
    }
    public static PreparedCampaign Read(PrepareRequest input,string id)
    {
        var source=SourceDiscovery.Describe(input.SourceRoot);var cache=CacheFolders.Open(input.CacheRoot,source.Root,input.ForgeRoot);
        var prepared=InputFiles.Read<PreparedCampaign>(cache.Root,InputFiles.PathFor("prepared-manifests",id),128*1024,id);
        using var snapshot=new CatalogSnapshot(cache,source);BuildPlanStore.ReadVerified(cache,snapshot,prepared.SourcePlanId);
        if(prepared.Format!=1 || prepared.Rules!=Rules || prepared.SourceFingerprint!=snapshot.Fingerprint || prepared.PackageFullName!=input.PackageFullName)throw new CacheException("PREPARED_CACHE_CHANGED","The dump, Forge version or cache changed. Prepare the campaign again.");
        var plan=BuildPlanStore.ReadVerified(cache,snapshot,prepared.SourcePlanId);if(plan.Language!=input.Language)throw new CacheException("PREPARED_LANGUAGE_CHANGED","Prepare the campaign for the selected language first.");
        // Cheap configuration checks let the main button offer preparation again
        // before starting Forge when a small required cache file is missing.
        var configurations=new[]{("menu-config",prepared.MenuConfigId,16*1024*1024),
            ("ui-residency",prepared.UiConfigId,512*1024),("movie-config",prepared.MovieConfigId,16384),
            ("completion-config",prepared.CompletionConfigId,4*1024*1024),("display-config",prepared.DisplayConfigId,4*1024*1024)};
        foreach(var (category,configId,limit) in configurations)
        {
            using var config=File.OpenRead(ConfigPath(cache.Root,category,configId));
            if(config.Length<8 || config.Length>=limit || InputFiles.Digest(config,default)!=configId)
                throw new CacheException("PREPARED_CONFIG_DAMAGED","A required campaign configuration changed. Prepare the campaign again.");
        }
        var moviePath=ConfigPath(cache.Root,"movie-config",prepared.MovieConfigId);using var movie=File.OpenRead(moviePath);
        if(movie.Length>16384 || InputFiles.Digest(movie,default)!=prepared.MovieConfigId)throw InputFiles.Damaged();movie.Position=8;
        using var reader=new BinaryReader(movie,new System.Text.UTF8Encoding(false,true));
        string Text(){var length=reader.ReadInt32();if(length<=0 || length>movie.Length-movie.Position)throw InputFiles.Damaged();return new System.Text.UTF8Encoding(false,true).GetString(reader.ReadBytes(length));}
        if(Text()!=input.PackageFullName || !SafePaths.Canonical(Text()).Equals(cache.Root,StringComparison.OrdinalIgnoreCase))throw new CacheException("PREPARED_CACHE_MOVED","The cache moved. Prepare it again to refresh its game paths.");
        return prepared;
    }
    public static string ConfigPath(string root,string category,string id)=>SafePaths.Child(root,InputFiles.PathFor(category,id,".bin"));
    public static string? LatestId(PrepareRequest input)
    {
        try{var cache=CacheFolders.Open(input.CacheRoot,input.SourceRoot,input.ForgeRoot);var latest=InputFiles.Read<Latest>(cache.Root,"prepared.json",4096);Read(input,latest.Id);return latest.Id;}
        catch(Exception error) when(error is CacheException or IOException or System.Text.Json.JsonException or ArgumentException){return null;}
    }
}
