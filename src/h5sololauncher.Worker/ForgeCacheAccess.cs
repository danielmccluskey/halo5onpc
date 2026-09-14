using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Forge;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Worker;

/// <summary>Called during user-requested preparation or playback while the worker owns the cache lock.</summary>
public static class ForgeCacheAccess
{
    public static string GrantRead(ForgeRequest request, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Cache permissions require Windows.");
        if (!request.AllowCacheRead) throw new CacheException("CACHE_PERMISSION_NOT_REQUESTED", "Use Allow Forge to read cache to change this folder’s permissions.");
        ForgePaths.ValidatePackage(request.PackageFullName);
        var cache = string.IsNullOrEmpty(request.SourceRoot) ? CacheFolders.OpenExisting(request.CacheRoot, request.ForgeRoot) : CacheFolders.Open(request.CacheRoot, request.SourceRoot, request.ForgeRoot);
        // Windows propagates inheritable entries. Reject links before asking it to change this tree.
        var folders = new Stack<string>(); folders.Push(cache.Root);
        while (folders.TryPop(out var folder))
        {
            cancellation.ThrowIfCancellationRequested();
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
            {
                cancellation.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new CacheException("CACHE_PERMISSION_LINKED", "The cache contains a linked file or folder. Choose a cache without links before granting Forge access.");
                if (attributes.HasFlag(FileAttributes.Directory)) folders.Push(entry);
            }
        }
        var sid = PackageSid();
        try
        {
            cancellation.ThrowIfCancellationRequested(); SafePaths.NoLinks(cache.Root);
            var directory = new DirectoryInfo(cache.Root);
            var acl = directory.GetAccessControl(AccessControlSections.Access);
            var rule = new FileSystemAccessRule(sid, FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow);
            var present = false;
            foreach (FileSystemAccessRule entry in acl.GetAccessRules(true, false, typeof(SecurityIdentifier)))
                if (entry.IdentityReference.Equals(sid) && entry.AccessControlType == AccessControlType.Allow && entry.PropagationFlags == PropagationFlags.None &&
                    (entry.InheritanceFlags & rule.InheritanceFlags) == rule.InheritanceFlags && (entry.FileSystemRights & rule.FileSystemRights) == rule.FileSystemRights)
                    present = true;
            acl.AddAccessRule(rule);
            // Reapply inheritance on retry, including after an interrupted propagation.
            // Preserve ownership, inheritance settings, denies and unrelated access rules.
            directory.SetAccessControl(acl);
            return $"Forge package SID: {sid.Value}\nCache read rule: {(present ? "already present" : "added")}\nFolder: {cache.Root}\nA live Forge read is still required to confirm access.";
        }
        catch (UnauthorizedAccessException e)
        {
            throw new CacheException("CACHE_PERMISSION_DENIED", "Windows would not let the launcher grant read access to this cache. Choose a cache folder you own, or have its owner grant Forge access. " + e.Message);
        }
    }
    public static SecurityIdentifier PackageSid()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Package identities require Windows.");
        var result = DeriveAppContainerSidFromAppContainerName(ForgePaths.Family, out var sid);
        if (result < 0) Marshal.ThrowExceptionForHR(result);
        try { return new SecurityIdentifier(sid); }
        finally { FreeSid(sid); }
    }
    [DllImport("userenv.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int DeriveAppContainerSidFromAppContainerName(string name, out nint sid);
    [DllImport("advapi32.dll", ExactSpelling = true)] private static extern nint FreeSid(nint sid);
}
