using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using MovieSift.Infrastructure.Persistence;
using Xunit;

namespace MovieSift.Infrastructure.Tests;

/// <summary>Models Linux fork inheritance deterministically without creating a managed child process or retrying contention.</summary>
public sealed class PortableRecordFileLockTests
{
    public static bool IsLinux => OperatingSystem.IsLinux();

    /// <summary>Disposal releases the lock even while a duplicated descriptor retains the same open-file description.</summary>
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Descriptor inheritance of flock locks requires Linux.")]
    public void DisposeUnlocksTheOwnedDescriptionBeforeClosingItsDescriptor()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Path, "portable-record.lock");
        using var original = PortableRecordFileLock.Acquire(path);
        var descriptor = FindOwnedDescriptor(path);
        var duplicateDescriptor = Duplicate(descriptor);
        Assert.True(duplicateDescriptor >= 0, $"Could not duplicate the owned descriptor (OS error {Marshal.GetLastPInvokeError()}).");
        using var retained = new SafeFileHandle((IntPtr)duplicateDescriptor, ownsHandle: true);

        // dup and fork retain the same open-file description; close alone does not release its flock.
        original.Dispose();
        using var next = PortableRecordFileLock.Acquire(path);
        Assert.False(retained.IsClosed);

        original.Dispose();
        var contention = Assert.Throws<PortableRecordLockException>(() => PortableRecordFileLock.Acquire(path));
        Assert.True(contention.IsBusy);
    }

    /// <summary>Reads the actual unique lock descriptor through Linux procfs instead of exposing a production test hook.</summary>
    private static int FindOwnedDescriptor(string path)
    {
        foreach (var entry in Directory.GetFileSystemEntries("/proc/self/fd"))
        {
            string? target;

            try
            {
                target = new FileInfo(entry).LinkTarget;
            }
            catch (IOException)
            {
                // Unrelated runtime descriptors may close after the bounded directory snapshot.
                continue;
            }

            if (string.Equals(target, path, StringComparison.Ordinal))
            {
                return int.Parse(Path.GetFileName(entry), CultureInfo.InvariantCulture);
            }
        }

        throw new InvalidOperationException("The acquired unique portable lock descriptor was not visible in procfs.");
    }

    [DllImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static extern int Duplicate(int descriptor);
}
