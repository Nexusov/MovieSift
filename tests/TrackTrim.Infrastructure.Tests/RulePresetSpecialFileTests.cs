using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TrackTrim.Application.Presets;
using TrackTrim.Core.Rules;
using TrackTrim.Infrastructure.Persistence;
using Xunit;

namespace TrackTrim.Infrastructure.Tests;

/// <summary>Exercises mandatory Linux FIFO and symlink boundaries without leaving a blocked test worker behind.</summary>
public sealed class RulePresetSpecialFileTests
{
    private const uint OwnerReadWritePermissions = 0x180;
    private const int LinuxReadWrite = 2;
    private const int LinuxNonBlocking = 0x800;
    private const int LinuxCloseOnExec = 0x80000;
    private static readonly TimeSpan FileTimeout = TimeSpan.FromSeconds(5);
    public static bool IsLinux => OperatingSystem.IsLinux();

    /// <summary>Rejects special files before reading or locking and explicitly joins a regressed blocking task.</summary>
    [Theory(SkipUnless = nameof(IsLinux), Skip = "The preset FIFO regressions require Linux.")]
    [InlineData("catalog-read")]
    [InlineData("catalog-change")]
    [InlineData("lock-change")]
    public async Task RejectsFifoWithoutBlockingCatalogOperations(string operation)
    {
        using var workspace = new TestWorkspace();
        var name = operation == "lock-change" ? "rule-presets.lock" : "rule-presets.json";
        var path = Path.Combine(workspace.Path, name);
        Assert.Equal(0, MakeFifo(path, OwnerReadWritePermissions));
        var store = new PortableRulePresetStore(workspace.Path);
        var action = Task.Run(() => operation == "catalog-read"
            ? store.ReadAsync(TestContext.Current.CancellationToken)
            : store.ChangeAsync(AddMutation()), CancellationToken.None);

        try
        {
            var result = await action.WaitAsync(FileTimeout, TestContext.Current.CancellationToken);
            Assert.Equal(RulePresetErrorCode.PersistenceUnavailable, result.Failure?.Code);
            Assert.Null(result.Catalog);
            Assert.False(result.Committed);
            Assert.True(File.Exists(path));
            Assert.Empty(Directory.GetFiles(workspace.Path, ".rule-presets.*.tmp"));
        }
        finally
        {
            if (!action.IsCompleted)
            {
                await ReleaseAndJoinAsync(new(path, action));
            }
        }
    }

    /// <summary>Catalog and lock links cannot authorize reads, overwrites or writes through another directory.</summary>
    [Theory(SkipUnless = nameof(IsLinux), Skip = "The preset symlink regressions require Linux.")]
    [InlineData("catalog")]
    [InlineData("lock")]
    [InlineData("data")]
    public async Task RejectsRedirectedCatalogLockOrDataDirectory(string redirected)
    {
        using var workspace = new TestWorkspace();
        var data = Path.Combine(workspace.Path, "data");
        var target = Path.Combine(workspace.Path, "unrelated");
        Directory.CreateDirectory(target);
        var targetFile = Path.Combine(target, "preserve.txt");
        await File.WriteAllTextAsync(targetFile, "unrelated original", TestContext.Current.CancellationToken);

        if (redirected == "data")
        {
            Directory.CreateSymbolicLink(data, target);
        }

        if (redirected != "data")
        {
            Directory.CreateDirectory(data);
            var name = redirected == "lock" ? "rule-presets.lock" : "rule-presets.json";
            File.CreateSymbolicLink(Path.Combine(data, name), targetFile);
        }

        var store = new PortableRulePresetStore(data);
        var result = await store.ChangeAsync(AddMutation());
        Assert.Equal(RulePresetErrorCode.PersistenceUnavailable, result.Failure?.Code);
        Assert.False(result.Committed);
        Assert.Equal("unrelated original", await File.ReadAllTextAsync(targetFile, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFileSystemEntries(target));

        if (redirected != "lock")
        {
            var read = await store.ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(RulePresetErrorCode.PersistenceUnavailable, read.Failure?.Code);
            Assert.Null(read.Catalog);
        }
    }

    /// <summary>Builds a valid bounded intent so only the filesystem substitution causes the failure.</summary>
    private static RulePresetMutation AddMutation() => new(RulePresetMutationKind.Add, Guid.NewGuid(), "New",
        new LanguageRules(new(LanguageRuleMode.Unchanged, []), new(LanguageRuleMode.Unchanged, [])),
        TestContext.Current.CancellationToken);

    /// <summary>Releases an old blocking FIFO implementation and awaits its completion before teardown.</summary>
    private static async Task ReleaseAndJoinAsync(BlockedOperation request)
    {
        var descriptor = Open(request.Path, LinuxReadWrite | LinuxNonBlocking | LinuxCloseOnExec);
        Assert.True(descriptor >= 0, $"Could not release the blocked FIFO operation (OS error {Marshal.GetLastPInvokeError()}).");
        using var release = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        var failure = await Record.ExceptionAsync(() => request.Operation.WaitAsync(FileTimeout, CancellationToken.None));
        Assert.IsNotType<TimeoutException>(failure);
        Assert.True(request.Operation.IsCompleted, "The bounded FIFO operation must be joined before leaving the test.");
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    private sealed record BlockedOperation(string Path, Task Operation);
}
