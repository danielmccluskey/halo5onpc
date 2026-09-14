namespace H5SoloLauncher.Core.Forge;

public sealed record ForgeLaunchRequest(string ForgeRoot, string PackageFullName);
public sealed record ForgeLaunchResult(string State, int? ProcessId = null, string? Code = null, string? Message = null, string? Details = null);
public interface IForgeLaunchWorker
{
    Task<ForgeLaunchResult> StartForgeAsync(ForgeLaunchRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation);
}
public sealed record RunningForge(int ProcessId, long Created);
public interface IForgeLaunchPlatform
{
    IDisposable Acquire();
    void Validate(ForgeLaunchRequest request);
    RunningForge? FindForge(ForgeLaunchRequest request);
    bool RecentAttempt { get; }
    void PrepareLaunch();
    void BeginAttempt();
    void EndAttempt();
    void LaunchThroughHalo(IProgress<IndexProgress> progress, CancellationToken cancellation);
    void Delay(CancellationToken cancellation);
}

/// <summary>One launch attempt; startup acceptance and a live Forge process are separate outcomes.</summary>
public sealed class ForgeLaunchCoordinator(IForgeLaunchPlatform platform, int maximumPolls = 180, int stablePolls = 8)
{
    public ForgeLaunchResult Run(ForgeLaunchRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation)
    {
        try
        {
            cancellation.ThrowIfCancellationRequested(); platform.Validate(request);
            using var lease = platform.Acquire();
            var existing = platform.FindForge(request);
            if (existing is not null) { platform.EndAttempt(); return new("Running", existing.ProcessId, Message: "Forge is already running."); }
            if (platform.RecentAttempt) throw new CacheException("FORGE_LAUNCH_PENDING", "A previous startup request may still complete. Wait a minute, then use Start Forge again. The launcher will reuse it if it appears.");
            progress.Report(new("Starting Forge through the Halo app", 0, 0));
            platform.PrepareLaunch();
            platform.BeginAttempt();
            platform.LaunchThroughHalo(progress, cancellation);
            RunningForge? observed = null; var stable = 0;
            for (var i = 0; i < maximumPolls; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var game = platform.FindForge(request);
                if (observed is not null && game != observed)
                    throw new CacheException("FORGE_EXITED_DURING_STARTUP", "Forge closed or restarted during startup. Copy the details, then retry from the launcher.");
                if (game is not null)
                {
                    observed = game;
                    if (++stable >= stablePolls) { platform.EndAttempt(); return new("Running", game.ProcessId, Message: "Forge is running. Its menu may still be loading."); }
                }
                progress.Report(new(game is null ? "Waiting for Forge to start" : "Checking Forge stays running", 0, 0));
                platform.Delay(cancellation);
            }
            throw new CacheException("FORGE_START_TIMEOUT", "Windows accepted the startup request, but Forge did not stay running within 45 seconds. Copy the details; a late startup request may still complete.");
        }
        catch (OperationCanceledException) { return new("Paused", Message: "Stopped waiting for Forge. A startup request already sent to Windows may still finish; an existing session will be reused next time."); }
        catch (Exception e) { return new("Failed", Code: e is CacheException known ? known.Code : "FORGE_LAUNCH_FAILED", Message: e.Message, Details: e.ToString()); }
    }
}
