using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using MovieSift.Application;
using MovieSift.Application.Ports;
using MovieSift.Core.Failures;

namespace MovieSift.Infrastructure.Files;

/// <summary>Retains the identity of one atomically created reservation directory until durable handoff or rollback.</summary>
internal sealed class ReservationDirectory : IDisposable
{
    private const uint OwnerPermissions = 0x1C0;
    private const int OpenDirectory = 0x10000;
    private const int NoFollow = 0x20000;
    private const int CloseOnExec = 0x80000;
    private const uint UnixFileTypeMask = 0xF000;
    private const uint UnixDirectory = 0x4000;
    private const uint WindowsShareReadWriteDelete = 7;
    private const uint WindowsOpenExisting = 3;
    private const uint WindowsDirectoryFlags = 0x02200000;
    private const int LinuxEntryExists = 17;
    private const int WindowsFileExists = 80;
    private const int WindowsEntryExists = 183;
    private const string WindowsExtendedPathPrefix = @"\\?\";
    private readonly SafeFileHandle handle;
    private readonly string identity;

    /// <summary>Captures identity from the retained directory handle rather than from a filename prefix.</summary>
    private ReservationDirectory(string path)
    {
        handle = OpenHandle(path);

        try
        {
            identity = ReadIdentity(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Fails on an already existing path instead of adopting another operation's directory.</summary>
    internal static ReservationDirectory CreateNew(string path)
    {
        var created = OperatingSystem.IsWindows()
            ? CreateDirectory(WindowsPath(path), IntPtr.Zero) : MakeDirectory(path, OwnerPermissions) == 0;

        if (!created)
        {
            var error = Marshal.GetLastPInvokeError();
            var existingEntry = OperatingSystem.IsWindows()
                ? error is WindowsFileExists or WindowsEntryExists : error == LinuxEntryExists;
            throw new OperationFailureException(new(existingEntry ? ErrorCode.OwnershipInvalid : ErrorCode.PersistenceUnavailable,
                $"Could not create a new private reservation directory (OS error {error})."));
        }

        try
        {
            return new(path);
        }
        catch (Exception exception)
        {
            throw new OperationFailureException(new(ErrorCode.PersistenceUnavailable, exception.Message),
                [new CleanupIssue(path, "The newly created directory identity could not be retained; the path was preserved.")], exception);
        }
    }

    /// <summary>Rejects redirection or replacement before a rollback deletes any directory content.</summary>
    internal void ValidateCurrent(string path)
    {
        PathSafety.RejectReparsePoints(path);
        using var current = OpenHandle(path);

        if (ReadIdentity(current) != identity || ReadIdentity(handle) != identity)
        {
            throw new IOException("The reservation directory no longer identifies this operation's directory; it was preserved.");
        }
    }

    /// <summary>Closes only the retained metadata handle, without deleting any path.</summary>
    public void Dispose() => handle.Dispose();

    /// <summary>Opens directory metadata without following the final redirected component.</summary>
    private static SafeFileHandle OpenHandle(string path)
    {
        var result = OperatingSystem.IsWindows()
            ? CreateFile(WindowsPath(path), 0, WindowsShareReadWriteDelete, IntPtr.Zero, WindowsOpenExisting, WindowsDirectoryFlags, IntPtr.Zero)
            : new SafeFileHandle((IntPtr)NativeMethods.Open(path, OpenDirectory | NoFollow | CloseOnExec), ownsHandle: true);

        if (result.IsInvalid)
        {
            result.Dispose();
            throw new IOException($"Could not open reservation directory metadata (OS error {Marshal.GetLastPInvokeError()}).");
        }

        return result;
    }

    /// <summary>Retains .NET's local long-path behavior when crossing the direct Windows directory APIs.</summary>
    private static string WindowsPath(string path) => path.StartsWith(WindowsExtendedPathPrefix, StringComparison.Ordinal)
        ? path : WindowsExtendedPathPrefix + path;

    /// <summary>Uses the existing platform identity structures while requiring an ordinary directory.</summary>
    private static string ReadIdentity(SafeFileHandle directory)
    {
        if (OperatingSystem.IsWindows())
        {
            var valid = NativeMethods.GetFileInformationByHandle(directory, out var information) &&
                ((FileAttributes)information.Attributes).HasFlag(FileAttributes.Directory) &&
                !((FileAttributes)information.Attributes).HasFlag(FileAttributes.ReparsePoint);

            if (!valid)
            {
                throw new IOException("The private reservation path is not an ordinary directory.");
            }

            return string.Create(CultureInfo.InvariantCulture,
                $"{information.VolumeSerialNumber:X8}:{information.FileIndexHigh:X8}{information.FileIndexLow:X8}");
        }

        var validLinux = NativeMethods.FStat(directory.DangerousGetHandle().ToInt32(), out var linux) == 0 &&
            (linux.Mode & UnixFileTypeMask) == UnixDirectory;

        if (!validLinux)
        {
            throw new IOException("The private reservation path is not an ordinary directory.");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{linux.Device:X}:{linux.Inode:X}");
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, IntPtr securityAttributes);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr securityAttributes,
        uint creation, uint flags, IntPtr template);

    [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    private static extern int MakeDirectory([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
}
