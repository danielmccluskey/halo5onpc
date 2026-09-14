using System.Buffers.Binary;
using System.IO;
using System.Text;
using H5SoloLauncher.Core.Storage;

namespace H5SoloLauncher.Tests;

internal sealed class IndexFixture : IDisposable
{
    private readonly string owned = Path.Combine(Path.GetTempPath(), "h5sololauncher-tests", Guid.NewGuid().ToString("N"));
    public string Root { get; }
    public string Destination { get; }
    public string ModulePath => Path.Combine(Root, "deploy/any/levels/globals-rtx-1.module");
    public string Cache => Path.Combine(Destination, CacheFolders.FolderName);

    public IndexFixture()
    {
        Root = Path.Combine(owned, "dump 光环", "Mount"); Destination = Path.Combine(owned, "cache choice");
        Directory.CreateDirectory(Destination);
        Write("AppxManifest.xml", Encoding.UTF8.GetBytes("<Package><Identity Name='Halo5-Guardians' Version='1.1.31695.21'/></Package>"));
        Write("version.txt", "synthetic\n"u8.ToArray());
        Write("deploy/any/levels/globals-rtx-1.module", Module(23));
        Write("deploy/x1/levels/campaignworld010/example/example-rtx-1.module", Module(27));
        Write("__cms__/rtx/levels/campaignworld010/example/example.mapinfo", [3, 4, 5]);
        Write("sound/english/soundbank.pck", Audio());
        Write("bink/opening.bk2", [9, 8, 7]);
    }

    public void Write(string relative, byte[] bytes)
    { var path = Path.Combine(Root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes); }

    public static byte[] Module(int revision, bool sectionOnly = false)
    {
        var header = revision == 27 ? 56 : 48; var block = revision == 27 ? 32 : 20;
        var names = "example.scenario\0"u8.ToArray();
        var result = new byte[header + 88 + names.Length + block + 4];
        "mohd"u8.CopyTo(result); I(result, 4, revision); I(result, 16, 1); I(result, 28, names.Length); I(result, 36, 1);
        I(result, header + 4, -1); I(result, header + 16, 1);
        I(result, header + 32, sectionOnly ? 0 : 4); I(result, header + 36, sectionOnly ? 0 : 4);
        I(result, header + 44, 123); I(result, header + 48, 456); I(result, header + 56, 789);
        "rncs"u8.CopyTo(result.AsSpan(header + 64)); I(result, header + 68, 4);
        names.CopyTo(result, header + 88);
        var descriptor = header + 88 + names.Length + (revision == 27 ? 8 : 0);
        I(result, descriptor + 4, 4); I(result, descriptor + 12, 4);
        result[^4] = 1; result[^3] = 2; result[^2] = 3; result[^1] = 4;
        return result;
    }

    public static byte[] Audio()
    {
        // Empty language table, one bank, zero media and external records.
        var bytes = new byte[64]; "AKPK"u8.CopyTo(bytes);
        I(bytes, 4, 52); I(bytes, 8, 1); I(bytes, 16, 24); I(bytes, 20, 4); I(bytes, 24, 4);
        I(bytes, 28, 1); I(bytes, 32, 42); I(bytes, 36, 1); I(bytes, 40, 4); I(bytes, 44, 60);
        return bytes;
    }
    public static void I(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);
    public void Dispose()
    {
        var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "h5sololauncher-tests"));
        if (Path.GetDirectoryName(Path.GetFullPath(owned)) != expected) throw new InvalidOperationException("Invalid fixture cleanup target.");
        if (Directory.Exists(owned)) Directory.Delete(owned, true);
    }
}
