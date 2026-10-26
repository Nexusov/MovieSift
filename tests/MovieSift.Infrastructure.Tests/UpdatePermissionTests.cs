using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using MovieSift.Application.Updates;
using MovieSift.Infrastructure.Files;
using MovieSift.Infrastructure.Updates;
using Xunit;
using InstallationFixture = MovieSift.Infrastructure.Tests.PortableUpdateInstallerTests.InstallationFixture;

namespace MovieSift.Infrastructure.Tests;

/// <summary>Checks that moving an updated portable tree cannot broaden access to retained user data.</summary>
public sealed class UpdatePermissionTests
{
    private const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PublicDirectoryMode = PrivateDirectoryMode | UnixFileMode.GroupRead |
        UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
    private const UnixFileMode PublicFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite |
        UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    public static bool IsLinux => OperatingSystem.IsLinux();
    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsPrivilegedLinux => OperatingSystem.IsLinux() && GetEffectiveUserId() == 0;

    /// <summary>Both a private application root and a private nested directory must still protect ordinary readable files.</summary>
    [SupportedOSPlatform("linux")]
    [Theory(SkipUnless = nameof(IsLinux), Skip = "Unix directory permissions require Linux.")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InstallationRetainsPrivateDirectoryPermissions(bool privateRoot)
    {
        using var fixture = new InstallationFixture();
        await fixture.PrepareAsync();
        var privateDirectory = Path.Combine(fixture.App, "private");
        Directory.CreateDirectory(privateDirectory);
        var userFile = Path.Combine(privateDirectory, "personal.txt");
        await File.WriteAllTextAsync(userFile, "private user content", TestContext.Current.CancellationToken);
        var rootMode = privateRoot ? PrivateDirectoryMode : PublicDirectoryMode;
        var nestedMode = privateRoot ? PublicDirectoryMode : PrivateDirectoryMode;
        File.SetUnixFileMode(fixture.App, rootMode);
        File.SetUnixFileMode(privateDirectory, nestedMode);
        File.SetUnixFileMode(userFile, PublicFileMode);
        File.SetUnixFileMode(Path.Combine(fixture.Session, "payload"), PublicDirectoryMode);

        await PortableUpdateInstaller.ApplyAsync(fixture.Session, fixture.Plan!, Directory.Move, TestContext.Current.CancellationToken);

        Assert.Equal(rootMode, File.GetUnixFileMode(fixture.App));
        Assert.Equal(nestedMode, File.GetUnixFileMode(privateDirectory));
        Assert.Equal(PublicFileMode, File.GetUnixFileMode(userFile));
        Assert.Equal("private user content", await File.ReadAllTextAsync(userFile, TestContext.Current.CancellationToken));
        Assert.Equal("new version", await File.ReadAllTextAsync(Path.Combine(fixture.App, "version.txt"), TestContext.Current.CancellationToken));
        var previous = Path.Combine(fixture.Session, "previous");
        Assert.Equal(rootMode, File.GetUnixFileMode(previous));
        Assert.Equal(nestedMode, File.GetUnixFileMode(Path.Combine(previous, "private")));
    }

    /// <summary>Unsupported ownership stops installation rather than changing the owners of preserved user data.</summary>
    [SupportedOSPlatform("linux")]
    [Theory(SkipUnless = nameof(IsPrivilegedLinux), Skip = "Changing test-file owners requires a privileged Linux test process.")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DifferentUserFileOwnerOrGroupPreventsDirectorySwitch(bool changeOwner)
    {
        using var fixture = new InstallationFixture();
        await fixture.PrepareAsync();
        var path = Path.Combine(fixture.App, "personal.txt");
        var before = Information(path);
        var user = changeOwner ? 65534u : before.User;
        var group = changeOwner ? before.Group : 65534u;
        Assert.Equal(0, ChangeOwner(path, user, group));
        File.SetUnixFileMode(path, PublicFileMode);

        await AssertRejectedBeforeMoveAsync(fixture);

        var after = Information(path);
        Assert.Equal(user, after.User);
        Assert.Equal(group, after.Group);
        Assert.Equal(PublicFileMode, File.GetUnixFileMode(path));
        Assert.Equal("user file", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    /// <summary>An actual extended access ACL or privileged mode cannot be silently dropped during preservation.</summary>
    [SupportedOSPlatform("linux")]
    [Theory(SkipUnless = nameof(IsLinux), Skip = "POSIX ACLs and Unix permission bits require Linux.")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnsupportedUserFilePermissionsPreventDirectorySwitch(bool extendedAcl)
    {
        using var fixture = new InstallationFixture();
        await fixture.PrepareAsync();
        var path = Path.Combine(fixture.App, "personal.txt");
        var acl = AccessAcl();
        var specialMode = PublicFileMode | UnixFileMode.SetUser;
        if (extendedAcl)
        {
            using var stream = FileSourceAccess.OpenRegularReadStream(path);
            Assert.Equal(0, SetExtendedAttribute(stream.SafeFileHandle.DangerousGetHandle().ToInt32(),
                "system.posix_acl_access", acl, (nuint)acl.Length, 0));
        }
        else
        {
            File.SetUnixFileMode(path, specialMode);
        }

        await AssertRejectedBeforeMoveAsync(fixture);

        Assert.Equal("user file", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        if (extendedAcl)
        {
            using var original = FileSourceAccess.OpenRegularReadStream(path);
            Assert.Equal((nint)acl.Length, NativeMethods.FGetXAttr(original.SafeFileHandle.DangerousGetHandle().ToInt32(),
                "system.posix_acl_access", IntPtr.Zero, 0));
        }
        else
        {
            Assert.Equal(specialMode, File.GetUnixFileMode(path));
        }
    }

    /// <summary>Explicit and inherited Windows access rules survive for the root, user directories and user files.</summary>
    [SupportedOSPlatform("windows")]
    [Fact(SkipUnless = nameof(IsWindows), Skip = "Windows access-control preservation requires Windows.")]
    public async Task InstallationRetainsWindowsOwnersGroupsAndProtectedAccessRules()
    {
        using var fixture = new InstallationFixture();
        await fixture.PrepareAsync();
        using var identity = WindowsIdentity.GetCurrent();
        var user = Assert.IsType<SecurityIdentifier>(identity.User);
        MakePrivate(fixture.App, user);
        var privateDirectory = Path.Combine(fixture.App, "private");
        Directory.CreateDirectory(privateDirectory);
        MakePrivate(privateDirectory, user);
        var inheritedDirectory = Path.Combine(fixture.App, "inherited");
        Directory.CreateDirectory(inheritedDirectory);
        var inheritedFile = Path.Combine(inheritedDirectory, "notes.txt");
        await File.WriteAllTextAsync(inheritedFile, "inherited private content", TestContext.Current.CancellationToken);
        var explicitFile = Path.Combine(fixture.App, "personal.txt");
        MakePrivate(explicitFile, user);
        Assert.Contains(ReadSecurity(inheritedDirectory).GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
            rule => rule.IsInherited);
        Assert.Contains(ReadSecurity(inheritedFile).GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
            rule => rule.IsInherited);
        var relativePaths = new[] { string.Empty, "private", "inherited", "personal.txt", Path.Combine("inherited", "notes.txt"), "data", Path.Combine("data", "settings.json") };
        var before = relativePaths.ToDictionary(relative => relative, relative => Snapshot(ReadSecurity(Path.Combine(fixture.App, relative))));

        await PortableUpdateInstaller.ApplyAsync(fixture.Session, fixture.Plan!, Directory.Move, TestContext.Current.CancellationToken);

        foreach (var relative in relativePaths)
        {
            var security = ReadSecurity(Path.Combine(fixture.App, relative));
            var after = Snapshot(security);
            Assert.True(security.AreAccessRulesProtected);
            Assert.Equal(before[relative].Owner, after.Owner);
            Assert.Equal(before[relative].Group, after.Group);
            Assert.Equal(before[relative].Rules, after.Rules);
            Assert.All(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
                rule => Assert.False(rule.IsInherited));
        }
        Assert.Equal("user file", await File.ReadAllTextAsync(explicitFile, TestContext.Current.CancellationToken));
        Assert.Equal("inherited private content", await File.ReadAllTextAsync(inheritedFile, TestContext.Current.CancellationToken));
        Assert.Equal("personal preferences", await File.ReadAllTextAsync(Path.Combine(fixture.App, "data", "settings.json"), TestContext.Current.CancellationToken));
        Assert.Equal("new version", await File.ReadAllTextAsync(Path.Combine(fixture.App, "version.txt"), TestContext.Current.CancellationToken));
    }

    /// <summary>Asserts the refusal happens while the complete previous installation is still at its original location.</summary>
    private static async Task AssertRejectedBeforeMoveAsync(InstallationFixture fixture)
    {
        var moves = 0;
        void ObserveMove(string source, string destination)
        {
            moves++;
            Directory.Move(source, destination);
        }

        var error = await Assert.ThrowsAsync<UpdateException>(() => PortableUpdateInstaller.ApplyAsync(
            fixture.Session, fixture.Plan!, ObserveMove, TestContext.Current.CancellationToken));
        Assert.Equal(UpdateErrorCode.UnsupportedInstallation, error.Code);
        Assert.Equal(0, moves);
        Assert.False(Directory.Exists(Path.Combine(fixture.Session, "previous")));
        Assert.Equal("old version", await File.ReadAllTextAsync(Path.Combine(fixture.App, "version.txt"), TestContext.Current.CancellationToken));
        Assert.Equal("personal preferences", await File.ReadAllTextAsync(Path.Combine(fixture.App, "data", "settings.json"), TestContext.Current.CancellationToken));
    }

    /// <summary>Reads Linux owner/group identity through the existing checked regular-file boundary.</summary>
    [SupportedOSPlatform("linux")]
    private static LinuxFileInformation Information(string path)
    {
        using var stream = FileSourceAccess.OpenRegularReadStream(path);
        Assert.Equal(0, NativeMethods.FStat(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), out var information));
        return information;
    }

    /// <summary>Creates a real Linux access ACL with an additional named-user grant.</summary>
    private static byte[] AccessAcl()
    {
        var entries = new (ushort Tag, ushort Permissions, uint Identity)[]
        {
            (1, 6, uint.MaxValue), (2, 4, 65534), (4, 0, uint.MaxValue), (16, 4, uint.MaxValue), (32, 0, uint.MaxValue)
        };
        var bytes = new byte[4 + entries.Length * 8];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 2);
        for (var index = 0; index < entries.Length; index++)
        {
            var target = bytes.AsSpan(4 + index * 8, 8);
            BinaryPrimitives.WriteUInt16LittleEndian(target, entries[index].Tag);
            BinaryPrimitives.WriteUInt16LittleEndian(target[2..], entries[index].Permissions);
            BinaryPrimitives.WriteUInt32LittleEndian(target[4..], entries[index].Identity);
        }
        return bytes;
    }

    /// <summary>Reads Access, Owner and Group without requiring auditing privileges.</summary>
    [SupportedOSPlatform("windows")]
    private static FileSystemSecurity ReadSecurity(string path) => Directory.Exists(path)
        ? FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path), AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group)
        : FileSystemAclExtensions.GetAccessControl(new FileInfo(path), AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group);

    /// <summary>Sets one protected DACL with both an allow and a nonmatching deny, keeping the existing owner and group.</summary>
    [SupportedOSPlatform("windows")]
    private static void MakePrivate(string path, SecurityIdentifier user)
    {
        var security = ReadSecurity(path);
        security.SetAccessRuleProtection(true, false);
        foreach (var rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>())
        {
            security.RemoveAccessRuleSpecific(rule);
        }
        var inheritance = Directory.Exists(path) ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.AnonymousSid, null),
            FileSystemRights.ReadData, inheritance, PropagationFlags.None, AccessControlType.Deny));
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, inheritance,
            PropagationFlags.None, AccessControlType.Allow));
        if (security is DirectorySecurity directory)
        {
            FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path), directory);
        }
        else
        {
            FileSystemAclExtensions.SetAccessControl(new FileInfo(path), (FileSecurity)security);
        }
    }

    /// <summary>Compares access semantics independently of the deliberate inherited-to-explicit conversion.</summary>
    [SupportedOSPlatform("windows")]
    private static AccessSnapshot Snapshot(FileSystemSecurity security) => new(
        security.GetOwner(typeof(SecurityIdentifier))!.Value,
        security.GetGroup(typeof(SecurityIdentifier))!.Value,
        security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(rule => $"{rule.IdentityReference.Value}|{rule.AccessControlType}|{rule.FileSystemRights}|{rule.InheritanceFlags}|{rule.PropagationFlags}")
            .Order(StringComparer.Ordinal).ToArray());

    private sealed record AccessSnapshot(string Owner, string Group, string[] Rules);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int ChangeOwner([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint user, uint group);

    [DllImport("libc", EntryPoint = "fsetxattr", SetLastError = true)]
    private static extern int SetExtendedAttribute(int descriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        byte[] value, nuint size, int flags);
}
