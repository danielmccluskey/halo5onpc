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

public sealed class ForgeFiles : IForgePayloadReader, IForgeAudioReader
{
    private readonly ForgeRequest request;
    private PackageProcessSession? session;
    private readonly string? permissionDetails;
    public string Mode => session is null ? "Direct file reads" : "Forge process reader";
    public ForgeFiles(ForgeRequest request, CancellationToken cancellation = default)
    {
        this.request = request;
        VerifyInstallation(request);
        if (request.AllowCacheRead) permissionDetails = ForgeCacheAccess.GrantRead(request, cancellation);
    }
    internal static void VerifyInstallation(ForgeRequest request)
    {
        ForgePaths.ValidatePackage(request.PackageFullName);
        uint size = 0;
        if (Native.GetPackagePathByFullName(request.PackageFullName, ref size, null) != 122 || size > 32768)
            throw new CacheException("FORGE_INSTALLATION_CHANGED", "Windows no longer reports this Forge package. Check Forge again.");
        var path = new StringBuilder((int)size);
        if (Native.GetPackagePathByFullName(request.PackageFullName, ref size, path) != 0 ||
            !SafePaths.Canonical(path.ToString()).Equals(SafePaths.Canonical(request.ForgeRoot), StringComparison.OrdinalIgnoreCase))
            throw new CacheException("FORGE_INSTALLATION_CHANGED", "Forge’s install folder changed. Check Forge again before preparing data.");
        SafePaths.NoLinks(request.ForgeRoot);
    }
    public ForgeRead Read(string relative, long offset, int count, CancellationToken cancellation) => ReadFile(relative, offset, count, false, cancellation);
    public ForgeRead ReadPayload(string relative, long offset, int count, CancellationToken cancellation) => ReadFile(relative, offset, count, true, cancellation);
    public ForgeRead ReadAudio(string relative, long offset, int count, CancellationToken cancellation) => ReadFile(relative, offset, count, false, cancellation, audio: true);
    private ForgeRead ReadFile(string relative, long offset, int count, bool payload, CancellationToken cancellation, bool audio = false)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!(audio ? ForgePaths.IsAudio(relative) : ForgePaths.IsGlobal(relative)) || offset < 0 || count is < 1 or > 1024 * 1024) throw new CacheException("FORGE_READ_INVALID", "The Forge table read request is invalid.");
        var path = SafePaths.Child(request.ForgeRoot, relative);
        var operation = audio ? 4 : payload ? 3 : 1;
        if (session is not null) return session.Read(operation, relative, offset, count, cancellation);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var header = new byte[audio ? 28 : 48]; stream.ReadExactly(header);
            long size = audio ? FileMetadata.AudioTableLength(header, stream.Length) : FileMetadata.ModuleTableLength(header, stream.Length);
            if (payload) size = stream.Length;
            if (offset > size || count > size - offset) throw new CacheException("FORGE_READ_INVALID", "A Forge read exceeds the module tables.");
            stream.Position = offset; var bytes = new byte[count]; stream.ReadExactly(bytes);
            return new(bytes, stream.Length, File.GetLastWriteTimeUtc(path).ToFileTimeUtc());
        }
        catch (UnauthorizedAccessException)
        {
            session = PackageProcessSession.Attach(PackageTarget.Forge(request.ForgeRoot, request.PackageFullName), "h5sololauncher.ForgeReader.dll", "H5Read", cancellation);
            return session.Read(operation, relative, offset, count, cancellation);
        }
    }
    public CacheProbe Probe(string path, byte[] expected, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var root = SafePaths.Canonical(request.CacheRoot); path = SafePaths.Canonical(path); SafePaths.NoLinks(path);
        if (!SafePaths.Within(root, path) || expected.Length != 32) throw new CacheException("FORGE_PROBE_INVALID", "The cache probe is outside this cache.");
        VerifyInstallation(request);
        try
        {
            session ??= PackageProcessSession.Attach(PackageTarget.Forge(request.ForgeRoot, request.PackageFullName), "h5sololauncher.ForgeReader.dll", "H5Read", cancellation);
            var read = session.Read(2, path, 0, 32, cancellation);
            return read.Bytes.AsSpan().SequenceEqual(expected)
                ? new("Passed", "Forge read a new file in this cache folder. Generated game files will need a separate check.", Details: permissionDetails)
                : new("Failed", "Forge returned different probe data. Try preparing Forge data again.", "FORGE_PROBE_MISMATCH");
        }
        catch (CacheException e) when (e.Code == "FORGE_NOT_RUNNING") { return new("NotTested", e.Message, e.Code, permissionDetails); }
        catch (CacheException e) when (e.Code == "FORGE_READ_DENIED")
        { return new("Denied", request.AllowCacheRead
            ? "Forge still could not read this cache after adding its read rule. Copy the details; another folder restriction may be blocking access."
            : "Forge needs read access to this cache folder. Click Allow Forge to read cache, then the launcher will check again.",
            e.Code, e + "\n" + permissionDetails); }
    }
    public void Dispose() => session?.Dispose();
}

