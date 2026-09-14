using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Planning;
using H5SoloLauncher.Core.Storage;
using Xunit;

namespace H5SoloLauncher.Tests;

internal sealed class PlanFixture : IDisposable
{
    public IndexFixture Files { get; } = new();
    public static AssetReference Model => new("mode", "000000c9", "00000000000007d1");
    public static AssetReference Missing => new("bitm", "000003e7", "000000000000270f");
    public PlanRequest Request => new(Files.Root, Files.Cache, Language: "English(US)");
    public string RootModule => "deploy/x1/" + ContentBundles.All[0].Scenarios[0] + "-rtx-1.module";

    public PlanFixture(bool missing = false, bool variants = false, bool resource = false)
    {
        var scenarios = ContentBundles.All[0].Scenarios;
        for (var i = 0; i < scenarios.Length; i++)
        {
            AssetReference[] deps = i == 0 ? [Model, new("scnr", "00000065", "00000000000003ea")]
                : i == 1 ? [new("scnr", "00000064", "00000000000003e9")] : [];
            if (missing && i == 0) deps = [..deps, Missing];
            Files.Write("deploy/x1/" + scenarios[i] + "-rtx-1.module",
                Module(new TestTag(scenarios[i].Replace('/', '\\') + ".scenario", "scnr", (uint)(100 + i), (ulong)(1001 + i), 1, Header(deps))));
        }
        var model = new TestTag("objects\\example.render_model", "mode", 201, 2001, 1, Header([]), Resource: resource ? 1 : -1);
        Files.Write("deploy/any/levels/globals-rtx-1.module", resource
            ? Module(model, new("mesh", "????", uint.MaxValue, ulong.MaxValue, 2, [], Parent: 0)) : Module(model));
        if (variants) Files.Write("deploy/any/levels/globals-rtx-20-1.module", Module(model with { Checksum = 2, Resource = -1, Payload = Header([]).Concat(new byte[] { 0 }).ToArray() }));
        Files.Write("sound/Durango/English(US)/voice.pck", IndexFixture.Audio());
        CacheFolders.Select(Files.Destination, Files.Root, null);
        Reindex();
    }
    public void Reindex() => Assert.Equal("Indexed", new SourceIndexer().Run(new(Files.Root, Files.Cache), new Callback(_ => { }), default).State);
    public PlanResult Run(Action<IndexProgress>? report = null, CancellationToken cancellation = default) =>
        new BuildPlanner().Run(Request, new Callback(report ?? (_ => { })), cancellation);
    public SourceBuildPlan Read(PlanResult result) => System.Text.Json.JsonSerializer.Deserialize<SourceBuildPlan>(
        File.ReadAllText(SafePaths.Child(Files.Cache, result.Summary!.RelativePath)))!;
    public void Dispose() => Files.Dispose();
    internal sealed class Callback(Action<IndexProgress> action) : IProgress<IndexProgress> { public void Report(IndexProgress value) => action(value); }

    internal sealed record TestTag(string Name, string Group, uint Id, ulong Asset, ulong Checksum, byte[] Payload,
        bool Compressed = true, int Parent = -1, int Resource = -1);
    public static byte[] Header(AssetReference[] dependencies)
    {
        var bytes = new byte[80 + dependencies.Length * 24]; "ucsh"u8.CopyTo(bytes);
        IndexFixture.I(bytes, 28, dependencies.Length); IndexFixture.I(bytes, 60, bytes.Length);
        for (var i = 0; i < dependencies.Length; i++)
        {
            var at = 80 + i * 24; var d = dependencies[i]; Encoding.ASCII.GetBytes(new string(d.Group.Reverse().ToArray())).CopyTo(bytes, at);
            IndexFixture.I(bytes, at + 4, -1); U64(bytes, at + 8, Convert.ToUInt64(d.AssetId, 16));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 16), Convert.ToUInt32(d.TagId, 16));
        }
        return bytes;
    }
    public static byte[] Module(params TestTag[] entries)
    {
        var names = entries.Select(x => Encoding.UTF8.GetBytes(x.Name + '\0')).ToArray();
        var stored = entries.Select(x => x.Payload.Length > 0 && x.Compressed ? Zip(x.Payload) : x.Payload).ToArray();
        var resources = entries.Where(x => x.Resource >= 0).Select(x => x.Resource).ToArray();
        var payloadAt = 48 + entries.Length * 88 + names.Sum(x => x.Length) + resources.Length * 4;
        var result = new byte[payloadAt + stored.Sum(x => x.Length)]; "mohd"u8.CopyTo(result);
        IndexFixture.I(result, 4, 23); IndexFixture.I(result, 16, entries.Length); IndexFixture.I(result, 28, names.Sum(x => x.Length)); IndexFixture.I(result, 32, resources.Length);
        int nameAt = 0, dataAt = 0, resourceAt = 0;
        for (var i = 0; i < entries.Length; i++)
        {
            var tag = entries[i]; var at = 48 + i * 88;
            IndexFixture.I(result, at, nameAt); IndexFixture.I(result, at + 4, tag.Parent);
            IndexFixture.I(result, at + 8, tag.Resource >= 0 ? 1 : 0); IndexFixture.I(result, at + 12, resourceAt);
            if (tag.Resource >= 0) resourceAt++;
            U64(result, at + 24, (ulong)dataAt); IndexFixture.I(result, at + 32, stored[i].Length); IndexFixture.I(result, at + 36, tag.Payload.Length);
            result[at + 43] = tag.Compressed ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(at + 44), tag.Id); U64(result, at + 48, tag.Asset); U64(result, at + 56, tag.Checksum);
            Encoding.ASCII.GetBytes(new string(tag.Group.Reverse().ToArray())).CopyTo(result, at + 64);
            IndexFixture.I(result, at + 68, tag.Payload.Length);
            names[i].CopyTo(result, 48 + entries.Length * 88 + nameAt); nameAt += names[i].Length;
            stored[i].CopyTo(result, payloadAt + dataAt); dataAt += stored[i].Length;
        }
        for (var i = 0; i < resources.Length; i++) IndexFixture.I(result, payloadAt - resources.Length * 4 + i * 4, resources[i]);
        return result;
    }
    private static void U64(byte[] b, int at, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(at), value);
    private static byte[] Zip(byte[] b) { using var m = new MemoryStream(); using (var z = new ZLibStream(m, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(b); return m.ToArray(); }
}
