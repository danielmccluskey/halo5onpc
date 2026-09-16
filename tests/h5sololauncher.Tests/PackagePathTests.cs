using System.Diagnostics;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Storage;
using Xunit;

namespace H5SoloLauncher.Tests;

public sealed class PackagePathTests
{
    [Fact]
    public void RegisteredPackageJunctionAllowsFilesButRejectsLinkedChildren()
    {
        var work = Path.Combine(Path.GetTempPath(), "h5-package-path-" + Guid.NewGuid().ToString("N"));
        var physical = Path.Combine(work, "physical");
        var registered = Path.Combine(work, "registered");
        Directory.CreateDirectory(physical);
        try
        {
            using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{registered}\" \"{physical}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            mklink.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);

            var executable = Path.Combine(physical, "halo5forge.exe");
            File.WriteAllBytes(executable, [1]);
            Assert.Equal(Path.Combine(registered, "halo5forge.exe"), SafePaths.PackageChild(registered, "halo5forge.exe"));
            Assert.True(SafePaths.PackageChildMatches(registered, "halo5forge.exe", executable));
            Assert.True(SafePaths.PackageChildMatches(registered, "halo5forge.exe", Path.Combine(registered, "halo5forge.exe")));

            var nested = Path.Combine(physical, "nested");
            using var nestedLink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{nested}\" \"{work}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            nestedLink.WaitForExit();
            Assert.Equal(0, nestedLink.ExitCode);
            Assert.Equal("PATH_LINKED", Assert.Throws<CacheException>(() => SafePaths.PackageChild(registered, "nested/file.bin")).Code);
        }
        finally
        {
            if (Directory.Exists(registered)) Directory.Delete(registered);
            if (Directory.Exists(Path.Combine(physical, "nested"))) Directory.Delete(Path.Combine(physical, "nested"));
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }
}
