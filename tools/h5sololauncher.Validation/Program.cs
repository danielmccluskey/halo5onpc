using System.Security.Cryptography;
using System.Text.Json;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Conversion;
using H5SoloLauncher.Core.Forge;

if(args.Length==8 && args[0]=="--offline-inputs")
    return OfflineInputs.Run(args);
if(args.Length==9 && args[0]=="--offline-audio")
    return OfflineInputs.Audio(args);
if(args.Length==6 && args[0]=="--seal-cache")
    return OfflineInputs.Seal(args);
if(args.Length==3 && args[0]=="--offline-finish")
    return OfflineInputs.Finish(args);
if(args.Length==4 && args[0]=="--offline-legacy")
    return OfflineInputs.Legacy(args);

if(args.Length==4 && args[0]=="--bitmap-shapes")
{
    var metadata=FileMetadata.Read(args[1],"Module",default);using var stream=File.OpenRead(args[1]);var wanted=args[3].Split(',');HashSet<string> lines=[];
    foreach(var entry in Enumerable.Range(0,metadata.ItemCount).Select(metadata.Entry).Where(x=>x.Group=="bitm" && wanted.Contains(x.TagId)))
    {
        var tag=new TagDocument(ModulePayloadReader.Read(stream,metadata,entry,default).Bytes);var images=tag.Block(tag.Structures.Single(x=>x.FieldBlock==0 && x.FieldOffset==240).Target);
        for(var i=0;i<entry.ResourceCount;i++)
        {
            var resource=metadata.Entry(metadata.Resource(entry.ResourceIndex+i));var shape=BitmapDescriptor.Read(images.Slice(i*40,40),new TagDocument(ModulePayloadReader.ReadResource(stream,metadata,resource,default).Bytes,true));
            lines.Add($"{shape.Width} {shape.Height} {shape.Depth} {shape.Kind} {shape.Format} {shape.Mips} {shape.TileMode} {shape.FormatInfo.Dxgi} {shape.FormatInfo.Bytes} {shape.FormatInfo.Block}");
        }
    }
    File.WriteAllLines(args[2],lines.Order());Console.WriteLine($"{lines.Count} shapes exported.");return 0;
}

if (args.Length == 5 && args[0] == "--extract-tag")
{
    var metadata = FileMetadata.Read(args[1], "Module", default);
    var entry = Enumerable.Range(0, metadata.ItemCount).Select(metadata.Entry).Single(x => x.Group == args[2] && x.TagId == args[3] && x.StoredSize > 0);
    using var stream = File.OpenRead(args[1]); var bytes = ModulePayloadReader.Read(stream, metadata, entry, default).Bytes;
    File.WriteAllBytes(args[4], bytes); Console.WriteLine(JsonSerializer.Serialize(new { entry.Index, entry.Name, entry.Group, entry.TagId, entry.AssetId, Bytes = bytes.Length })); return 0;
}

if (args.Length == 3 && args[0] == "--audio-controls")
{
    var converter = new AudioConverter(); List<object> proof = []; var failures = 0;
    foreach (var expectedFile in Directory.EnumerateFiles(args[1], "*.expected.bnk"))
    {
        var file = expectedFile.Replace(".expected.bnk", ".source.bnk");
        try
        {
            var source = File.ReadAllBytes(file); var expected = File.ReadAllBytes(file.Replace(".source.bnk", ".expected.bnk"));
            var converted = converter.Bank(source, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(12)));
            var equal = converted.Bytes.AsSpan().SequenceEqual(expected); if (!equal) failures++;
            proof.Add(new { File = file, Passed = equal, converted.Media, converted.PreservedXma });
        }
        catch (Exception e) { failures++; proof.Add(new { File = file, Passed = false, Error = e.Message }); }
    }
    File.WriteAllBytes(args[2], JsonSerializer.SerializeToUtf8Bytes(proof)); Console.WriteLine($"{proof.Count - failures} passed; {failures} failed.");
    return failures == 0 && proof.Count > 0 ? 0 : 1;
}

if (args.Length == 3 && args[0] == "--inspect-tree")
{
    var module = FileMetadata.Read(args[1], "Module", default); Queue<int> pending = new([int.Parse(args[2])]); HashSet<int> seen = [];
    while (pending.TryDequeue(out var index))
    {
        if (!seen.Add(index)) throw new InvalidDataException("Cycle or shared child.");
        var entry = module.Entry(index); var children = Enumerable.Range(entry.ResourceIndex, entry.ResourceCount).Select(module.Resource).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new { entry.Index, entry.Name, entry.Parent, entry.ResourceCount, entry.StoredSize, entry.LogicalSize, entry.AssetId, entry.Checksum, entry.TagId, entry.Group, Children = children }));
        foreach (var child in children) pending.Enqueue(child);
    }
    return 0;
}

