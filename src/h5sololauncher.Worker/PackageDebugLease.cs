using System.Runtime.InteropServices;
using H5SoloLauncher.Core;

namespace H5SoloLauncher.Worker;

// Process Lifetime Management may suspend a packaged game as soon as it loses
// foreground ownership. A balanced package debug lease disables that automatic
// suspension only while the launcher has native work executing inside the app.
internal sealed class PackageDebugLease : IDisposable
{
    private sealed class Active(object manager, IPackageDebugSettings settings)
    { public object Manager { get; } = manager; public IPackageDebugSettings Settings { get; } = settings; public int References { get; set; } = 1; }
    private static readonly object gate = new();
    private static readonly Dictionary<string, Active> active = new(StringComparer.Ordinal);
    private int held = 1;
    private readonly string package;

    private PackageDebugLease(string package) { this.package = package; }

    internal static PackageDebugLease Acquire(string package)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Forge lifecycle control requires Windows.");
        lock (gate)
        {
            if (active.TryGetValue(package, out var existing))
            {
                existing.References++;
                var resumedExisting = existing.Settings.Resume(package);
                if (resumedExisting >= 0) return new(package);
                existing.References--;
                throw ResumeError(resumedExisting);
            }
            object manager = new PackageDebugSettings();
            var settings = (IPackageDebugSettings)manager;
            var enabled = settings.EnableDebugging(package, null, 0);
            if (enabled < 0)
            {
                Marshal.FinalReleaseComObject(manager);
                throw new CacheException("FORGE_LIFECYCLE_CONTROL_FAILED", $"Windows couldn’t keep Forge active for preparation (0x{enabled:X8}). Run the launcher as administrator and retry.");
            }
            var resumed = settings.Resume(package);
            if (resumed >= 0)
            { active.Add(package, new(manager, settings)); return new(package); }
            settings.DisableDebugging(package); Marshal.FinalReleaseComObject(manager);
            throw ResumeError(resumed);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref held, 0) == 0) return;
        lock (gate)
        {
            if (!active.TryGetValue(package, out var value)) return;
            if (--value.References != 0) return;
            active.Remove(package);
            try { value.Settings.DisableDebugging(package); }
            finally { if (OperatingSystem.IsWindows()) Marshal.FinalReleaseComObject(value.Manager); }
        }
    }
    private static CacheException ResumeError(int result) => new("FORGE_RESUME_FAILED", $"Windows couldn’t resume Forge for preparation (0x{result:X8}). Close Forge completely, then use Start Forge again.");

    [ComImport, Guid("B1AEC16F-2383-4852-B0E9-8F0B1DC66B4D")]
    private class PackageDebugSettings { }

    [ComImport, Guid("F27C3930-8029-4AD1-94E3-3DBA417810C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPackageDebugSettings
    {
        [PreserveSig] int EnableDebugging([MarshalAs(UnmanagedType.LPWStr)] string packageFullName,
            [MarshalAs(UnmanagedType.LPWStr)] string? debuggerCommandLine, nint environment);
        [PreserveSig] int DisableDebugging([MarshalAs(UnmanagedType.LPWStr)] string packageFullName);
        [PreserveSig] int Suspend([MarshalAs(UnmanagedType.LPWStr)] string packageFullName);
        [PreserveSig] int Resume([MarshalAs(UnmanagedType.LPWStr)] string packageFullName);
    }
}
