using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MovieSift.Application.Jobs;
using MovieSift.Core.Failures;
using MovieSift.Infrastructure.Files;
using Xunit;

namespace MovieSift.Infrastructure.Tests;

/// <summary>Checks actual Linux permission preservation without changing ownership or elevating the application.</summary>
[SupportedOSPlatform("linux")]
public sealed class SourceReplacementPermissionTests
{
    public static bool IsLinux => OperatingSystem.IsLinux();
    public static bool IsPrivilegedLinux => OperatingSystem.IsLinux() && GetEffectiveUserId() == 0;

    /// <summary>A broadly readable candidate must acquire the original's ordinary permissions before publication.</summary>
    [Theory(SkipUnless = nameof(IsLinux), Skip = "Unix file permissions require Linux.")]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute)]
    public async Task ReplacementPreservesOrdinaryPermissionsAndOwnership(UnixFileMode mode)
    {
        using var workspace = new TestWorkspace();
        var fixture = await SourceReplacementTests.CreateAsync(workspace, (boundary, output) =>
        {
            if (boundary == ReplacementBoundary.BeforeReplace)
            {
                File.SetUnixFileMode(output.CandidatePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
        });
        var path = fixture.Plan.DestinationPath;
        File.SetUnixFileMode(path, mode);
        var original = Information(path);
        var result = await fixture.Jobs.RunAsync(new(fixture.Plan));
        var published = Information(path);

        Assert.Equal(JobPhase.Completed, result.Phase);
        Assert.Empty(result.CleanupIssues);
        Assert.Equal(mode, File.GetUnixFileMode(path));
        Assert.Equal(original.User, published.User);
        Assert.Equal(original.Group, published.Group);
        Assert.Equal(ReplacementBackend.Result, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    /// <summary>Setuid and setgid bits must not be copied onto newly generated content.</summary>
    [Theory(SkipUnless = nameof(IsLinux), Skip = "Unix file permissions require Linux.")]
    [InlineData(UnixFileMode.SetUser)]
    [InlineData(UnixFileMode.SetGroup)]
    public async Task SpecialPermissionBitsRejectReplacement(UnixFileMode special)
    {
        using var workspace = new TestWorkspace();
        var fixture = await SourceReplacementTests.CreateAsync(workspace);
        var path = fixture.Plan.DestinationPath;
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | special;
        File.SetUnixFileMode(path, mode);
        var result = await fixture.Jobs.RunAsync(new(fixture.Plan));

        Assert.Equal(ErrorCode.InvalidDestination, result.Failure?.Code);
        Assert.Equal(ErrorReason.ReplacementPermissions, result.Failure?.Reason);
        Assert.Null(result.PublishedPath);
        Assert.Equal(mode, File.GetUnixFileMode(path));
        Assert.Equal(ReplacementBackend.Original, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    /// <summary>Privileged test setup creates mismatched ownership; production code must fail instead of silently changing it.</summary>
    [Theory(SkipUnless = nameof(IsPrivilegedLinux), Skip = "Changing test-file owners requires a privileged Linux test process.")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DifferentOwnerOrGroupRejectsReplacement(bool changeOwner)
    {
        using var workspace = new TestWorkspace();
        var fixture = await SourceReplacementTests.CreateAsync(workspace);
        var path = fixture.Plan.DestinationPath;
        var before = Information(path);
        var owner = changeOwner ? 65534u : before.User;
        var group = changeOwner ? before.Group : 65534u;
        Assert.Equal(0, ChangeOwner(path, owner, group));
        // Keep this fixture writable so the independent read-only gate cannot hide the ownership mismatch under test.
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead |
            UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite;
        File.SetUnixFileMode(path, mode);
        var result = await fixture.Jobs.RunAsync(new(fixture.Plan));
        var after = Information(path);

        Assert.Equal(ErrorCode.InvalidDestination, result.Failure?.Code);
        Assert.Equal(ErrorReason.ReplacementPermissions, result.Failure?.Reason);
        Assert.Equal(owner, after.User);
        Assert.Equal(group, after.Group);
        Assert.Equal(mode, File.GetUnixFileMode(path));
        Assert.Equal(ReplacementBackend.Original, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    /// <summary>An actual named-user access ACL is retained on the unchanged source, rather than lost during replacement.</summary>
    [Fact(SkipUnless = nameof(IsLinux), Skip = "POSIX access ACLs require Linux.")]
    public async Task ExtendedAccessAclRejectsReplacementWithoutRemovingTheAcl()
    {
        using var workspace = new TestWorkspace();
        var fixture = await SourceReplacementTests.CreateAsync(workspace);
        var path = fixture.Plan.DestinationPath;
        var acl = AccessAcl();

        using (var stream = FileSourceAccess.OpenRegularReadStream(path))
        {
            Assert.Equal(0, SetExtendedAttribute(stream.SafeFileHandle.DangerousGetHandle().ToInt32(),
                "system.posix_acl_access", acl, (nuint)acl.Length, 0));
        }

        var result = await fixture.Jobs.RunAsync(new(fixture.Plan));
        Assert.Equal(ErrorCode.InvalidDestination, result.Failure?.Code);
        Assert.Equal(ErrorReason.ReplacementPermissions, result.Failure?.Reason);
        Assert.Equal(ReplacementBackend.Original, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        using var original = FileSourceAccess.OpenRegularReadStream(path);
        Assert.Equal((nint)acl.Length, NativeMethods.FGetXAttr(original.SafeFileHandle.DangerousGetHandle().ToInt32(),
            "system.posix_acl_access", IntPtr.Zero, 0));
    }

    /// <summary>Reads the existing native identity structure through a checked regular-file handle.</summary>
    private static LinuxFileInformation Information(string path)
    {
        using var stream = FileSourceAccess.OpenRegularReadStream(path);
        Assert.Equal(0, NativeMethods.FStat(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), out var information));
        return information;
    }

    /// <summary>Encodes Linux's version-two access ACL with one explicit named-user grant.</summary>
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

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int ChangeOwner([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint user, uint group);

    [DllImport("libc", EntryPoint = "fsetxattr", SetLastError = true)]
    private static extern int SetExtendedAttribute(int descriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        byte[] value, nuint size, int flags);
}
