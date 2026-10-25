using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using MovieSift.Application;
using MovieSift.Application.Ports;
using MovieSift.Core.Failures;
using MovieSift.Core.Media;
using MovieSift.Core.Planning;

namespace MovieSift.Infrastructure.Files;

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
    private const int ReadBufferBytes = 4096;

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

    /// <summary>Provides a read-only regular-file stream without blocking on a substituted Linux FIFO.</summary>
    internal static FileStream OpenRegularReadStream(string path)
    {
        PathSafety.RejectReparsePoints(path);
        var handle = OpenReadHandle(path);

        try
        {
            _ = OperatingSystem.IsWindows()
                ? ReadWindowsRevision(new(path, handle)) : ReadLinuxRevision(new(path, handle));
            return new FileStream(handle, FileAccess.Read, ReadBufferBytes, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Captures a regular file's identity directly from this operation's already opened handle.</summary>
    internal static SourceRevision ReadRegularRevision(HandleRequest request) => OperatingSystem.IsWindows()
        ? ReadWindowsRevision(request) : ReadLinuxRevision(request);

    /// <summary>Rechecks path safety and advisory capacity for only the additional allocation requested at this stage.</summary>
    public async Task ValidateDestinationAsync(ValidateDestinationRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        var path = Path.GetFullPath(request.DestinationPath);
        var parent = Path.GetDirectoryName(path);
        var invalidDestination = request.AdditionalAllocationBytes < 0 || !Path.IsPathFullyQualified(request.DestinationPath) ||
            MediaContainerPolicy.FromExtension(path) != request.Container || !Enum.IsDefined(request.Container) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) || parent is null;

        if (invalidDestination)
        {
            throw new OperationFailureException(new(ErrorCode.InvalidDestination, "Choose a local destination matching the inspected container."));
        }

        var existingParent = ValidateOutputParent(new(request, parent!));

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

        var drive = GetContainingDrive(existingParent);

        if (drive.DriveType == DriveType.Network)
        {
            throw new OperationFailureException(new(ErrorCode.InvalidDestination, "Network destinations are not supported.", ErrorReason.NetworkPath));
        }

        if (drive.AvailableFreeSpace < request.AdditionalAllocationBytes)
        {
            throw new OperationFailureException(new(ErrorCode.InsufficientSpace, "The additional output allocation exceeds the currently available space."));
        }
    }

    /// <summary>Creates only validated missing output parents, immediately before ownership reservation.</summary>
    public async Task PrepareDestinationAsync(ValidateDestinationRequest request)
    {
        await ValidateDestinationAsync(request);

        if (request.OutputDirectoryRoot is null)
        {
            return;
        }

        var root = Path.GetFullPath(request.OutputDirectoryRoot);
        var parent = Path.GetDirectoryName(Path.GetFullPath(request.DestinationPath))!;
        var current = root;
        var relative = Path.GetRelativePath(root, parent);

        if (relative == ".")
        {
            return;
        }

        foreach (var component in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            PathSafety.RejectReparsePoints(current);
            current = Path.Combine(current, component);
            Directory.CreateDirectory(current);
            PathSafety.RejectReparsePoints(current);
        }

        await ValidateDestinationAsync(request);
    }

    /// <summary>Allows absent parents only within an explicit existing safe output root, without creating them.</summary>
    private static string ValidateOutputParent(OutputParentRequest request)
    {
        var root = request.Destination.OutputDirectoryRoot;

        if (root is null)
        {
            if (!Directory.Exists(request.Parent))
            {
                throw new OperationFailureException(new(ErrorCode.InvalidDestination, "The output directory must already exist."));
            }

            PathSafety.RejectReparsePoints(request.Parent);
            return request.Parent;
        }

        var invalidRoot = !Path.IsPathFullyQualified(root) || root.StartsWith(@"\\", StringComparison.Ordinal) ||
            !OutputPathPolicy.IsWithinDirectory(root, request.Parent) || !Directory.Exists(root);

        if (invalidRoot)
        {
            throw new OperationFailureException(new(ErrorCode.InvalidDestination, "Choose an existing output root containing the destination."));
        }

        root = Path.GetFullPath(root);
        PathSafety.RejectReparsePoints(root);
        var relative = Path.GetRelativePath(root, request.Parent);

        if (relative == ".")
        {
            return root;
        }

        var current = root;

        foreach (var component in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            var child = Path.Combine(current, component);
            FileAttributes attributes;

            try
            {
                attributes = File.GetAttributes(child);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return current;
            }

            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new OperationFailureException(new(ErrorCode.InvalidDestination, "Symlink and junction paths are not supported.", ErrorReason.RedirectedPath));
            }

            if (!attributes.HasFlag(FileAttributes.Directory))
            {
                throw new OperationFailureException(new(ErrorCode.InvalidDestination, "Every existing output parent must be a directory."));
            }

            current = child;
        }

        return current;
    }

    /// <summary>Uses the destination's mount rather than assuming every Linux path is on root.</summary>
    internal static DriveInfo GetContainingDrive(string path)
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