if (args.Length == 4 && args[0] == "--layout-controls")
{
    var layoutControls = JsonSerializer.Deserialize<LayoutControl[]>(File.ReadAllBytes(args[1])) ?? throw new InvalidDataException();
    var snapshot = JsonSerializer.Deserialize<NativeSchemaSnapshot>(File.ReadAllBytes(args[2])) ?? throw new InvalidDataException();
    var converter = new StructuralConverter(snapshot); List<object> reports = []; var failures = 0;
    foreach (var control in layoutControls)
    {
        try
        {
            byte[] Read(ControlFile file)
            {
                var bytes = File.ReadAllBytes(file.Path);
                if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Control hash differs.");
                return bytes;
            }
            var source = Read(control.Source); var native = Read(control.Native);
            var result = converter.Convert(control.Group, new TagDocument(source));
            var equal = result.Bytes.Length == native.Length && result.Bytes.AsSpan(0, 16).SequenceEqual(native.AsSpan(0, 16)) && result.Bytes.AsSpan(24).SequenceEqual(native.AsSpan(24));
            if (!equal) failures++;
            reports.Add(new { control.Group, control.Id, State = equal ? "Passed" : "Mismatch", result.Method });
        }
        catch (Exception e) { failures++; reports.Add(new { control.Group, control.Id, State = "Failed", Error = e.Message }); }
    }
    File.WriteAllBytes(args[3], JsonSerializer.SerializeToUtf8Bytes(reports, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"{layoutControls.Length - failures} passed; {failures} failed.");
    return failures == 0 ? 0 : 1;
}

if (args.Length != 3 || args[0] != "--texture-controls")
{ Console.Error.WriteLine("Expected --texture-controls <control manifest> <output report>"); return 2; }
var controls = JsonSerializer.Deserialize<TextureControl[]>(File.ReadAllBytes(args[1])) ?? throw new InvalidDataException();
List<TextureControlResult> results = [];
foreach (var control in controls)
{
    try
    {
        byte[] Read(ControlFile file)
        {
            var bytes = File.ReadAllBytes(file.Path);
            if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != file.Sha256.ToLowerInvariant()) throw new InvalidDataException("Control file hash differs: " + file.Path);
            return bytes;
        }
        var source = control.Source.Select(Read).ToArray(); var native = control.Native.Select(Read).ToArray();
        var tag = new TagDocument(source[0]); var nativeTag = new TagDocument(native[0]);
        var images = tag.Block(tag.Structures.Single(x => x.FieldBlock == 0 && x.FieldOffset == 240).Target);
        var nativeImages = nativeTag.Block(nativeTag.Structures.Single(x => x.FieldBlock == 0 && x.FieldOffset == 240).Target);
        if (!images.SequenceEqual(nativeImages)) { results.Add(new(control.Id, "DifferentAuthoredImages", null, null)); continue; }
        if (images.Length != 40) throw new InvalidDataException("Control fixture must contain one image.");
        var converted = BitmapConverter.Convert(images, new TagDocument(source[1], resource: true), source.Skip(2).ToArray(), TextureAddresses.BuiltIn);
        var nativeResource = native[1]; var result = converted.Resource;
        var rewritten = tag.WithSchema(BitmapConverter.NativeTagSchema);
        var resourceEqual = result.Length == nativeResource.Length && result.AsSpan(0, 16).SequenceEqual(nativeResource.AsSpan(0, 16)) && result.AsSpan(24).SequenceEqual(nativeResource.AsSpan(24));
        var tagEqual = rewritten.Length == native[0].Length && rewritten.AsSpan(0, 16).SequenceEqual(native[0].AsSpan(0, 16)) && rewritten.AsSpan(28).SequenceEqual(native[0].AsSpan(28));
        var chunksEqual = converted.Chunks.Length == native.Length - 2 && converted.Chunks.Select((x, i) => x.AsSpan().SequenceEqual(native[i + 2])).All(x => x);
        results.Add(new(control.Id, resourceEqual && tagEqual && chunksEqual ? "Passed" : "Mismatch", converted.Descriptor,
            $"Tag={tagEqual}; resource={resourceEqual}; chunks={chunksEqual}; converted SHA256={Convert.ToHexString(SHA256.HashData(result))}"));
    }
    catch (Exception e) { results.Add(new(control.Id, e is CacheException known ? known.Code : "Failed", null, e.Message)); }
}
File.WriteAllBytes(args[2], JsonSerializer.SerializeToUtf8Bytes(results, new JsonSerializerOptions { WriteIndented = true }));
foreach (var group in results.GroupBy(x => x.State)) Console.WriteLine(group.Key + ": " + group.Count());
foreach (var result in results.Where(x => x.State != "Passed")) Console.WriteLine(JsonSerializer.Serialize(result));
return results.All(x => x.State == "Passed") ? 0 : 1;

sealed record ControlFile(string Path, string Sha256);
sealed record TextureControl(string Id, ControlFile[] Source, ControlFile[] Native);
sealed record TextureControlResult(string Id, string State, BitmapDescriptor? Shape, string? Details);
sealed record LayoutControl(string Group, string Id, ControlFile Source, ControlFile Native);
