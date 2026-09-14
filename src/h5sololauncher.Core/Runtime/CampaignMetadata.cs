using System.Text;
using System.Text.Json;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Conversion;
using H5SoloLauncher.Core.Forge;
using H5SoloLauncher.Core.Planning;
using H5SoloLauncher.Core.Preparation;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Core.Runtime;

public sealed record CampaignMapInput(string SourcePath, string CachePath, string Sha256);
public sealed record CampaignMapMetadata(uint MapId, int Mission, int Sublevel, byte[] Modules);
public interface ICampaignRegistry : IDisposable
{
    CampaignMapMetadata[] Read(CampaignMapInput[] inputs, IProgress<IndexProgress> progress, CancellationToken cancellation);
}
public sealed record CampaignRoute(uint MapId, int Mission, int Sublevel, uint NextMapId, string Scenario, string MetadataSha256,
    byte[] SourceModules, byte[] TargetModules, string[] ModulePaths, bool Available);
public sealed record CampaignCatalogue(int Format, string Rules, string SourceFingerprint, string PackageFullName, string ModulesId, CampaignRoute[] Maps);
public sealed record CampaignMetadataRequest(InputRequest Inputs, string ModulesId);
public sealed record CampaignMetadataResult(string State, string? ManifestId = null, int Maps = 0, int Available = 0, string? Code = null, string? Message = null, string? Details = null);

