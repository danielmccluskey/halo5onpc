using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Forge;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.ViewModels;

public sealed partial class CacheViewModel
{
    private string forgePackage = string.Empty;
    private ForgeSummary? preparedForge;
    private ForgeResult? forgeResult;
    public bool IsPreparingForge { get; private set; }
    public bool CanPrepareForge => CanChoose && worker is IForgeWorker && HasMatchingPlan &&
        summary?.State == "Indexed" && summary.SourceRoot.Equals(source, StringComparison.OrdinalIgnoreCase) && forgePackage.Length > 0;
    private bool HasMatchingForge => HasMatchingPlan && preparedForge?.PlanId == planned?.PlanId && preparedForge?.PackageFullName == forgePackage;
    public bool CanOpenForgeReport => !IsBusy && HasMatchingForge;
    public bool NeedsForgeCacheAccess => HasMatchingForge && preparedForge!.Probe.State == "Denied";
    public bool CanAllowForgeCacheRead => CanPrepareForge && NeedsForgeCacheAccess;
    public bool NeedsHaloApp => forgeResult?.Code == "HALO_APP_MISSING";
    public string ForgeCacheAccessMessage => "This gives Halo 5 Forge read access to the selected cache folder and its contents, then repeats preparation. The permission stays with this cache. Your dump and the Forge installation are outside this change.";
    public string ForgeReportPath => HasMatchingForge ? SafePaths.Child(Directory, preparedForge!.RelativePath) : string.Empty;
    public string ForgeStatus => IsPreparingForge ? "Preparing Forge data…" : forgeResult?.State switch
    {
        "Failed" => "Forge preparation needs attention.",
        "Paused" => "Forge preparation paused.",
        _ => NeedsForgeCacheAccess ? "Forge data saved — cache access needs attention." : HasMatchingForge ? "Forge table comparison saved." : "Prepare Forge’s global data."
    };
    public string ForgeMessage => forgeResult?.State is "Failed" or "Paused" ? forgeResult.Message ?? string.Empty : HasMatchingForge
        ? $"{preparedForge!.Modules:N0} modules and {preparedForge.Tags:N0} native tags checked.\n" +
          $"{preparedForge.NativeMatches:N0} of {preparedForge.MissingIdentities:N0} distinct missing references have native candidates; {preparedForge.Unmatched:N0} remain unmatched.\n" +
          (preparedForge.DifferentAssetIds > 0 ? $"{preparedForge.DifferentAssetIds:N0} unmatched references share a group/tag ID with Forge but have different asset IDs. They still need investigation.\n" : string.Empty) +
          $"Cache access: {preparedForge.Probe.State}. {preparedForge.Probe.Message}\n" +
          "This table report is an input to preparation. The campaign preparation status reports the converted cache."
        : "This starts Forge when needed, reads its global tag tables and checks access to your cache folder. The Halo app may appear during startup. No campaign launch yet.";
    public string ForgeDetails => $"Forge preparation: {ForgeStatus}\n{ForgeMessage}\n" +
        (HasMatchingForge ? $"Report: {ForgeReportPath}\nReport ID: {preparedForge!.ReportId}\nForge package: {preparedForge.PackageFullName}\n" +
            $"Catalog ID: {preparedForge.CatalogId}\nReader: {preparedForge.ReaderMode}\nTable bytes: {preparedForge.TableBytes}\nReused modules: {preparedForge.ReusedModules}\nProbe error: {preparedForge.Probe.Code}\n{preparedForge.Probe.Details}\n" : string.Empty) +
        (forgeResult?.Code is null ? string.Empty : $"Error: {forgeResult.Code}\n{forgeResult.Details}");
    private void ClearForge() { preparedForge = null; forgeResult = null; ClearInputs(); }
    private async Task LoadForgeAsync()
    {
        ClearForge(); if (!HasMatchingPlan || forgePackage.Length == 0) return;
        try
        {
            preparedForge = await Task.Run(() => ForgeReportStore.Read(CacheFolders.Open(Directory, source, forge), forgePackage, planned!.PlanId));
            await LoadInputsAsync();
        }
        catch (Exception e) { forgeResult = ForgeFailure(e); }
    }
    public Task PrepareForgeAsync()
    {
        if (!CanPrepareForge) return Task.CompletedTask;
        active = PrepareForgeCoreAsync(false); return active;
    }
    public Task AllowForgeCacheReadAsync()
    {
        if (!CanAllowForgeCacheRead) return Task.CompletedTask;
        active = PrepareForgeCoreAsync(true); return active;
    }
    private async Task PrepareForgeCoreAsync(bool allowCacheRead)
    {
        stop = new(); IsBusy = true; IsPreparingForge = true; IsStopping = false; forgeResult = null; notice = string.Empty; progress = null; Refresh();
        try
        {
            forgeResult = await ((IForgeWorker)worker).PrepareForgeAsync(new(source, Directory, forge, forgePackage, planned!.PlanId, allowCacheRead),
                new Progress<IndexProgress>(value => { progress = value; Refresh(); }), stop.Token);
            if (forgeResult.Summary is not null) preparedForge = forgeResult.Summary;
            await LoadInputsAsync();
        }
        catch (Exception e) { forgeResult = ForgeFailure(e); }
        finally { stop.Dispose(); stop = null; IsBusy = false; IsPreparingForge = false; IsStopping = false; progress = null; Refresh(); }
    }
    private static ForgeResult ForgeFailure(Exception e) => new("Failed", Code: e is CacheException known ? known.Code : "FORGE_PREPARATION_FAILED", Message: e.Message, Details: e.ToString());
}
