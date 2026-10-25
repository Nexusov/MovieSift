using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using MovieSift.Application;
using MovieSift.Core.Failures;
using MovieSift.Infrastructure.MediaTools;
using Xunit;

namespace MovieSift.Infrastructure.Tests;

/// <summary>Checks the real file boundary of the narrow Segment Info reader.</summary>
public sealed class SegmentInfoFileTests
{
    private const uint OwnerReadWritePermissions = 0x180;
    private const int LinuxReadWrite = 2;
    private const int LinuxNonBlocking = 0x800;
    private const int LinuxCloseOnExec = 0x80000;
    private static readonly TimeSpan ReaderTimeout = TimeSpan.FromSeconds(5);
    public static bool IsLinux => OperatingSystem.IsLinux();

    /// <summary>Rejects a substituted FIFO promptly and reaps an older blocking implementation on failure.</summary>
    [Fact(SkipUnless = nameof(IsLinux), Skip = "The FIFO reader regression requires Linux.")]
    public async Task RejectsFifoWithoutBlockingTheInspectionWorker()
    {
        using var workspace = new TestWorkspace();
        var fifoPath = Path.Combine(workspace.Path, "substituted-source.mkv");
        Assert.Equal(0, MakeFifo(fifoPath, OwnerReadWritePermissions));
        var cancellationToken = TestContext.Current.CancellationToken;
        var inspection = Task.Run(() => MkvSegmentInfoReader.ValidateAsync(new(fifoPath, cancellationToken)), CancellationToken.None);

        try
        {
            var failure = await Assert.ThrowsAsync<OperationFailureException>(() =>
                inspection.WaitAsync(ReaderTimeout, cancellationToken));
            Assert.Equal(ErrorCode.InvalidSource, failure.Failure.Code);
        }
        finally
        {
            if (!inspection.IsCompleted)
            {
                await ReleaseBlockedReaderAsync(new(fifoPath, inspection));
            }
        }
    }

    /// <summary>Unblocks a regression's pending FIFO open and observes its failure before test teardown.</summary>
    private static async Task ReleaseBlockedReaderAsync(FifoReader request)
    {
        // O_RDWR succeeds even if the queued reader has not entered open() yet; the descriptor is test-only.
        var descriptor = Open(request.Path, LinuxReadWrite | LinuxNonBlocking | LinuxCloseOnExec);
        Assert.True(descriptor >= 0, $"Could not release the blocked FIFO reader (OS error {Marshal.GetLastPInvokeError()}).");
        using var releaseHandle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        var failure = await Record.ExceptionAsync(() => request.Inspection.WaitAsync(ReaderTimeout, CancellationToken.None));
        Assert.IsNotType<TimeoutException>(failure);
        Assert.True(request.Inspection.IsCompleted, "The FIFO inspection worker must be observed before leaving the test.");
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    private sealed record FifoReader(string Path, Task Inspection);
}
