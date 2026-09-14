using System.Buffers.Binary;
using System.IO;
using System.Xml;
using System.Xml.Linq;
using H5SoloLauncher.Models;
using H5SoloLauncher.Core.Content;

namespace H5SoloLauncher.Services;

/// <summary>Read-only structural checks against the selected Xbox export itself.</summary>
public sealed class CampaignDumpValidator : ICampaignDumpValidator
{
    public Task<CampaignDumpCheckResult> ValidateAsync(string directory, CancellationToken cancellation = default) =>
        Task.Run(() => Validate(directory, cancellation), cancellation);

    private static CampaignDumpCheckResult Validate(string directory, CancellationToken cancellation)
    {
        var root = directory;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            root = SourceDiscovery.ResolveRoot(directory);
            // Accept the export root or its deploy folder, never search unrelated directories.
            if (string.Equals(Path.GetFileName(root), "deploy", StringComparison.OrdinalIgnoreCase))
                root = Path.GetDirectoryName(root) ?? root;

            // GetAttributes preserves permission errors, unlike Directory.Exists.
            if (!File.GetAttributes(root).HasFlag(FileAttributes.Directory))
                return new(CampaignDumpStatus.WrongFolder, root, Problems: ["Choose an extracted game folder."]);

            var manifestPath = Path.Combine(root, "AppxManifest.xml");
            if (!File.Exists(manifestPath))
                return new(CampaignDumpStatus.WrongFolder, root,
                    Problems: ["AppxManifest.xml was not found. Choose the extracted Halo 5: Guardians game folder."]);

            using var reader = XmlReader.Create(manifestPath, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 1024 * 1024
            });
            var manifest = XDocument.Load(reader);
            var package = manifest.Root;
            var identity = package?.Elements().SingleOrDefault(element => element.Name.LocalName == "Identity");
            if (package?.Name.LocalName != "Package" || (string?)identity?.Attribute("Name") != "Halo5-Guardians")
                return new(CampaignDumpStatus.WrongFolder, root,
                    Problems: ["This manifest does not identify a Halo 5: Guardians Xbox export."]);

            var versionText = (string?)identity?.Attribute("Version");
            if (!Version.TryParse(versionText, out var version))
                return new(CampaignDumpStatus.Incomplete, root, Problems: ["The game manifest has no readable package version."]);

            List<string> problems = [];
            var moduleCount = 0;
            var unsupportedModule = false;
            foreach (var platform in new[] { "any", "x1" })
            {
                var levels = Path.Combine(root, "deploy", platform, "levels");
                var modules = Files(levels, "*.module", cancellation).ToArray();
                moduleCount += modules.Length;
                if (!modules.Any(path => string.Equals(Path.GetDirectoryName(path), levels, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(path).StartsWith("globals-", StringComparison.OrdinalIgnoreCase)))
                    problems.Add($"Missing global modules under deploy/{platform}/levels.");
                if (!modules.Any(path => IsCampaignPath(levels, path)))
                    problems.Add($"Missing campaign modules under deploy/{platform}/levels.");

                foreach (var path in modules)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var problem = CheckModuleHeader(path);
                    if (problem is null) continue;
                    unsupportedModule = true;
                    if (problems.Count < 12) problems.Add($"{Path.GetRelativePath(root, path)}: {problem}");
                }
            }

            var metadataRoot = Path.Combine(root, "__cms__", "rtx", "levels");
            var metadata = Files(metadataRoot, "*.mapinfo", cancellation)
                .Where(path => IsCampaignPath(metadataRoot, path) && new FileInfo(path).Length > 0).Count();
            if (metadata == 0) problems.Add("Missing campaign .mapinfo files under __cms__/rtx/levels.");
            var audio = Files(Path.Combine(root, "sound"), "*.pck", cancellation).Count(path => new FileInfo(path).Length > 0);
            if (audio == 0) problems.Add("Missing audio .pck files under sound.");
            var movies = Files(Path.Combine(root, "bink"), "*.bk2", cancellation).Count(path => new FileInfo(path).Length > 0);
            if (movies == 0) problems.Add("Missing movie .bk2 files under bink.");

            var dump = new CampaignDumpInfo(root, version.ToString(), moduleCount, metadata, audio, movies);
            return new(unsupportedModule ? CampaignDumpStatus.UnsupportedFormat :
                problems.Count > 0 ? CampaignDumpStatus.Incomplete : CampaignDumpStatus.Recognized,
                root, dump, problems);
        }
        catch (OperationCanceledException) { throw; }
        catch (DirectoryNotFoundException exception)
        {
            return new(CampaignDumpStatus.MissingFolder, root,
                Problems: ["The selected folder or a file being checked is no longer available. Choose the export again."],
                ErrorDetails: exception.ToString());
        }
        catch (FileNotFoundException exception)
        {
            return new(CampaignDumpStatus.MissingFolder, root,
                Problems: ["The selected folder or a file being checked is no longer available. Choose the export again."],
                ErrorDetails: exception.ToString());
        }
        catch (XmlException exception)
        {
            return new(CampaignDumpStatus.UnsupportedFormat, root,
                Problems: ["AppxManifest.xml could not be read as a game manifest."], ErrorDetails: exception.ToString());
        }
        catch (Exception exception)
        {
            return new(CampaignDumpStatus.CheckFailed, root,
                Problems: ["The export could not be read. Check the folder is accessible and try again."],
                ErrorDetails: exception.ToString());
        }
    }

    private static IEnumerable<string> Files(string directory, string pattern, CancellationToken cancellation)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(directory); }
        catch (DirectoryNotFoundException) { yield break; }
        catch (FileNotFoundException) { yield break; }
        if (!attributes.HasFlag(FileAttributes.Directory)) yield break;
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException($"Choose an export with real content folders instead of a linked folder: {directory}");

        foreach (var path in Directory.EnumerateFiles(directory, pattern, new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.CaseInsensitive
        }))
        {
            cancellation.ThrowIfCancellationRequested();
            yield return path;
        }
    }

    private static bool IsCampaignPath(string levels, string file) =>
        Path.GetRelativePath(levels, file).Split(Path.DirectorySeparatorChar)[0]
            .StartsWith("campaign", StringComparison.OrdinalIgnoreCase);

    private static string? CheckModuleHeader(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[48];
        if (stream.Length < header.Length) return "module header is truncated.";
        stream.ReadExactly(header);
        if (!header[..4].SequenceEqual("mohd"u8)) return "not a readable module; check that the export was fully extracted.";
        var revision = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        if (revision is not (23 or 27)) return $"module revision {revision} is not recognized.";
        var items = BinaryPrimitives.ReadInt32LittleEndian(header[16..]);
        var strings = BinaryPrimitives.ReadInt32LittleEndian(header[28..]);
        var resources = BinaryPrimitives.ReadInt32LittleEndian(header[32..]);
        var blocks = BinaryPrimitives.ReadInt32LittleEndian(header[36..]);
        if (items < 0 || strings < 0 || resources < 0 || blocks < 0) return "module table sizes are invalid.";
        var tableBytes = (revision == 27 ? 56L : 48L) + items * 88L + strings + resources * 4L + blocks * (revision == 27 ? 32L : 20L);
        return tableBytes > stream.Length ? "module tables are truncated." : null;
    }
}
