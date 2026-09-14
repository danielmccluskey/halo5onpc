using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using H5SoloLauncher.Models;
using H5SoloLauncher.Services;
using Xunit;

namespace H5SoloLauncher.Tests;

public sealed class CampaignDumpValidatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecognizesStandaloneExportWithoutChangingOrCopyingItsFiles(bool selectDeploy)
    {
        using var fixture = new ExportFixture();
        var before = Snapshot(fixture.Root);
        var result = await new CampaignDumpValidator().ValidateAsync(selectDeploy ? Path.Combine(fixture.Root, "deploy") : fixture.Root);
        Assert.Equal(CampaignDumpStatus.Recognized, result.Status);
        Assert.Equal(fixture.Root, result.Dump!.RootDirectory);
        Assert.Equal("1.1.31695.21", result.Dump.PackageVersion);
        Assert.Equal(4, result.Dump.ModuleCount);
        Assert.Equal(1, result.Dump.CampaignMetadataCount);
        Assert.Equal(before, Snapshot(fixture.Root));
    }

    [Fact]
    public async Task ReportsMissingXboxModulesInsteadOfAcceptingAPcCache()
    {
        using var fixture = new ExportFixture(xboxModules: false);
        var result = await new CampaignDumpValidator().ValidateAsync(fixture.Root);
        Assert.Equal(CampaignDumpStatus.Incomplete, result.Status);
        Assert.Contains(result.Problems!, problem => problem.Contains("deploy/x1/levels"));
    }

    [Theory]
    [InlineData(false, true, "audio")]
    [InlineData(true, false, "movie")]
    public async Task ReportsMissingMedia(bool audio, bool movies, string expected)
    {
        using var fixture = new ExportFixture(audio: audio, movies: movies);
        var result = await new CampaignDumpValidator().ValidateAsync(fixture.Root);
        Assert.Equal(CampaignDumpStatus.Incomplete, result.Status);
        Assert.Contains(result.Problems!, problem => problem.Contains(expected));
    }

    [Theory]
    [InlineData("<Package><Identity Name='Microsoft.Halo5Forge' Version='1.0.0.0'/></Package>", CampaignDumpStatus.WrongFolder)]
    [InlineData("<Package><Identity Name='Halo5-Guardians' Version='bad'/></Package>", CampaignDumpStatus.Incomplete)]
    [InlineData("<Package", CampaignDumpStatus.UnsupportedFormat)]
    [InlineData("<!DOCTYPE Package [<!ENTITY data SYSTEM 'file:///does-not-exist'>]><Package>&data;</Package>", CampaignDumpStatus.UnsupportedFormat)]
    public async Task RefusesWrongOrMalformedManifests(string manifest, CampaignDumpStatus expected)
    {
        using var fixture = new ExportFixture();
        fixture.Write("AppxManifest.xml", Encoding.UTF8.GetBytes(manifest));
        var result = await new CampaignDumpValidator().ValidateAsync(fixture.Root);
        Assert.Equal(expected, result.Status);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("magic")]
    [InlineData("revision")]
    [InlineData("tables")]
    public async Task RefusesUnreadableModuleHeaders(string defect)
    {
        using var fixture = new ExportFixture();
        var bytes = ExportFixture.Module(23);
        if (defect == "truncated") bytes = bytes[..12];
        if (defect == "magic") bytes[0] = 0;
        if (defect == "revision") BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 99);
        if (defect == "tables") BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), int.MaxValue);
        fixture.Write("deploy/x1/levels/globals-rtx-1.module", bytes);
        var result = await new CampaignDumpValidator().ValidateAsync(fixture.Root);
        Assert.Equal(CampaignDumpStatus.UnsupportedFormat, result.Status);
        Assert.Contains(result.Problems!, problem => problem.Contains("globals-rtx-1.module"));
    }

    [Fact]
    public async Task MissingDirectoryHasAnActionableResult()
    {
        using var fixture = new ExportFixture();
        var result = await new CampaignDumpValidator().ValidateAsync(Path.Combine(fixture.Root, "unavailable export"));
        Assert.Equal(CampaignDumpStatus.MissingFolder, result.Status);
        Assert.Contains("Choose the export again", result.Problems![0]);
    }

    [Fact]
    public async Task CancellationDoesNotProduceASuccessOrFailureResult()
    {
        using var fixture = new ExportFixture();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CampaignDumpValidator()
            .ValidateAsync(fixture.Root, new CancellationToken(canceled: true)));
    }

    private static string[] Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Order().Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
}
