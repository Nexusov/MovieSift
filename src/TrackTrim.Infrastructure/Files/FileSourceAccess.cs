using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TrackTrim.Application;
using TrackTrim.Application.Ports;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Media;

namespace TrackTrim.Infrastructure.Files;

/// <summary>Reads file identities without requesting write access to source media.</summary>
public sealed class FileSourceAccess : ISourceAccess
{
    private const uint FileTypeDisk = 1;
    private const uint UnixFileTypeMask = 0xF000;
    private const uint UnixRegularFile = 0x8000;
    private const long NanosecondsPerTick = 100;
    private const long TicksPerSecond = TimeSpan.TicksPerSecond;
    private const int LinuxNonBlocking = 0x800;
    private const int LinuxNoFollow = 0x20000;
    private const int LinuxCloseOnExec = 0x80000;

    /// <summary>Captures an existing local regular file's identity, size and modification time.</summary>
    public Task<SourceRevision> ReadRevisionAsync(SourceRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();

        try
        {
            var path = Path.GetFullPath(request.Path);
            var isInvalidPath = !Path.IsPathFullyQualified(request.Path) || path.StartsWith(@"\\", StringComparison.Ordinal);

            if (isInvalidPath)
            {
                throw new OperationFailureException(new(ErrorCode.InvalidSource, "Choose a local regular file.", ErrorReason.RegularFileRequired));
            }

            var drive = GetContainingDrive(path);

            if (drive.DriveType == DriveType.Network)
            {
                throw new OperationFailureException(new(ErrorCode.InvalidSource, "Network sources are not supported.", ErrorReason.NetworkPath));
            }

            PathSafety.RejectReparsePoints(path);

            if (OperatingSystem.IsLinux())
            {
                var statResult = NativeMethods.LStat(path, out var information);
                var isRegularFile = statResult == 0 && (information.Mode & UnixFileTypeMask) == UnixRegularFile;

                if (!isRegularFile)
                {
                    throw new OperationFailureException(new(ErrorCode.InvalidSource, "Choose a local regular file.", ErrorReason.RegularFileRequired));
                }
            }

            using var handle = OpenReadHandle(path);
            var revision = OperatingSystem.IsWindows() ? ReadWindowsRevision(new(path, handle)) : ReadLinuxRevision(new(path, handle));
            return Task.FromResult(revision);
        }
        catch (OperationFailureException exception) when (exception.Failure.Code == ErrorCode.InvalidDestination)
        {
            throw new OperationFailureException(new(ErrorCode.InvalidSource, exception.Failure.Detail, exception.Failure.Reason));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new OperationFailureException(new(ErrorCode.InvalidSource, exception.Message));
        }
    }

    /// <summary>Opens Linux files without following a final symlink or blocking on a substituted FIFO.</summary>
    private static SafeFileHandle OpenReadHandle(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }

        var descriptor = NativeMethods.Open(path, LinuxNonBlocking | LinuxNoFollow | LinuxCloseOnExec);

        if (descriptor < 0)
        {
            throw new OperationFailureException(new(ErrorCode.InvalidSource, $"Could not open a regular file (OS error {Marshal.GetLastPInvokeError()})."));
        }

