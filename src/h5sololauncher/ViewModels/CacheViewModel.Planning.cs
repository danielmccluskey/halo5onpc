using System.IO;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Planning;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.ViewModels;

public sealed partial class CacheViewModel
{
    private PlanSummary? planned;
    private PlanResult? planResult;
    private string selectedLanguage = string.Empty;
    public string[] Languages { get; private set; } = [];
    public bool IsPlanning { get; private set; }
    public bool CanChooseLanguage => contextReady && CanChoose && Languages.Length > 0;
    public bool CanPlan => CanChooseLanguage && worker is IPlanWorker && summary?.State == "Indexed" &&
        summary.SourceRoot.Equals(source, StringComparison.OrdinalIgnoreCase) && Languages.Contains(SelectedLanguage, StringComparer.Ordinal);
    public string SelectedLanguage
    {
        get => selectedLanguage;
        set { if (IsBusy || value == selectedLanguage) return; selectedLanguage = value ?? string.Empty; planResult = null;ClearPrepared();Refresh();RefreshPrepared(); }
    }
    private bool HasMatchingPlan => planned is not null && planned.Language == SelectedLanguage && planned.BundleId == "full-campaign";
    public bool CanOpenPlan => !IsBusy && HasMatchingPlan;
    public string PlanPath => HasMatchingPlan ? SafePaths.Child(Directory, planned!.RelativePath) : string.Empty;
    public string PlanStatus => IsPlanning ? "Checking campaign dependencies…" : planResult?.State switch
    {
        "Failed" => "Dependency check needs attention.",
        "Paused" => "Dependency check paused.",
        _ => HasMatchingPlan ? "Source dependency plan saved." : "Check the files needed for Osiris through Guardians."
    };
    public string PlanMessage => planResult?.State is "Failed" or "Paused" ? planResult.Message ?? string.Empty : HasMatchingPlan
        ? $"{planned!.RootModules:N0} campaign modules, {planned.Tags:N0} stored tags and {planned.DependencyReferences:N0} references checked.\n" +
          $"{planned.MissingReferences:N0} unmatched references, {planned.PatchChoices:N0} patch choices and {planned.UnresolvedResources:N0} resources to resolve.\n" +
          $"{planned.Issues:N0} source issues were passed to the conversion stages.\n" +
          "This report records the original source analysis. Preparation resolves the campaign's layers and checks the converted outputs."
        : "This reads real tag references from the indexed dump and saves a reusable source plan. The connecting cinematic is included. No game files are converted in this step.";
    public string PlanDetails => $"Dependency plan: {PlanStatus}\nLanguage: {SelectedLanguage}\n{PlanMessage}\n" +
        (HasMatchingPlan ? $"Plan file: {PlanPath}\nPlan ID: {planned!.PlanId}\nSource fingerprint: {planned.InputFingerprint}\nStored tag bytes read: {planned.StoredBytesRead}\nReused analyses: {planned.ReusedAnalyses}\n" : string.Empty) +
        (planResult?.Code is null ? string.Empty : $"Error: {planResult.Code}\n{planResult.Details}");

    private void ClearPlan() { planned = null; planResult = null; Languages = []; ClearForge(); }
    private async Task LoadPlanAsync()
    {
        if (!contextReady || summary?.State != "Indexed") return;
        try
        {
            var options = await Task.Run(() => BuildPlanner.Inspect(Directory, source, forge));
            Languages = options.Languages.Where(x=>x=="English(US)").ToArray(); planned = options.SavedPlan;
            selectedLanguage = planned is not null && Languages.Contains(planned.Language,StringComparer.Ordinal)?planned.Language:(Languages.Contains(selectedLanguage, StringComparer.Ordinal) ? selectedLanguage :
                Languages.FirstOrDefault(x => x == "English(US)") ?? Languages.FirstOrDefault() ?? string.Empty);
            await LoadForgeAsync();
        }
        catch (Exception e) { planResult = PlanFailure(e); }
    }
    public Task PlanAsync()
    {
        if (!CanPlan) return Task.CompletedTask;
        active = PlanCoreAsync(); return active;
    }
    private async Task PlanCoreAsync()
    {
        stop = new(); IsBusy = true; IsPlanning = true; IsStopping = false; planResult = null; notice = string.Empty; progress = null; ClearForge(); Refresh();
        try
        {
            planResult = await ((IPlanWorker)worker).PlanAsync(new(source, Directory, forge, Language: SelectedLanguage),
                new Progress<IndexProgress>(value => { progress = value; Refresh(); }), stop.Token);
            if (planResult.Summary is not null) planned = planResult.Summary;
            await LoadForgeAsync();
            FreeBytes = new DriveInfo(Path.GetPathRoot(Directory)!).AvailableFreeSpace;
        }
        catch (Exception e) { planResult = PlanFailure(e); }
        finally { stop.Dispose(); stop = null; IsPlanning = false; IsBusy = false; IsStopping = false; progress = null; Refresh(); }
    }
    private static PlanResult PlanFailure(Exception e) => new("Failed", Code: e is CacheException known ? known.Code : "PLAN_FAILED", Message: e.Message, Details: e.ToString());
}
