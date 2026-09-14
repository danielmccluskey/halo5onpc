using System.Diagnostics;
using System.Runtime.InteropServices;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Forge;

namespace H5SoloLauncher.Worker;

public sealed class ForgeMemory : IForgeMemory
{
    private readonly PackageTarget target;
    private readonly RunningForge running;
    private nint handle;
    public ulong ImageBase { get; }
    public ForgeMemory(string root, string package)
    {
        target = PackageTarget.Forge(root, package);
        running = target.Find() ?? throw new CacheException("FORGE_NOT_RUNNING", "Start Forge before reading its native layouts.");
        handle = Native.OpenProcess(0x101010, false, running.ProcessId);
        if (handle == 0) throw Failure();
        try
        {
            VerifyIdentity();
            if (!Native.IsWow64Process2(handle, out var machine, out var native) || machine != 0 || native != 0x8664)
                throw new CacheException("FORGE_ARCHITECTURE", "The campaign runtime requires the x64 Forge process.");
            using var process = Process.GetProcessById(running.ProcessId);
            ImageBase = checked((ulong)(process.MainModule?.BaseAddress ?? throw Failure()));
        }
        catch { Dispose(); throw; }
    }
    public byte[] Read(ulong address, int count)
    {
        if (handle == 0) throw new ObjectDisposedException(nameof(ForgeMemory));
        if (count is <= 0 or > 1024 * 1024 || address < 0x10000 || address > 0x00007fffffffffff - (ulong)count)
            throw new CacheException("FORGE_MEMORY_RANGE", "The native layout read is outside its supported range.");
        var bytes = new byte[count];
        if (!Native.ReadProcessMemory(handle, checked((nint)address), bytes, (nuint)count, out var read) || read != (nuint)count) throw Failure();
        return bytes;
    }
    public void VerifyIdentity()
    {
        if (handle == 0 || Native.WaitForSingleObject(handle, 0) == 0 ||
            !Native.GetProcessTimes(handle, out var created, out _, out _, out _) || created != running.Created ||
            PackageProcessSession.Package(handle) != target.PackageFullName || target.Find() != running)
            throw new CacheException("FORGE_PROCESS_CHANGED", "Forge closed or restarted while its data was being read. Retry preparation.");
    }
    private static CacheException Failure() => new("FORGE_MEMORY_READ", "Windows couldn't read Forge's native layouts (error " + Marshal.GetLastWin32Error() + "). Keep Forge open and retry preparation.");
    public void Dispose() { if (handle != 0) { Native.CloseHandle(handle); handle = 0; } }
}
