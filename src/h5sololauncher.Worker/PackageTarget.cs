using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Forge;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Worker;

internal sealed record PackageTarget(string Root, string PackageFullName, string Family, string Executable, string ApplicationId)
{
    public const string HaloFamily = "Microsoft.Tomp_8wekyb3d8bbwe";
    public static PackageTarget Forge(string root, string package) => new(root, package, ForgePaths.Family, "halo5forge.exe", "Ausar");
    public static void Verify(PackageTarget target)
    {
        if (target.Family != ForgePaths.Family && target.Family != HaloFamily) throw new CacheException("PACKAGE_INVALID", "Unexpected launcher package identity.");
        if (!PathFor(target.PackageFullName).Equals(SafePaths.Canonical(target.Root), StringComparison.OrdinalIgnoreCase))
            throw new CacheException("PACKAGE_CHANGED", "An app installation changed. Check Forge again before retrying.");
        SafePaths.PackageChild(target.Root, target.Executable);
    }
    private static string PathFor(string package)
    {
        uint size = 0;
        if (Native.GetPackagePathByFullName(package, ref size, null) != 122 || size > 32768) throw new CacheException("PACKAGE_MISSING", "Windows no longer reports the required app package.");
        var path = new StringBuilder((int)size);
        if (Native.GetPackagePathByFullName(package, ref size, path) != 0) throw new CacheException("PACKAGE_MISSING", "Windows couldn’t locate the required app package.");
        return SafePaths.Canonical(path.ToString());
    }
    public RunningForge? Find()
    {
        Verify(this); RunningForge? found = null;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Executable)))
        {
            using (process)
            {
                using var self = Process.GetCurrentProcess(); if (process.SessionId != self.SessionId) continue;
                var handle = Native.OpenProcess(0x1000, false, process.Id);
                if (handle == 0) throw new CacheException("PACKAGE_PROCESS_UNREADABLE", "Windows couldn’t inspect an existing game or Halo process. Close that instance and retry from the launcher.");
                try
                {
                    var path = new StringBuilder(32768); uint size = 32768;
                    if (!Native.QueryFullProcessImageName(handle, 0, path, ref size) || PackageProcessSession.Package(handle) != PackageFullName ||
                        !SafePaths.PackageChildMatches(Root, Executable, path.ToString()))
                        throw new CacheException("PACKAGE_PROCESS_MISMATCH", "An app with the expected executable name is running from a different package or folder. Close that instance before starting Forge here.");
                    if (!Native.GetProcessTimes(handle, out var created, out _, out _, out _)) continue;
                    if (found is not null) throw new CacheException("PACKAGE_MULTIPLE_PROCESSES", "More than one matching app process is running. Close the extra instance before retrying.");
                    found = new(process.Id, created);
                }
                finally { Native.CloseHandle(handle); }
            }
        }
        return found;
    }
    internal static PackageTarget FindHalo()
    {
        uint count = 0, length = 0;
        var status = GetPackagesByPackageFamily(HaloFamily, ref count, 0, ref length, 0);
        if (count == 0) throw new CacheException("HALO_APP_MISSING", "Install the Halo app for this Windows account, then use Start Forge again. Forge needs its companion Halo app to receive the startup request.");
        if (status != 122 || count > 64 || length > 65536) throw new CacheException("HALO_DISCOVERY_FAILED", "Windows couldn’t list the installed Halo app.");
        var pointers = Marshal.AllocHGlobal(checked((int)count * IntPtr.Size)); var buffer = Marshal.AllocHGlobal(checked((int)length * 2));
        try
        {
            if (GetPackagesByPackageFamily(HaloFamily, ref count, pointers, ref length, buffer) != 0) throw new CacheException("HALO_DISCOVERY_FAILED", "The Halo installation changed during discovery. Retry.");
            List<PackageTarget> targets = [];
            for (var i = 0; i < count; i++)
            {
                var fullName = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointers, i * IntPtr.Size))!;
                if (!fullName.Contains("_x64__", StringComparison.Ordinal)) continue;
                var root = PathFor(fullName); SafePaths.PackageRoot(root);
                var path = SafePaths.PackageChild(root, "AppxManifest.xml");
                using var stream = File.OpenRead(path);
                if (stream.Length > 2 * 1024 * 1024) throw new CacheException("HALO_MANIFEST_INVALID", "The Halo package manifest is too large.");
                var manifest = XDocument.Load(stream); var ns = manifest.Root!.Name.Namespace;
                if (manifest.Root.Element(ns + "Identity")?.Attribute("Name")?.Value != "Microsoft.Tomp") continue;
                var app = manifest.Root.Element(ns + "Applications")?.Elements(ns + "Application").SingleOrDefault(x => x.Attribute("Id")?.Value == "App");
                var executable = app?.Attribute("Executable")?.Value;
                if (string.IsNullOrWhiteSpace(executable)) continue;
                SafePaths.PackageChild(root, executable); targets.Add(new(root, fullName, HaloFamily, executable, "App"));
            }
            if (targets.Count != 1) throw new CacheException("HALO_INSTALLATION_UNSUPPORTED", "A single Windows x64 Halo app installation is required. Check the Halo app in Windows Settings and retry.");
            return targets[0];
        }
        finally { Marshal.FreeHGlobal(pointers); Marshal.FreeHGlobal(buffer); }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackagesByPackageFamily(string family, ref uint count, nint names, ref uint length, nint buffer);
}
