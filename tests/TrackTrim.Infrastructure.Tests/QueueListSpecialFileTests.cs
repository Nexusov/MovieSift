using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TrackTrim.Application.QueueLists;
using TrackTrim.Infrastructure.Persistence;
using Xunit;

namespace TrackTrim.Infrastructure.Tests;

/// <summary>Uses actual special files to protect portable listing reads, lock acquisition and replacement.</summary>
public sealed class QueueListSpecialFileTests
{
    private const uint OwnerReadWritePermissions = 0x180;
    private const int LinuxReadWrite = 2;
    private const int LinuxNonBlocking = 0x800;
    private const int LinuxCloseOnExec = 0x80000;
    private static readonly TimeSpan FileTimeout = TimeSpan.FromSeconds(5);
    public static bool IsLinux => OperatingSystem.IsLinux();

    /// <summary>Rejects a directory in place of the record or lock without deleting any owned sentinel content.</summary>
    [Theory]
    [InlineData("record")]
    [InlineData("lock")]
    public async Task DirectoryCannotSubstituteForRegularRecordOrLock(string target)
    {
        using var workspace = new TestWorkspace();
        var name = target == "record" ? PortableQueueListStore.RecordFileName : PortableQueueListStore.LockFileName;
        var directory = Path.Combine(workspace.Path, name);
        Directory.CreateDirectory(directory);
        var sentinel = Path.Combine(directory, "preserve.txt");
        await File.WriteAllTextAsync(sentinel, "preserve", TestContext.Current.CancellationToken);
        var result = await new PortableQueueListStore(workspace.Path).WriteAsync(WriteRequest(workspace.Path));
        Assert.Equal(QueueListErrorCode.PersistenceUnavailable, result.Failure?.Code);
        Assert.False(result.Committed);
        Assert.Equal("preserve", await File.ReadAllTextAsync(sentinel, TestContext.Current.CancellationToken));

        if (target == "record")
        {
            var read = await new PortableQueueListStore(workspace.Path).ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(QueueListErrorCode.PersistenceUnavailable, read.Failure?.Code);
        }
    }

    /// <summary>A substituted FIFO cannot block startup, a read-modify-replace or the interprocess lock.</summary>
    [Theory(SkipUnless = nameof(IsLinux), Skip = "The queue-list FIFO regressions require Linux.")]
    [InlineData("record-read")]
    [InlineData("record-write")]
    [InlineData("lock-write")]
    public async Task FifoIsRejectedBeforeBlockingFileOperations(string operation)
    {
        using var workspace = new TestWorkspace();
        var name = operation == "lock-write" ? PortableQueueListStore.LockFileName : PortableQueueListStore.RecordFileName;
        var path = Path.Combine(workspace.Path, name);
        Assert.Equal(0, MakeFifo(path, OwnerReadWritePermissions));
        var store = new PortableQueueListStore(workspace.Path);
        var action = Task.Run(() => operation == "record-read"
            ? store.ReadAsync(TestContext.Current.CancellationToken)
            : store.WriteAsync(WriteRequest(workspace.Path)), CancellationToken.None);

        try
        {
            var result = await action.WaitAsync(FileTimeout, TestContext.Current.CancellationToken);
            Assert.Equal(QueueListErrorCode.PersistenceUnavailable, result.Failure?.Code);
            Assert.Null(result.List);
            Assert.False(result.Committed);
            Assert.True(File.Exists(path));
            Assert.Empty(Directory.GetFiles(workspace.Path, ".queue-list.*.tmp"));
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

    /// <summary>Record, lock and ancestor links preserve unrelated target bytes and never redirect a write.</summary>
    [Theory(SkipUnless = nameof(IsLinux), Skip = "The queue-list symlink regressions require Linux.")]
    [InlineData("record")]
    [InlineData("lock")]
    [InlineData("data")]
    public async Task RedirectedRecordLockOrDataDirectoryCannotAuthorizePersistence(string target)
    {
        using var workspace = new TestWorkspace();
        var data = Path.Combine(workspace.Path, "data");
        var unrelated = Path.Combine(workspace.Path, "unrelated");
        Directory.CreateDirectory(unrelated);
        var sentinel = Path.Combine(unrelated, "preserve.txt");
        await File.WriteAllTextAsync(sentinel, "preserve", TestContext.Current.CancellationToken);

        if (target == "data")
        {
            Directory.CreateSymbolicLink(data, unrelated);
        }

        if (target != "data")
        {
            Directory.CreateDirectory(data);
            var name = target == "record" ? PortableQueueListStore.RecordFileName : PortableQueueListStore.LockFileName;
            File.CreateSymbolicLink(Path.Combine(data, name), sentinel);
        }

        var store = new PortableQueueListStore(data);
        var write = await store.WriteAsync(WriteRequest(workspace.Path));
        Assert.Equal(QueueListErrorCode.PersistenceUnavailable, write.Failure?.Code);
        Assert.False(write.Committed);
        Assert.Equal("preserve", await File.ReadAllTextAsync(sentinel, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFileSystemEntries(unrelated));

        if (target != "lock")
        {
            var read = await store.ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(QueueListErrorCode.PersistenceUnavailable, read.Failure?.Code);
            Assert.Null(read.List);
        }
    }

    /// <summary>Uses valid listing data so each result isolates the substituted storage path.</summary>
    private static QueueListWriteRequest WriteRequest(string root) => new(new([new(Path.Combine(root, "source.mkv"), null)]),
        null, TestContext.Current.CancellationToken);

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
}
