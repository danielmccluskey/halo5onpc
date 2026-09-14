using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Forge;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Worker;

internal sealed class PackageProcessSession : IDisposable
{
    private readonly PackageTarget request;
    private readonly Process process;
    private readonly nint handle;
    private nint readAddress;
    private readonly string marker;
    private FileStream? lease;
    private bool uncertain;
    private bool disposed;
    private const int RequestSize = 1114160, DataOffset = 65584;
    private PackageProcessSession(PackageTarget request, Process process, nint handle, string marker, FileStream lease)
    { this.request = request; this.process = process; this.handle = handle; this.marker = marker; this.lease = lease; }

    public static PackageProcessSession Attach(PackageTarget request, string helperName, string exportName, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested(); PackageTarget.Verify(request);
        if (!Environment.Is64BitProcess || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new CacheException("FORGE_READER_ARCHITECTURE", "Use the Windows x64 build of h5sololauncher.");
        Process? chosen = null; nint chosenHandle = 0;
        try
        {
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(request.Executable)))
            {
                var keep = false; nint opened = 0;
                try
                {
                    using var self = Process.GetCurrentProcess();
                    if (process.SessionId != self.SessionId) continue;
                    opened = Native.OpenProcess(0x43A, false, process.Id); // create thread, query, VM read/write/operation
                    if (opened == 0) throw Error("FORGE_ATTACH_DENIED", "Couldn’t open the running Forge process. Run Forge and the launcher under the same Windows account.");
                    var image = new StringBuilder(32768); uint length = 32768;
                    if (!Native.QueryFullProcessImageName(opened, 0, image, ref length)) throw Error("FORGE_IDENTITY_UNAVAILABLE", "Couldn’t verify the running Forge executable.");
                    if (!SafePaths.Canonical(image.ToString()).Equals(SafePaths.Child(request.Root, request.Executable), StringComparison.OrdinalIgnoreCase)) continue;
                    if (Package(opened) != request.PackageFullName) continue;
                    if (!Native.IsWow64Process2(opened, out var machine, out var native) || machine != 0 || native != 0x8664)
                        throw new CacheException("FORGE_READER_ARCHITECTURE", "The running Forge process must be native Windows x64.");
                    if (chosen is not null) throw new CacheException("FORGE_MULTIPLE_PROCESSES", "More than one matching Forge process is running. Close the extra instance and retry.");
                    chosen = process; chosenHandle = opened; keep = true;
                }
                finally { if (!keep) { if (opened != 0) Native.CloseHandle(opened); process.Dispose(); } }
            }
            if (chosen is null) throw new CacheException("FORGE_NOT_RUNNING", "The required app is no longer running. Use Start Forge in the launcher to retry.");
            if (!Native.GetProcessTimes(chosenHandle, out var creation, out _, out _, out _)) throw Error("FORGE_IDENTITY_UNAVAILABLE", "Couldn’t identify this Forge session.");
            var localState = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", request.Family, "LocalState");
            SafePaths.NoLinks(localState);
            if (!Directory.Exists(localState)) throw new CacheException("FORGE_LOCAL_STATE_MISSING", "Windows has not created the app data folder yet. Retry startup from the launcher.");
            var bridge = SafePaths.Child(localState, "h5sololauncher/bridge"); Directory.CreateDirectory(bridge);
            var marker = SafePaths.Child(bridge, $"session-{chosen.Id}-{creation:x16}.pending");
            FileStream lease;
            try { lease = new(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
            catch (IOException) { throw new CacheException("FORGE_SESSION_PENDING", "A reader session was interrupted or is still running. Close the Halo and Forge apps completely, then use Start Forge in the launcher to retry."); }
            var result = new PackageProcessSession(request, chosen, chosenHandle, marker, lease);
            chosen = null; chosenHandle = 0;
            try
            {
                lease.Write(Encoding.UTF8.GetBytes("h5sololauncher package helper session; retained after uncertain completion.\n")); lease.Flush(true);
                result.LoadHelper(bridge, helperName, exportName, cancellation); return result;
            }
            catch { result.Dispose(); throw; }
        }
        finally { if (chosenHandle != 0) Native.CloseHandle(chosenHandle); chosen?.Dispose(); }
    }
    private void LoadHelper(string bridge, string helperName, string exportName, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var helper = Path.Combine(AppContext.BaseDirectory, helperName); SafePaths.NoLinks(helper);
        if (!File.Exists(helper)) throw new CacheException("FORGE_READER_MISSING", "The Forge reader DLL is missing. Keep all published launcher files together.");
        var bytes = File.ReadAllBytes(helper);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var folder = SafePaths.Child(bridge, hash); Directory.CreateDirectory(folder);
        var staged = SafePaths.Child(folder, Path.GetFileName(helper));
        if (!File.Exists(staged))
        {
            using var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None); output.Write(bytes); output.Flush(true);
        }
        using (var input = File.OpenRead(staged))
            if (!SHA256.HashData(input).AsSpan().SequenceEqual(SHA256.HashData(bytes))) throw new CacheException("FORGE_READER_DAMAGED", "The staged Forge reader differs from this launcher. Keep the details for diagnosis.");
        process.Refresh(); var alreadyLoaded=false;
        foreach(ProcessModule module in process.Modules)
        {
            if(!Path.GetFileName(module.FileName).Equals(helperName,StringComparison.OrdinalIgnoreCase))continue;
            if(!SafePaths.Canonical(module.FileName).Equals(SafePaths.Canonical(staged),StringComparison.OrdinalIgnoreCase))
                throw new CacheException("FORGE_HELPER_VERSION_CHANGED","Forge is using helpers from another launcher build. Close Forge, then start it again from this launcher.");
            alreadyLoaded=true;
        }
        var local = NativeLibrary.Load(staged);
        long exportOffset;
        try { exportOffset = NativeLibrary.GetExport(local, exportName).ToInt64() - local.ToInt64(); }
        finally { NativeLibrary.Free(local); }
        var load = Native.GetProcAddress(Native.GetModuleHandle("kernel32.dll"), "LoadLibraryW");
        if (load == 0 || !Native.GetModuleHandleEx(6, load, out var owner)) throw Error("FORGE_READER_LOAD_FAILED", "Couldn’t locate the Windows DLL loader.");
        var ownerPath = new StringBuilder(32768);
        if (Native.GetModuleFileName(owner, ownerPath, ownerPath.Capacity) == 0) throw Error("FORGE_READER_LOAD_FAILED", "Couldn’t locate the Windows loader module.");
        var remoteOwner = Module(ownerPath.ToString());
        var loader = remoteOwner.Base + (load.ToInt64() - owner.ToInt64());
        if (loader < remoteOwner.Base || loader >= remoteOwner.Base + remoteOwner.Size) throw new CacheException("FORGE_READER_LOAD_FAILED", "The Windows loader address is outside its module.");
        if(!alreadyLoaded)Invoke(new nint(loader), Encoding.Unicode.GetBytes(staged + '\0'), 0);
        var remoteHelper = Module(staged);
        if (exportOffset <= 0 || exportOffset >= remoteHelper.Size) throw new CacheException("FORGE_READER_LOAD_FAILED", "The Forge reader export is outside its module.");
        readAddress = new nint(remoteHelper.Base + exportOffset);
    }
    private (long Base, int Size) Module(string path)
    {
        process.Refresh();
        foreach (ProcessModule module in process.Modules)
            if (SafePaths.Canonical(module.FileName).Equals(SafePaths.Canonical(path), StringComparison.OrdinalIgnoreCase))
                return (module.BaseAddress.ToInt64(), module.ModuleMemorySize);
        throw new CacheException("FORGE_READER_LOAD_FAILED", "The required reader module wasn’t found in Forge. Copy the details and restart Forge before retrying.");
    }
    public ForgeRead Read(int operation, string path, long offset, int count, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (path.Length >= 32768 || count is < 1 or > 1024 * 1024 || offset < 0) throw new CacheException("FORGE_READ_INVALID", "The reader request exceeds its limits.");
        if (Package(handle) != request.PackageFullName) throw new CacheException("FORGE_CHANGED", "Forge exited or changed. Start it again and retry.");
        var buffer = new byte[RequestSize];
        using (var writer = new BinaryWriter(new MemoryStream(buffer), Encoding.Unicode))
        {
            writer.Write(0x48354652); writer.Write(1); writer.Write(operation); writer.Write(-1); writer.Write(count); writer.Write(0);
            writer.Write(offset); writer.Write(0L); writer.Write(0L); writer.Write(Encoding.Unicode.GetBytes(path + '\0'));
        }
        var result = Invoke(readAddress, buffer, RequestSize);
        var status = BitConverter.ToInt32(result, 12); var returned = BitConverter.ToInt32(result, 20);
        if (status != 0) throw new CacheException(status == 5 ? "FORGE_READ_DENIED" : "FORGE_READ_FAILED",
            $"Forge couldn’t read {path}: {new Win32Exception(status).Message} (Windows {status}).");
        if (returned != count) throw new CacheException("FORGE_READ_FAILED", "Forge returned an incomplete table read.");
        return new(result.AsSpan(DataOffset, returned).ToArray(), BitConverter.ToInt64(result, 32), BitConverter.ToInt64(result, 40));
    }
    internal byte[] Call(byte[] buffer, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (Package(handle) != request.PackageFullName) throw new CacheException("PACKAGE_EXITED", "The app exited while its helper was starting. Retry from the launcher.");
        return Invoke(readAddress, buffer, buffer.Length);
    }
    private byte[] Invoke(nint function, byte[] requestBytes, int readBytes)
    {
        var remote = Native.VirtualAllocEx(handle, 0, (nuint)requestBytes.Length, 0x3000, 4);
        if (remote == 0) throw Error("FORGE_READER_MEMORY", "Couldn’t allocate the bounded Forge reader buffer.");
        nint thread = 0; var safeToFree = true;
        try
        {
            if (!Native.WriteProcessMemory(handle, remote, requestBytes, (nuint)requestBytes.Length, out var written) || written != (nuint)requestBytes.Length)
                throw Error("FORGE_READER_MEMORY", "Couldn’t send the Forge reader request.");
            thread = Native.CreateRemoteThread(handle, 0, 0, function, remote, 0, out _);
            if (thread == 0) throw Error("FORGE_READER_START_FAILED", "Windows couldn’t start the Forge reader. Copy the details for diagnosis.");
            safeToFree = false;
            // Cancellation is observed between calls. Never free memory beneath a running thread.
            if (Native.WaitForSingleObject(thread, 15000) != 0)
            {
                uncertain = true;
                throw new CacheException("FORGE_READER_TIMEOUT", "Forge’s reader did not finish within 15 seconds. Close the Halo and Forge apps completely, then use Start Forge in the launcher to retry. Its pending memory was left intact.");
            }
            safeToFree = true;
            if (!Native.GetExitCodeThread(thread, out var exit) || (readBytes > 0 && exit >= 0x80000000))
            { uncertain = true; throw new CacheException("FORGE_READER_CRASHED", "The Forge reader failed. Close the Halo and Forge apps completely, then use Start Forge in the launcher to retry."); }
            if (readBytes == 0) return [];
            var response = new byte[readBytes];
            if (!Native.ReadProcessMemory(handle, remote, response, (nuint)readBytes, out var read) || read != (nuint)readBytes)
                throw Error("FORGE_READER_MEMORY", "Couldn’t retrieve the Forge reader response.");
            return response;
        }
        finally
        {
            if (thread != 0) Native.CloseHandle(thread);
            if (safeToFree) Native.VirtualFreeEx(handle, remote, 0, 0x8000);
        }
    }
    internal static string Package(nint handle)
    {
        uint length = 0; if (Native.GetPackageFullName(handle, ref length, null) != 122 || length > 1024) return string.Empty;
        var value = new StringBuilder((int)length); return Native.GetPackageFullName(handle, ref length, value) == 0 ? value.ToString() : string.Empty;
    }
    private static CacheException Error(string code, string message) => new(code, message + " " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        lease?.Dispose(); lease = null;
        try { if (!uncertain) File.Delete(marker); }
        finally { Native.CloseHandle(handle); process.Dispose(); }
    }
}

