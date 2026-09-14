using System.Text.Json;

namespace H5SoloLauncher.Core.Planning;

public static class ContentBundles
{
    public static IReadOnlyList<ContentBundle> All { get; } = Load();
    public static ContentBundle Get(string id) => All.SingleOrDefault(x => x.Id == id)
        ?? throw new CacheException("CONTENT_UNSUPPORTED", "This campaign bundle is not supported by the planner.");
    private static ContentBundle[] Load()
    {
        using var stream = typeof(ContentBundles).Assembly.GetManifestResourceStream("H5SoloLauncher.Core.Planning.Bundles.json")!;
        return JsonSerializer.Deserialize<ContentBundle[]>(stream)!;
    }
}
