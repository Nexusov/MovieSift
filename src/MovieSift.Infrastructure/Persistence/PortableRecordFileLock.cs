using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using MovieSift.Infrastructure.Files;

namespace MovieSift.Infrastructure.Persistence;

/// <summary>Owns an exclusive nonblocking process lock on a permanent regular sibling file.</summary>
internal sealed class PortableRecordFileLock(SafeFileHandle handle) : IDisposable
{
    private const uint WindowsReadWrite = 0xC0000000;
    private const uint WindowsOpenAlways = 4;
    private const uint WindowsOpenReparsePoint = 0x00200000;
    private const uint WindowsDiskFile = 1;
    private const uint WindowsReparsePoint = 0x400;
    private const int WindowsSharingViolation = 32;
    private const int LinuxReadWrite = 2;
    private const int LinuxCreate = 0x40;
    private const int LinuxNonBlocking = 0x800;
    private const int LinuxNoFollow = 0x20000;
    private const int LinuxCloseOnExec = 0x80000;
    private const uint LinuxOwnerReadWrite = 0x180;
    private const uint LinuxTypeMask = 0xF000;
    private const uint LinuxRegularFile = 0x8000;
    private const int LinuxExclusiveNonBlockingLock = 2 | 4;
    private const int LinuxUnlock = 8;
    private const int LinuxWouldBlock = 11;
    private int disposed;

    /// <summary>Rejects redirected paths and unsafe handles before acquiring a fail-fast OS lock.</summary>
    internal static PortableRecordFileLock Acquire(string path, bool shared = false)
    {
        PathSafety.RejectReparsePoints(Path.GetDirectoryName(path)!);

        if (OperatingSystem.IsWindows())
        {
            return AcquireWindows(path, shared);
        }

        if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            return AcquireLinux(path, shared);
        }

        throw Unavailable("Portable record locking supports only Windows x64 and Linux x64.");
    }

    /// <summary>Unlocks the owned description before close so a forked descriptor cannot retain a completed operation's lock.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsLinux())
            {
                _ = Flock(handle, LinuxUnlock);
            }
        }
        finally
        {
            handle.Dispose();
        }
    }

    /// <summary>Uses an exclusive Windows handle while opening the link itself for safe type validation.</summary>
    private static PortableRecordFileLock AcquireWindows(string path, bool shared)
    {
        var opened = CreateFile(path, WindowsReadWrite, shared ? 3u : 0u, IntPtr.Zero, WindowsOpenAlways,
            WindowsOpenReparsePoint, IntPtr.Zero);

        if (opened.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            opened.Dispose();
            throw error == WindowsSharingViolation ? Busy() : Unavailable($"Could not open the portable record lock (OS error {error}).");
        }

        try
        {
            var validInformation = NativeMethods.GetFileInformationByHandle(opened, out var information);
            var regular = NativeMethods.GetFileType(opened) == WindowsDiskFile && validInformation &&
                (information.Attributes & (uint)FileAttributes.Directory) == 0 &&
                (information.Attributes & WindowsReparsePoint) == 0;

            if (!regular)
            {
                throw Unavailable("The portable record lock must be a regular file without redirection.");
            }

            return new(opened);
        }
        catch
        {
            opened.Dispose();
            throw;
        }
    }

    /// <summary>Opens without FIFO blocking or final-link traversal and locks the verified regular inode.</summary>
    private static PortableRecordFileLock AcquireLinux(string path, bool shared)
    {
        var descriptor = Open(path, LinuxReadWrite | LinuxCreate | LinuxNonBlocking | LinuxNoFollow | LinuxCloseOnExec,
            LinuxOwnerReadWrite);

        if (descriptor < 0)
        {
            throw Unavailable($"Could not open the portable record lock (OS error {Marshal.GetLastPInvokeError()}).");
        }

        var opened = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);

        try
        {
            var regular = NativeMethods.FStat(descriptor, out var information) == 0 &&
                (information.Mode & LinuxTypeMask) == LinuxRegularFile;

            if (!regular)
            {
                throw Unavailable("The portable record lock must be a regular file without redirection.");
            }

            if (Flock(opened, shared ? 1 | 4 : LinuxExclusiveNonBlockingLock) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                throw error == LinuxWouldBlock ? Busy() : Unavailable($"Could not acquire the portable record lock (OS error {error}).");
            }

            return new(opened);
        }
        catch
        {
            opened.Dispose();
            throw;
        }
    }

    /// <summary>Identifies contention separately from unsupported or unsafe persistence.</summary>
    private static PortableRecordLockException Busy() => new(true,
        "Another process is updating this portable record; retry the operation.");

    /// <summary>Preserves an actionable operating-system or file-type failure.</summary>
    private static PortableRecordLockException Unavailable(string detail) => new(false, detail);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint sharing, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(SafeFileHandle descriptor, int operation);
}
