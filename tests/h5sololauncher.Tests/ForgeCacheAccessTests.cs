using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using H5SoloLauncher.Core;
using H5SoloLauncher.Worker;
using Xunit;

namespace H5SoloLauncher.Tests;

public sealed class ForgeCacheAccessTests
{
    [Fact]
    public void GrantsOnlyForgeReadRightsAndPreservesSurroundingFoldersAndExistingRules()
    {
        using var f = new ForgePreparationTests.Fixture();
        var request = f.Request with { AllowCacheRead = true };
        var parent = Path.GetDirectoryName(request.CacheRoot)!;
        var beforeParent = Sddl(parent); var beforeSource = Sddl(request.SourceRoot); var beforeForge = Sddl(request.ForgeRoot);
        var beforeRoot = new DirectoryInfo(request.CacheRoot).GetAccessControl();
        var beforeRules = Rules(request.CacheRoot);
        var nested = Directory.CreateDirectory(Path.Combine(request.CacheRoot, "forge", "existing"));
        var existing = Path.Combine(nested.FullName, "existing.bin"); File.WriteAllText(existing, "test");
        var sid = ForgeCacheAccess.PackageSid();
        Assert.StartsWith("S-1-15-2-", sid.Value); Assert.NotEqual("S-1-15-2-1", sid.Value);
        Assert.Contains("added", ForgeCacheAccess.GrantRead(request, default));
        var rootRule = Assert.Single(Rules(request.CacheRoot), x => x.IdentityReference.Equals(sid) && !x.IsInherited);
        Assert.Equal(AccessControlType.Allow, rootRule.AccessControlType);
        Assert.Equal(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, rootRule.FileSystemRights);
        Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, rootRule.InheritanceFlags);
        Assert.Equal(PropagationFlags.None, rootRule.PropagationFlags);
        foreach (var original in beforeRules)
            Assert.Contains(Rules(request.CacheRoot), x => RuleKey(x) == RuleKey(original));
        Assert.Equal(beforeRoot.GetOwner(typeof(SecurityIdentifier)), new DirectoryInfo(request.CacheRoot).GetAccessControl().GetOwner(typeof(SecurityIdentifier)));
        Assert.Equal(beforeParent, Sddl(parent)); Assert.Equal(beforeSource, Sddl(request.SourceRoot)); Assert.Equal(beforeForge, Sddl(request.ForgeRoot));
        AssertReadOnlyInheritance(existing, sid);
        var fresh = Path.Combine(nested.FullName, "new-probe.bin"); File.WriteAllText(fresh, "test"); AssertReadOnlyInheritance(fresh, sid);
        var first = Sddl(request.CacheRoot); Assert.Contains("already present", ForgeCacheAccess.GrantRead(request, default)); Assert.Equal(first, Sddl(request.CacheRoot));
    }
    [Fact]
    public void OrdinaryPreparationCannotGrantAccessAndCancellationMakesNoChange()
    {
        using var f = new ForgePreparationTests.Fixture(); var before = Sddl(f.Request.CacheRoot);
        Assert.Equal("CACHE_PERMISSION_NOT_REQUESTED", Assert.Throws<CacheException>(() => ForgeCacheAccess.GrantRead(f.Request, default)).Code);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => ForgeCacheAccess.GrantRead(f.Request with { AllowCacheRead = true }, stop.Token));
        Assert.Equal(before, Sddl(f.Request.CacheRoot));
    }
    [Fact]
    public void UnownedOrOverlappingFolderIsRejectedBeforePermissionsChange()
    {
        using var f = new ForgePreparationTests.Fixture(); var before = Sddl(f.Request.CacheRoot);
        Assert.Throws<CacheException>(() => ForgeCacheAccess.GrantRead(f.Request with { AllowCacheRead = true, CacheRoot = f.Request.SourceRoot }, default));
        File.WriteAllText(Path.Combine(f.Request.CacheRoot, "cache.json"), "{}");
        Assert.Throws<CacheException>(() => ForgeCacheAccess.GrantRead(f.Request with { AllowCacheRead = true }, default)); Assert.Equal(before, Sddl(f.Request.CacheRoot));
    }
    [Fact]
    public void ExistingDenyAndProtectedInheritanceArePreserved()
    {
        using var f = new ForgePreparationTests.Fixture(); var sid = ForgeCacheAccess.PackageSid(); var directory = new DirectoryInfo(f.Request.CacheRoot);
        var acl = directory.GetAccessControl(); acl.SetAccessRuleProtection(true, preserveInheritance: true);
        acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ReadData, AccessControlType.Deny)); directory.SetAccessControl(acl);
        ForgeCacheAccess.GrantRead(f.Request with { AllowCacheRead = true }, default);
        Assert.True(directory.GetAccessControl().AreAccessRulesProtected);
        Assert.Contains(Rules(directory.FullName), x => x.IdentityReference.Equals(sid) && x.AccessControlType == AccessControlType.Deny && x.FileSystemRights.HasFlag(FileSystemRights.ReadData));
    }
    private static FileSystemAccessRule[] Rules(string path) => new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
        .GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
    private static string Sddl(string path) => new DirectoryInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group);
    private static string RuleKey(FileSystemAccessRule x) => $"{x.IdentityReference}|{x.AccessControlType}|{x.FileSystemRights}|{x.InheritanceFlags}|{x.PropagationFlags}|{x.IsInherited}";
    private static void AssertReadOnlyInheritance(string path, SecurityIdentifier sid)
    {
        var rules = new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();
        var rule = Assert.Single(rules, x => x.IdentityReference.Equals(sid));
        Assert.True(rule.IsInherited); Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, rule.FileSystemRights);
    }
}
