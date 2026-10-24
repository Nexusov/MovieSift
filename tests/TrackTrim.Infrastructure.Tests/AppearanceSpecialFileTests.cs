using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TrackTrim.Application.Appearance;
using TrackTrim.Infrastructure.Persistence;
using Xunit;

namespace TrackTrim.Infrastructure.Tests;

/// <summary>Applies the existing nonblocking regular-record boundary to portable appearance files and locks.</summary>
public sealed class AppearanceSpecialFileTests
{
    private const uint OwnerReadWritePermissions = 0x180;
    private const int LinuxReadWrite = 2;
    private const int LinuxNonBlocking = 0x800;
    private const int LinuxCloseOnExec = 0x80000;
    private static readonly TimeSpan FileTimeout = TimeSpan.FromSeconds(5);
    public static bool IsLinux => OperatingSystem.IsLinux();

    /// <summary>A FIFO cannot block startup or redirect a cosmetic preference write.</summary>
    [Theory(SkipUnless = nameof(IsLinux), Skip = "Appearance FIFO regressions require Linux.")]
    [InlineData("record")]
    [InlineData("lock")]
    public async Task FifoIsRejectedWithoutBlocking(string kind)
    {
        using var workspace = new TestWorkspace();
        var name = kind == "record" ? PortableAppearanceStore.FileName : PortableAppearanceStore.LockFileName;
        var path = Path.Combine(workspace.Path, name);
        Assert.Equal(0, MakeFifo(path, OwnerReadWritePermissions));
        var store = new PortableAppearanceStore(workspace.Path);
        var action = Task.Run(async () =>
        {
            if (kind == "record")
            {
                Assert.Equal(AppearanceErrorCode.PersistenceUnavailable, (await store.LoadAsync(TestContext.Current.CancellationToken)).Failure?.Code);
            }

            var saved = await store.SaveAsync(new(AppearanceSettings.Default, ReplaceInvalidData: true));
            Assert.False(saved.Saved);
            Assert.Equal(AppearanceErrorCode.PersistenceUnavailable, saved.Failure?.Code);
        }, CancellationToken.None);

        try
        {
            await action.WaitAsync(FileTimeout, TestContext.Current.CancellationToken);
            Assert.True(File.Exists(path));
            Assert.Empty(Directory.GetFiles(workspace.Path, ".appearance.*.tmp"));
        }
        finally
        {
            if (!action.IsCompleted)
            {
                var descriptor = Open(path, LinuxReadWrite | LinuxNonBlocking | LinuxCloseOnExec);
                Assert.True(descriptor >= 0);
                using var release = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
                var failure = await Record.ExceptionAsync(() => action.WaitAsync(FileTimeout, CancellationToken.None));
                Assert.IsNotType<TimeoutException>(failure);
                Assert.True(action.IsCompleted);
            }
        }
    }

    /// <summary>Redirected appearance, lock and data paths cannot modify unrelated targets, including explicit reset.</summary>
    [Theory(SkipUnless = nameof(IsLinux), Skip = "Appearance symlink regressions require Linux.")]
    [InlineData("record")]
    [InlineData("lock")]
    [InlineData("data")]
    public async Task RedirectedPathsPreserveUnrelatedFiles(string kind)
    {
        using var workspace = new TestWorkspace();
        var target = Path.Combine(workspace.Path, "unrelated");
        var data = Path.Combine(workspace.Path, "data");
        Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "preserve.txt");
        await File.WriteAllTextAsync(sentinel, "unchanged", TestContext.Current.CancellationToken);

        if (kind == "data")
        {
            Directory.CreateSymbolicLink(data, target);
        }

        if (kind != "data")
        {
            Directory.CreateDirectory(data);
            var name = kind == "record" ? PortableAppearanceStore.FileName : PortableAppearanceStore.LockFileName;
            File.CreateSymbolicLink(Path.Combine(data, name), sentinel);
        }

        var store = new PortableAppearanceStore(data);
        var saved = await store.SaveAsync(new(AppearanceSettings.Default, ReplaceInvalidData: true));
        Assert.False(saved.Saved);
        Assert.Equal(AppearanceErrorCode.PersistenceUnavailable, saved.Failure?.Code);
        Assert.Equal("unchanged", await File.ReadAllTextAsync(sentinel, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFileSystemEntries(target));
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
}