public static class CampaignMetadata
{
    public const string Rules = "campaign-catalogue-1";
    public static CampaignMetadataResult Run(CampaignMetadataRequest request, Func<ICampaignRegistry> readerFactory, IProgress<IndexProgress> progress, CancellationToken cancellation)
    {
        try
        {
            var input = request.Inputs; var source = SourceDiscovery.Describe(input.SourceRoot); var cache = CacheFolders.Open(input.CacheRoot, source.Root, input.ForgeRoot);
            using var lease = new FileStream(SafePaths.Child(cache.Root,"index.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            using var snapshot = new CatalogSnapshot(cache,source); var plan = BuildPlanStore.ReadVerified(cache,snapshot,input.PlanId);
            var modules = InputFiles.Read<AssembledCampaign>(cache.Root,InputFiles.PathFor("game-manifests",request.ModulesId),4*1024*1024,request.ModulesId);
            if (modules.Format != 1 || modules.Rules != ModuleAssembly.Rules || modules.PackageFullName != input.PackageFullName) throw InputFiles.Damaged();
            var effective = InputFiles.Read<EffectivePlan>(cache.Root,InputFiles.PathFor("effective-plans",modules.EffectivePlanId),128L*1024*1024,modules.EffectivePlanId);
            if (effective.SourcePlanId != input.PlanId || effective.InputFingerprint != snapshot.Fingerprint) throw InputFiles.Damaged();
            var native = InputFiles.Read<NativeModuleManifest>(cache.Root,InputFiles.PathFor("native-manifests",effective.NativeManifestId),1024*1024,effective.NativeManifestId);
            var inputs = Stage(source.Root,cache.Root,cancellation);
            using var reader = readerFactory(); var metadata = reader.Read(inputs,progress,cancellation);
            var routes = Build(inputs,metadata,modules,native,plan.Bundle.Scenarios);
            var catalogue = new CampaignCatalogue(1,Rules,snapshot.Fingerprint,input.PackageFullName,request.ModulesId,routes);
            var id = InputFiles.Save(cache.Root,"catalogue-manifests",catalogue);
            return new("Registered",id,routes.Length,routes.Count(x=>x.Available),Message:"Campaign metadata registered automatically. Runtime preparation is still required before Play.");
        }
        catch (OperationCanceledException) { return new("Paused",Message:"Campaign registration paused. Any queued native operation remains owned by Forge."); }
        catch (Exception error) { return new("Failed",Code:error is CacheException known ? known.Code : "CAMPAIGN_REGISTRY_FAILED",Message:error.Message,Details:error.ToString()); }
    }
    public static CampaignMapInput[] Stage(string source, string cache, CancellationToken cancellation)
    {
        var root = SafePaths.Child(source,"__cms__/rtx/levels"); List<string> files = []; var visited = 0;
        var pending = new Stack<string>(Directory.EnumerateDirectories(root).Where(p=>Path.GetFileName(p).StartsWith("campaignworld",StringComparison.Ordinal) || Path.GetFileName(p)=="cinematics"));
        while (pending.TryPop(out var directory))
        {
            cancellation.ThrowIfCancellationRequested(); SafePaths.NoLinks(directory);
            if (++visited > 4096) throw Invalid("The source metadata tree exceeds its supported size.");
            foreach (var child in Directory.EnumerateDirectories(directory)) pending.Push(child);
            foreach (var file in Directory.EnumerateFiles(directory,"*.mapinfo")) { SafePaths.NoLinks(file); files.Add(file); }
            if (files.Count > 32) throw Invalid("The dump contains too many campaign map files for this conversion.");
        }
        if (files.Count != 19) throw Invalid("The extracted dump must contain all 19 campaign and cinematic mapinfo files.");
        List<CampaignMapInput> result = [];
        foreach (var file in files.Order(StringComparer.Ordinal))
        {
            cancellation.ThrowIfCancellationRequested(); using var stream = File.OpenRead(file);
            if (stream.Length is <=0 or >1024*1024) throw Invalid("A source mapinfo file is empty or too large.");
            var bytes = new byte[stream.Length]; stream.ReadExactly(bytes); var digest = InputFiles.Hash(bytes);
            var relative = "game/metadata/"+digest.ToLowerInvariant()+".mapinfo"; var path = SafePaths.Child(cache,relative);
            // Preserve an already opened native input. The game holds read-only handles until import completes.
            if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) InputFiles.Write(cache,relative,bytes);
            result.Add(new(Path.GetRelativePath(source,file).Replace('\\','/'),relative,digest));
        }
        return result.ToArray();
    }
    public static CampaignRoute[] Build(CampaignMapInput[] inputs, CampaignMapMetadata[] maps, AssembledCampaign assembled, NativeModuleManifest native, string[] requiredScenarios)
    {
        if (inputs.Length != 19 || maps.Length != inputs.Length || maps.Select(x=>x.MapId).Distinct().Count()!=maps.Length || maps.Select(x=>(x.Mission,x.Sublevel)).Distinct().Count()!=maps.Length)
            throw Invalid("Forge returned an incomplete or ambiguous campaign catalogue.");
        var modulePaths = assembled.Modules.Select(x=>x.OriginalPath.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var globalPaths = native.Modules.Select(x=>x.Path).Concat(assembled.Modules.Where(x=>x.SharedBanks).Select(x=>x.OriginalPath)).ToArray();
        List<CampaignRoute> routes = [];
        for (var i=0;i<maps.Length;i++)
        {
            var map = maps[i]; var parts = ModuleList(map.Modules);
            if (map.Mission is <0 or >=15 || map.Sublevel is <0 or >2 || !parts[2].StartsWith("?any\\levels\\",StringComparison.Ordinal) || !parts[2].EndsWith("-rtx-",StringComparison.Ordinal)) throw Invalid("A campaign map has an invalid scenario or mission slot.");
            var scenario = parts[2][5..^5].Replace('\\','/');
            if (inputs[i].SourcePath != "__cms__/rtx/"+scenario+".mapinfo") throw Invalid("The parsed map does not match its source file.");
            if (parts[0] != "?any\\levels\\globals-rtx-" || parts[1] != "?%(Platform)\\levels\\globals-rtx-" || parts[3] != "?%(Platform)\\"+scenario.Replace('/','\\')+"-rtx-") throw Invalid("A campaign map uses unsupported module prefixes.");
            string PathFor(string reference) => "deploy/"+(parts[reference[1]-'0'][1..]+reference[3..]+".module").Replace("%(Platform)","pc").Replace('\\','/');
            var required = parts.Skip(4).Where(x=>x[1] is '2' or '3').Select(PathFor).ToArray();
            var available = required.Length>0 && required.All(x=>modulePaths.Contains(x.ToLowerInvariant()));
            var selected = new List<string>(parts.Take(4));
            foreach (var global in globalPaths.OrderBy(GlobalOrder))
            {
                var platform = global.StartsWith("deploy/any/",StringComparison.Ordinal) ? 0 : global.StartsWith("deploy/pc/",StringComparison.Ordinal) ? 1 : throw Invalid("An installed global module has an unknown platform.");
                var name = Path.GetFileName(global); if (!name.StartsWith("globals-rtx-",StringComparison.Ordinal) || !name.EndsWith(".module",StringComparison.Ordinal)) throw Invalid("A native global module has an unexpected name.");
                selected.Add($"<{platform}>"+name[12..^7]);
            }
            selected.AddRange(parts.Skip(4).Where(x=>x[1] is '2' or '3'));
            var target = Encoding.ASCII.GetBytes(string.Join('\0',selected)+'\0'); ModuleList(target);
            routes.Add(new(map.MapId,map.Mission,map.Sublevel,uint.MaxValue,scenario,inputs[i].Sha256,map.Modules,target,required,available));
        }
        var ordered = routes.OrderBy(x=>x.Mission).ThenBy(x=>x.Sublevel).ToArray();
        for (var i=0;i<ordered.Length-1;i++) ordered[i]=ordered[i] with {NextMapId=ordered[i+1].MapId};
        if (!ordered.Where(x=>x.Available).Select(x=>x.Scenario).ToHashSet(StringComparer.Ordinal).SetEquals(requiredScenarios)) throw Invalid("The generated modules do not cover exactly the requested campaign maps.");
        return ordered;
    }
    internal static string[] ModuleList(byte[] bytes)
    {
        if (bytes.Length is <8 or >4096 || bytes[^1]!=0 || bytes.AsSpan(0,bytes.Length-1).ContainsAnyExceptInRange((byte)0,(byte)127)) throw Invalid("A campaign module list has an invalid encoding or size.");
        var parts = Encoding.ASCII.GetString(bytes.AsSpan(0,bytes.Length-1)).Split('\0');
        if (parts.Length<5 || parts.Take(4).Any(x=>!x.StartsWith('?') || x.Contains("..") || x.Contains(':')) || parts.Skip(4).Any(x=>x.Length<4 || x[0]!='<' || x[1] is <'0' or >'3' || x[2]!='>' || x[3..].Any(c=>!char.IsAsciiDigit(c)&&c!='-')) || parts.Distinct().Count()!=parts.Length)
            throw Invalid("A campaign module list contains invalid or duplicate references.");
        return parts;
    }
    private static (int Version, int Revision, int Platform) GlobalOrder(string path)
    {
        var name=Path.GetFileName(path);
        if (!name.StartsWith("globals-rtx-",StringComparison.Ordinal) || !name.EndsWith(".module",StringComparison.Ordinal)) throw Invalid("A native global module has an unexpected name.");
        var parts=name[12..^7].Split('-');
        if (parts.Length is <1 or >2 || !int.TryParse(parts[0],out var version) || version<0 || (parts.Length==2 && (!int.TryParse(parts[1],out _) || int.Parse(parts[1])<0))) throw Invalid("A native global module has an invalid revision.");
        return (version,parts.Length==2?int.Parse(parts[1]):0,path.StartsWith("deploy/any/",StringComparison.Ordinal)?0:1);
    }
    private static CacheException Invalid(string message) => new("CAMPAIGN_METADATA_INVALID",message);
}
