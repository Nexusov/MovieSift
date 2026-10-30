using MovieSift.Application;
using MovieSift.Application.Ports;
using MovieSift.Core.Failures;
using MovieSift.Core.Media;
using MovieSift.Core.Planning;

namespace MovieSift.Infrastructure.Files;

/// <summary>Includes one identity-bound app-local input copy in the existing output reservation and journal.</summary>
public sealed partial class OwnedOutputStore
{
    private const long ProjectionGrowthBytes = 16 * 1024 * 1024;

    /// <summary>Creates an empty owned file before durable handoff; no source bytes are written during reservation.</summary>
    private async Task<OwnedInputProjection> ReserveProjectionAsync(ReserveOutputRequest request, PartialReservation partial)
    {
        if (request.Plan.Inspection.Container != MediaContainer.Matroska)
        {
            throw InvalidOwnership("Only Matroska jobs may reserve an input projection.");
        }

        PathSafety.RejectReparsePoints(recordDirectory);
        var drive = FileSourceAccess.GetContainingDrive(recordDirectory);

        if (drive.DriveType == DriveType.Network)
        {
            throw InvalidOwnership("The app-local projection directory must be on a local filesystem.");
        }

        var path = ProjectionPath(request.Plan.Id);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.Read };

        if (OperatingSystem.IsLinux())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        await using var stream = new FileStream(path, options);
        partial.ProjectionPath = path;
        partial.Projection = FileSourceAccess.ReadRegularRevision(new(path, stream.SafeFileHandle));
        var sameVolume = partial.Marker!.FileIdentity.Split(':')[0] == partial.Projection.FileIdentity.Split(':')[0];
        long allocation;

        try
        {
            allocation = checked(request.Plan.Inspection.Source.Length * (sameVolume ? 2 : 1) + ProjectionGrowthBytes);
        }
        catch (OverflowException)
        {
            throw new OperationFailureException(new(ErrorCode.InsufficientSpace, "The combined projection and output allocation is too large."));
        }

        if (drive.AvailableFreeSpace < allocation)
        {
            throw new OperationFailureException(new(ErrorCode.InsufficientSpace,
                "The app-local projection volume does not have enough space for the input copy and remaining output allocation."));
        }

        await stream.FlushAsync(request.CancellationToken);
        stream.Flush(flushToDisk: true);
        return new(partial.Projection, request.Plan.Inspection.Source);
    }

    /// <summary>Allows only the exact app-local resource and its original source identity in a Matroska record.</summary>
    private void ValidateProjectionDescription(OwnedOutput output)
    {
        if (output.InputProjection is not { } projection)
        {
            return;
        }

        var created = projection.CreatedFile;
        var original = projection.OriginalSource;
        var validResource = output.Container == MediaContainer.Matroska && created is not null && original is not null &&
            created.FullPath == ProjectionPath(output.JobId) && created.Length == 0 && IsValidRevision(created) && IsValidRevision(original) &&
            IsCanonicalAbsolutePath(original.FullPath) && MediaContainerPolicy.FromExtension(original.FullPath) == MediaContainer.Matroska;

        if (!validResource || created!.FileIdentity == original!.FileIdentity || original.FullPath == created.FullPath ||
            original.FullPath == output.CandidatePath || original.FullPath == output.DirectoryPath)
        {
            throw InvalidOwnership("The input projection does not identify a distinct owned regular-file resource.");
        }

        var consistentSource = output.OutputMode == OutputMode.ReplaceSource
            ? original == output.ExpectedSource : original.FullPath != output.DestinationPath;

        if (!consistentSource)
        {
            throw InvalidOwnership("The input projection source does not match the approved output mode.");
        }
    }

    /// <summary>Includes dangling links and directories rather than treating them as an absent projection.</summary>
    private static bool ProjectionExists(OwnedOutput output)
    {
        if (output.InputProjection is not { } projection)
        {
            return false;
        }

        try
        {
            _ = File.GetAttributes(projection.CreatedFile.FullPath);
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Deletes an identity-validated projection only after the current owner has reaped its workers.</summary>
    private static void RemoveProjection(OwnedOutput output)
    {
        if (!ProjectionExists(output))
        {
            return;
        }

        OwnedInputProjectionAccess.Validate(output.InputProjection!);
        File.Delete(output.InputProjection!.CreatedFile.FullPath);
    }

    /// <summary>Rolls back only the empty file whose identity this partial reservation actually captured.</summary>
    private static void RemovePartialProjection(PartialReservation partial)
    {
        if (partial.ProjectionPath is null)
        {
            return;
        }

        if (partial.Projection is not { } created)
        {
            throw new IOException($"The projection identity could not be captured; the path was preserved: {partial.ProjectionPath}");
        }

        OwnedInputProjectionAccess.ValidateCreatedFile(created);
        File.Delete(created.FullPath);
    }

    /// <summary>Derives the only permitted input-copy name within the existing journal directory.</summary>
    private string ProjectionPath(Guid jobId) => Path.Combine(recordDirectory, $"{jobId:N}.input.mkv");
}