        return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    /// <summary>Rechecks path safety and advisory capacity for only the additional allocation requested at this stage.</summary>
    public async Task ValidateDestinationAsync(ValidateDestinationRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        var path = Path.GetFullPath(request.DestinationPath);
        var parent = Path.GetDirectoryName(path);
        var invalidDestination = request.AdditionalAllocationBytes < 0 || !Path.IsPathFullyQualified(request.DestinationPath) ||
            !string.Equals(Path.GetExtension(path), ".mkv", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) || parent is null;

        if (invalidDestination)
        {
            throw new OperationFailureException(new(ErrorCode.InvalidDestination, "Choose a local .mkv destination."));
        }

        if (!Directory.Exists(parent))
        {
            throw new OperationFailureException(new(ErrorCode.InvalidDestination, "The output directory must already exist."));
        }

        PathSafety.RejectReparsePoints(parent!);

        if (File.Exists(path))
        {
            var destinationRevision = await ReadRevisionAsync(new(path, request.CancellationToken));
            var code = destinationRevision.FileIdentity == request.Source.FileIdentity
                ? ErrorCode.InvalidDestination : ErrorCode.DestinationExists;
            throw new OperationFailureException(new(code, path));
        }

        if (Directory.Exists(path))
        {
            throw new OperationFailureException(new(ErrorCode.DestinationExists, path));
        }

        var drive = GetContainingDrive(parent!);

        if (drive.DriveType == DriveType.Network)
        {
            throw new OperationFailureException(new(ErrorCode.InvalidDestination, "Network destinations are not supported.", ErrorReason.NetworkPath));
        }

        if (drive.AvailableFreeSpace < request.AdditionalAllocationBytes)
        {
            throw new OperationFailureException(new(ErrorCode.InsufficientSpace, "The additional output allocation exceeds the currently available space."));
        }
    }

    /// <summary>Uses the destination's mount rather than assuming every Linux path is on root.</summary>
    private static DriveInfo GetContainingDrive(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return new DriveInfo(Path.GetPathRoot(path)!);
        }

        return DriveInfo.GetDrives().Where(drive =>
        {
            var prefix = drive.Name.EndsWith(Path.DirectorySeparatorChar)
                ? drive.Name : drive.Name + Path.DirectorySeparatorChar;
            return path == drive.Name || path.StartsWith(prefix, StringComparison.Ordinal);
        }).MaxBy(drive => drive.Name.Length) ?? new DriveInfo(Path.GetPathRoot(path)!);
    }

    /// <summary>Uses Windows volume and file indexes to distinguish hard links from different files.</summary>
    private static SourceRevision ReadWindowsRevision(HandleRequest request)
    {
        if (NativeMethods.GetFileType(request.Handle) != FileTypeDisk)
        {
            throw new OperationFailureException(new(ErrorCode.InvalidSource, "Could not identify a disk file."));
        }

        if (!NativeMethods.GetFileInformationByHandle(request.Handle, out var information))
        {
            throw new OperationFailureException(new(ErrorCode.InvalidSource, "Could not read the file identity."));
        }
        var identifier = string.Create(CultureInfo.InvariantCulture,
            $"{information.VolumeSerialNumber:X8}:{information.FileIndexHigh:X8}{information.FileIndexLow:X8}");
        var length = ((long)information.FileSizeHigh << 32) | information.FileSizeLow;
        var modified = ((long)information.LastWriteTimeHigh << 32) | information.LastWriteTimeLow;
        return new(request.Path, identifier, length, DateTime.FromFileTimeUtc(modified).Ticks);
    }

    /// <summary>Reads Linux x64 device and inode identity from the open read-only handle.</summary>
    private static SourceRevision ReadLinuxRevision(HandleRequest request)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new OperationFailureException(new(ErrorCode.InvalidSource, "Only Windows x64 and Linux x64 are supported."));
        }

        var result = NativeMethods.FStat(request.Handle.DangerousGetHandle().ToInt32(), out var information);
        var isRegularFile = result == 0 && (information.Mode & UnixFileTypeMask) == UnixRegularFile;

        if (!isRegularFile)
        {
            throw new OperationFailureException(new(ErrorCode.InvalidSource, "Choose a regular file."));
        }

        var identifier = string.Create(CultureInfo.InvariantCulture, $"{information.Device:X}:{information.Inode:X}");
        var modifiedTicks = DateTime.UnixEpoch.Ticks + information.ModifiedSeconds * TicksPerSecond +
            information.ModifiedNanoseconds / NanosecondsPerTick;
        return new(request.Path, identifier, information.Size, modifiedTicks);
    }
}
