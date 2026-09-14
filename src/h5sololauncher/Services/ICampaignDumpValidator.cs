using H5SoloLauncher.Models;

namespace H5SoloLauncher.Services;

public interface ICampaignDumpValidator
{
    Task<CampaignDumpCheckResult> ValidateAsync(string directory, CancellationToken cancellation = default);
}
