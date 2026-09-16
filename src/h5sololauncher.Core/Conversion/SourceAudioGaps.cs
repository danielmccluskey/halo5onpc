using System.Text.Json;
namespace H5SoloLauncher.Core.Conversion;

public sealed record SourceAudioGap(uint Id,string Name,string TagSha256);
public sealed record SourceAudioGapProfile(string SourceVersion,SourceAudioGap[] Banks);

// These original scenario sound constants reference banks absent from every
// source language/patch package and Forge. Preserve that absence, never synthesize
// a bank or substitute another voice bank. Unexpected gaps remain hard failures.
public static class SourceAudioGaps
{
    public const string Rules="source-audio-gaps-1";
    public static SourceAudioGap[] Resolve(string version,uint[] missing,IEnumerable<ConvertedAsset> tags)
    {
        using var stream=typeof(SourceAudioGaps).Assembly.GetManifestResourceStream("H5SoloLauncher.Core.Conversion.audio-source-gaps.json")!;
        var profile=JsonSerializer.Deserialize<SourceAudioGapProfile>(stream)!;
        var assets=tags.ToArray();List<SourceAudioGap> result=[];
        foreach(var id in missing.Order())
        {
            var gap=profile.SourceVersion==version?profile.Banks.SingleOrDefault(x=>x.Id==id):null;
            if(gap is null || !assets.Any(x=>x.Sha256==gap.TagSha256 && x.Name.Replace('\\','/').EndsWith("/"+gap.Name+".soundbank",StringComparison.Ordinal)))
                throw new CacheException("AUDIO_BANK_MISSING","The dump is missing an unreviewed required campaign audio bank: "+id);
            result.Add(gap);
        }
        return result.ToArray();
    }
}
