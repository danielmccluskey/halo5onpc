using System.Buffers.Binary;
using System.IO;

namespace H5SoloLauncher.Tests;

/// <summary>Synthetic structural fixtures, never copied from a game installation.</summary>
internal sealed class ExportFixture : IDisposable
{
    private static readonly string FixtureParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "h5sololauncher-tests"));
    public string Root { get; } = Path.Combine(FixtureParent, Guid.NewGuid().ToString("N"), "fresh export 光环");

    public ExportFixture(bool xboxModules = true, bool audio = true, bool movies = true)
    {
        Write("AppxManifest.xml", System.Text.Encoding.UTF8.GetBytes(
            "<Package xmlns='http://schemas.microsoft.com/appx/2010/manifest'><Identity Name='Halo5-Guardians' Version='1.1.31695.21' /></Package>"));
        foreach (var platform in xboxModules ? new[] { "any", "x1" } : new[] { "any" })
        {
            Write($"deploy/{platform}/levels/globals-rtx-1.module", Module(23));
            Write($"deploy/{platform}/levels/campaignworld100/example/example-rtx-1.module", Module(27));
        }
        Write("__cms__/rtx/levels/campaignworld100/example/example.mapinfo", [1, 2, 3]);
        if (audio) Write("sound/english/soundbank.pck", [4, 5, 6]);
        if (movies) Write("bink/opening.bk2", [7, 8, 9]);
    }

    public void Write(string relative, byte[] data)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
    }

    public static byte[] Module(int revision)
    {
        var bytes = new byte[(revision == 27 ? 56 : 48) + 88];
        "mohd"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), revision);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 1);
        return bytes;
    }

    public void Dispose()
    {
        var owned = Path.GetFullPath(Path.GetDirectoryName(Root)!);
        if (Path.GetDirectoryName(owned) != FixtureParent)
            throw new InvalidOperationException("Refusing cleanup outside the test fixture directory.");
        if (Directory.Exists(owned)) Directory.Delete(owned, recursive: true);
    }
}
