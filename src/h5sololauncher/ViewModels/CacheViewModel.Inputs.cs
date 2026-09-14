using System.IO;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Preparation;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.ViewModels;

public sealed partial class CacheViewModel
{
    private InputSummary? cachedInputs;
    private InputResult? inputResult;
    public bool IsBuildingInputs { get; private set; }
    public bool CanBuildInputs => CanChoose && worker is IInputWorker && HasMatchingForge && summary?.State == "Indexed" &&
        summary.SourceRoot.Equals(source, StringComparison.OrdinalIgnoreCase);
    private bool HasMatchingInputs => HasMatchingForge && cachedInputs?.PlanId == planned?.PlanId &&
        cachedInputs?.PackageFullName == forgePackage && cachedInputs?.ForgeCatalogId == preparedForge?.CatalogId;
    public bool CanOpenInputs => !IsBusy && HasMatchingInputs;
    public string InputManifestPath => HasMatchingInputs ? SafePaths.Child(Directory, cachedInputs!.RelativePath) : string.Empty;
    public string InputStatus => IsBuildingInputs ? IsStopping ? "Pausing input caching…" : "Building conversion inputs…" : inputResult?.State switch
    {
        "Paused" => "Input caching paused.", "Failed" => "Input caching needs attention.",
        _ => HasMatchingInputs ? "Source tag inputs cached." : "Cache the tag data needed for conversion."
    };
    public string InputMessage => IsBuildingInputs ? $"{progress?.Stage ?? "Preparing the input cache…"}\n" +
        (progress?.Total > 0 ? $"{progress.Completed:N0} / {progress.Total:N0} items; {progress.Reused:N0} packs reused.\n{progress.File}" : string.Empty)
        : inputResult?.State is "Failed" or "Paused" ? inputResult.Message ?? string.Empty : HasMatchingInputs
        ? $"{cachedInputs!.Tags:N0} tags stored as {cachedInputs.UniquePayloads:N0} unique payloads in {cachedInputs.Packs:N0} pack files.\n" +
          $"{cachedInputs.PayloadBytes / (1024.0 * 1024):N1} MiB cached; {cachedInputs.ReusedPacks:N0} packs reused.\n" +
          $"{cachedInputs.SchemaHeaders:N0} source schema headers collected; {cachedInputs.NameMatches:N0} missing references have potential Forge equivalents supported by their names.\n" +
          "These are source inputs. Preparation selects layers, converts resources and validates the game output."
        : "This reads the planned tags from your dump and stores verified bytes, dependency names and schema headers in the chosen cache. Pause keeps completed packs. " +
          (HasMatchingPlan ? $"Allow up to {planned!.LogicalBytes / (1024.0 * 1024):N0} MiB for tag data, plus metadata and 256 MiB of free space. " : "Check dependencies and prepare Forge data first. ") +
          "Forge can be closed during this step.";
    public string InputDetails => $"Conversion inputs: {InputStatus}\n{InputMessage}\n" +
        (HasMatchingInputs ? $"Manifest: {InputManifestPath}\nManifest ID: {cachedInputs!.ManifestId}\nRules: {cachedInputs.Rules}\n" +
            $"Plan ID: {cachedInputs.PlanId}\nForge catalog: {cachedInputs.ForgeCatalogId}\nSource bytes read: {cachedInputs.SourceBytesRead}\n" +
            $"Named missing identities: {cachedInputs.NamedMissing}\nTags without a single schema root: {cachedInputs.TagsWithoutSingleRoot}\n" +
            $"Patch choices retained: {cachedInputs.PatchChoices}\nUnresolved resources retained: {cachedInputs.UnresolvedResources}\n" : string.Empty) +
        (inputResult?.Code is null ? string.Empty : $"Error: {inputResult.Code}\n{inputResult.Details}");
    private void ClearInputs() { cachedInputs = null; inputResult = null; }
    private async Task LoadInputsAsync()
    {
        ClearInputs(); if (!HasMatchingForge) return;
        try { cachedInputs = await Task.Run(() => InputCacheStore.ReadSummary(CacheFolders.Open(Directory, source, forge), planned!.PlanId, forgePackage, preparedForge!.CatalogId)); }
        catch (Exception e) { inputResult = InputFailure(e); }
    }
    public Task BuildInputsAsync()
    {
        if (!CanBuildInputs) return Task.CompletedTask;
        active = BuildInputsCoreAsync(); return active;
    }
    private async Task BuildInputsCoreAsync()
    {
        stop = new(); IsBusy = true; IsBuildingInputs = true; IsStopping = false; inputResult = null; notice = string.Empty; progress = null; Refresh();
        try
        {
            inputResult = await ((IInputWorker)worker).BuildInputsAsync(new(source, Directory, forge, forgePackage, planned!.PlanId),
                new Progress<IndexProgress>(value => { progress = value; Refresh(); }), stop.Token);
            if (inputResult.Summary is not null) cachedInputs = inputResult.Summary;
            FreeBytes = new DriveInfo(Path.GetPathRoot(Directory)!).AvailableFreeSpace;
        }
        catch (Exception e) { inputResult = InputFailure(e); }
        finally { stop.Dispose(); stop = null; IsBuildingInputs = false; IsBusy = false; IsStopping = false; progress = null; Refresh(); }
    }
    private static InputResult InputFailure(Exception e) => new("Failed", Code: e is CacheException known ? known.Code : "INPUT_CACHE_FAILED", Message: e.Message, Details: e.ToString());
}
