using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Forge;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Worker;

public sealed class WindowsForgeLaunch : IForgeLaunchPlatform
{
    private readonly string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "h5sololauncher", "launch");
    private PackageTarget? halo;
    private string PendingPath => SafePaths.Child(directory, "pending.json");
    private sealed record Attempt(int Format, DateTimeOffset Started);
    public IDisposable Acquire()
    {
        SafePaths.NoLinks(directory); Directory.CreateDirectory(directory);
        try { return new FileStream(SafePaths.Child(directory, "launch.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new CacheException("FORGE_LAUNCH_BUSY", "Another launcher is already starting Forge. Wait for that attempt to finish."); }
    }
    public void Validate(ForgeLaunchRequest request)
    {
        ForgePaths.ValidatePackage(request.PackageFullName);
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) throw new CacheException("FORGE_READER_ARCHITECTURE", "Use the Windows x64 launcher.");
        PackageTarget.Verify(PackageTarget.Forge(request.ForgeRoot, request.PackageFullName));
    }
    public RunningForge? FindForge(ForgeLaunchRequest request) => PackageTarget.Forge(request.ForgeRoot, request.PackageFullName).Find();
    public bool RecentAttempt
    {
        get
        {
            if (!File.Exists(PendingPath)) return false;
            using var stream = File.OpenRead(PendingPath);
            if (stream.Length > 4096) throw new CacheException("FORGE_LAUNCH_STATE_INVALID", "Startup recovery data is invalid. Copy the details for diagnosis.");
            var attempt = JsonSerializer.Deserialize<Attempt>(stream);
            if (attempt?.Format != 1) throw new CacheException("FORGE_LAUNCH_STATE_INVALID", "Startup recovery data is invalid. Copy the details for diagnosis.");
            return DateTimeOffset.UtcNow - attempt.Started < TimeSpan.FromSeconds(90);
        }
    }
    public void PrepareLaunch()
    {
        halo = PackageTarget.FindHalo(); PackageTarget.Verify(halo);
        var helper = Path.Combine(AppContext.BaseDirectory, "h5sololauncher.HubBridge.dll"); SafePaths.NoLinks(helper);
        if (!File.Exists(helper)) throw new CacheException("HALO_BRIDGE_MISSING", "The Halo startup helper is missing. Keep the complete published launcher folder together.");
    }
    public void BeginAttempt()
    {
        var temporary = SafePaths.Child(directory, "attempt-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, new Attempt(1, DateTimeOffset.UtcNow)); stream.Flush(true); }
            File.Move(temporary, PendingPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void EndAttempt() { if (File.Exists(PendingPath)) File.Delete(PendingPath); }
    public void LaunchThroughHalo(IProgress<IndexProgress> progress, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Forge startup requires Windows.");
        cancellation.ThrowIfCancellationRequested();
        var target = halo ?? throw new InvalidOperationException("Halo launch preflight was not run.");
        PackageTarget.Verify(target); progress.Report(new("Opening the Halo app for Forge startup", 0, 0));
        object manager = new ActivationManager();
        try
        {
            var result = ((IActivationManager)manager).ActivateApplication(target.Family + "!" + target.ApplicationId, "", 2, out _);
            if (result < 0) throw new CacheException("HALO_ACTIVATION_FAILED", $"Windows couldn’t open the Halo app (0x{result:X8}). Check that the Halo app is installed for this account. {Marshal.GetExceptionForHR(result)?.Message}");
        }
        finally { Marshal.FinalReleaseComObject(manager); }
        var stable = 0; RunningForge? observed = null;
        for (var i = 0; i < 60; i++)
        {
            cancellation.ThrowIfCancellationRequested(); var current = target.Find();
            stable = current is not null && current == observed ? stable + 1 : 0; observed = current;
            if (stable >= 4) break;
            if (i == 59) throw new CacheException("HALO_START_TIMEOUT", "The Halo app did not stay open during startup. Copy the details so its startup failure can be diagnosed.");
            Delay(cancellation);
        }
        progress.Report(new("Requesting Forge startup from Halo", 0, 0));
        try
        {
            using var session = PackageProcessSession.Attach(target, "h5sololauncher.HubBridge.dll", "H5LaunchForge", cancellation);
            var request = new byte[20]; BitConverter.GetBytes(0x48354C48).CopyTo(request, 0); BitConverter.GetBytes(1).CopyTo(request, 4);
            var response = session.Call(request, cancellation);
            var status = BitConverter.ToInt32(response, 8); var accepted = BitConverter.ToUInt32(response, 12); var stage = BitConverter.ToUInt32(response, 16);
            if (stage != 3 || status < 0 || accepted != 1)
                throw new CacheException("HALO_LAUNCH_REQUEST_FAILED", $"Halo could not confirm the Forge startup request (stage {stage}, HRESULT 0x{status:X8}, accepted {accepted}). " +
                    "Copy the details. A pending request may still complete; the launcher will check for it before retrying.");
        }
        catch (CacheException e) when (!e.Code.StartsWith("HALO_", StringComparison.Ordinal))
        { throw new CacheException("HALO_BRIDGE_FAILED", "The startup helper could not complete inside the Halo app. " + e.Code + ": " + e.Message); }
    }
    public void Delay(CancellationToken cancellation)
    { if (cancellation.WaitHandle.WaitOne(250)) cancellation.ThrowIfCancellationRequested(); }

    public static ForgeLaunchResult Run(ForgeLaunchRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation)
    {
        var platform = new WindowsForgeLaunch();
        var result = new ForgeLaunchCoordinator(platform).Run(request, progress, cancellation);
        result = result with { Details = $"Forge package: {request.PackageFullName}\nForge folder: {request.ForgeRoot}\n" +
            (platform.halo is null ? string.Empty : $"Halo package: {platform.halo.PackageFullName}\nHalo folder: {platform.halo.Root}\nHalo executable: {platform.halo.Executable}\n") + result.Details };
        try
        {
            var logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "h5sololauncher", "logs");
            SafePaths.NoLinks(logs); Directory.CreateDirectory(logs); var path = SafePaths.Child(logs, "launch.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Move(path, SafePaths.Child(logs, "launch.previous.log"), overwrite: true);
            File.AppendAllText(path, DateTimeOffset.UtcNow.ToString("O") + " " + JsonSerializer.Serialize(result) + Environment.NewLine);
        }
        catch (Exception) { /* Preserve the startup result when logging is unavailable. */ }
        return result;
    }
    public static int ActivateExisting(ForgeLaunchRequest request)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Forge activation requires Windows.");
        var target = PackageTarget.Forge(request.ForgeRoot, request.PackageFullName); PackageTarget.Verify(target);
        object manager = new ActivationManager();
        try
        {
            var result = ((IActivationManager)manager).ActivateApplication(target.Family + "!" + target.ApplicationId, "", 0, out var processId);
            if (result < 0) throw new CacheException("FORGE_ACTIVATION_FAILED", $"Windows couldn’t show Forge (0x{result:X8}). {Marshal.GetExceptionForHR(result)?.Message}");
            return checked((int)processId);
        }
        finally { Marshal.FinalReleaseComObject(manager); }
    }
    public static ForgeLaunchResult Inspect(ForgeLaunchRequest request)
    {
        try
        {
            var platform = new WindowsForgeLaunch(); platform.Validate(request);
            var running = platform.FindForge(request);
            if (running is not null) return new("Running", running.ProcessId, Message: "Forge is already running. No startup action was taken.");
            platform.PrepareLaunch();
            return new("Available", Message: "The installed Halo app and packaged startup bridge were found. No app was started.",
                Details: $"Halo package: {platform.halo!.PackageFullName}\nFolder: {platform.halo.Root}\nExecutable: {platform.halo.Executable}");
        }
        catch (Exception e) { return new("Failed", Code: e is CacheException known ? known.Code : "FORGE_LAUNCH_CHECK_FAILED", Message: e.Message, Details: e.ToString()); }
    }
    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")] private class ActivationManager { }
    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivationManager
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
    }
}
