using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using TrackTrim.Application;
using TrackTrim.Application.Ports;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Media;
using TrackTrim.Infrastructure.Persistence;

namespace TrackTrim.Infrastructure.Files;

/// <summary>Publishes only uniquely owned candidates and never overwrites a destination.</summary>
public sealed class OwnedOutputStore(string dataDirectory) : IOutputStore
{
    private const int LegacyMatroskaRecordVersion = 1;
    private const int RecordVersion = 2;
    private const int OwnershipTokenBytes = 32;
    private const int OwnershipTokenCharacters = OwnershipTokenBytes * 2;
    private const string CandidateStem = "candidate";
    private const string MarkerName = "owner.json";
    private const string ReservedStage = "Reserved";
    private const string PublishingStage = "Publishing";
    private readonly string recordDirectory = Path.Combine(Path.GetFullPath(dataDirectory), "ownership");

    /// <summary>Records ownership before any media tool can write the candidate.</summary>
    public async Task<OwnedOutput> ReserveAsync(ReserveOutputRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        var destination = Path.GetFullPath(request.Plan.DestinationPath);
        var parent = Path.GetDirectoryName(destination)!;
        var directory = Path.Combine(parent, $".tracktrim-{request.Plan.Id:N}");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(OwnershipTokenBytes));
        var container = request.Plan.Inspection.Container;

        if (!Enum.IsDefined(container))
        {
            throw InvalidOwnership("The approved plan has an unsupported container.");
        }

        var candidateName = CandidateStem + MediaContainerPolicy.Extension(container);
        var output = ValidateOutput(new(request.Plan.Id, directory, Path.Combine(directory, candidateName), destination, token, container));

        try
        {
            Directory.CreateDirectory(recordDirectory);
            PathSafety.RejectReparsePoints(recordDirectory);
            PathSafety.RejectReparsePoints(parent);

            if (Directory.Exists(directory) || File.Exists(directory))
            {
                throw new OperationFailureException(new(ErrorCode.OwnershipInvalid, directory));
            }

            Directory.CreateDirectory(directory);
            PathSafety.RejectReparsePoints(directory);
            await using (var markerStream = new FileStream(Path.Combine(directory, MarkerName), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(markerStream, new OwnerMarker(output.JobId, token), cancellationToken: request.CancellationToken);
                await markerStream.FlushAsync(request.CancellationToken);
                markerStream.Flush(flushToDisk: true);
            }

            await WriteRecordAsync(new(output, ReservedStage));
            return output;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new OperationFailureException(new(ErrorCode.PersistenceUnavailable, exception.Message));
        }
    }

    /// <summary>Checks the fixed layout, matching marker and persisted ownership record.</summary>
    public async Task ValidateOwnershipAsync(OutputRequest request)
    {
        await ReadOwnedRecordAsync(request);
    }

    /// <summary>Flushes an interruption record before entering the publication operation.</summary>
    public async Task MarkPublishingAsync(OutputRequest request)
    {
        await ValidateOwnershipAsync(request);
        await WriteRecordAsync(new(request.Output, PublishingStage));
    }

    /// <summary>Uses the platform move's no-overwrite operation on the destination filesystem.</summary>
    public async Task<PublishedOutput> PublishAsync(OutputRequest request)
    {
        await ValidateOwnershipAsync(request);
        var length = new FileInfo(request.Output.CandidatePath).Length;

        try
        {
            File.Move(request.Output.CandidatePath, request.Output.DestinationPath, overwrite: false);
            return new(request.Output.DestinationPath, length);
        }
        catch (IOException exception) when (File.Exists(request.Output.CandidatePath))
        {
            var code = File.Exists(request.Output.DestinationPath) || Directory.Exists(request.Output.DestinationPath)
                ? ErrorCode.DestinationExists : ErrorCode.PublicationFailed;
            throw new OperationFailureException(new(code, exception.Message));
        }
        catch (IOException exception)
        {
            throw new OperationFailureException(new(ErrorCode.PublicationUncertain, exception.Message));
        }
    }

    /// <summary>Deletes only the validated marker and candidate, leaving unexpected contents untouched.</summary>
    public async Task<ImmutableArray<CleanupIssue>> CleanupAsync(OutputRequest request)
    {
        try
        {
            var record = await ReadOwnedRecordAsync(request);
            using var currentProcess = Process.GetCurrentProcess();
            var ownsCurrentProcess = record.OwnerProcessId == currentProcess.Id &&
                record.OwnerStartedUtcTicks == currentProcess.StartTime.ToUniversalTime().Ticks;
            var unsafeInterruptedCleanup = !ownsCurrentProcess &&
                (IsOriginalProcessAlive(record) || File.Exists(request.Output.CandidatePath));

            if (unsafeInterruptedCleanup)
            {
                return [new(request.Output.DirectoryPath, "A previous worker may still own this candidate. Start a fresh operation; no media was deleted.")];
            }

            var permittedPaths = new HashSet<string>(StringComparer.Ordinal)
            {
                request.Output.CandidatePath,
                Path.Combine(request.Output.DirectoryPath, MarkerName)
            };
            var hasUnexpectedEntries = Directory.EnumerateFileSystemEntries(request.Output.DirectoryPath)
                .Any(path => !permittedPaths.Contains(path));

            if (hasUnexpectedEntries)
            {
                return [new(request.Output.DirectoryPath, "Unexpected contents prevented automatic cleanup.")];
            }

            if (ownsCurrentProcess)
            {
                File.Delete(request.Output.CandidatePath);
            }

            File.Delete(Path.Combine(request.Output.DirectoryPath, MarkerName));
            Directory.Delete(request.Output.DirectoryPath, recursive: false);
            File.Delete(RecordPath(request.Output.JobId));
            return [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationFailureException or JsonException)
        {
            return [new(request.Output?.DirectoryPath ?? recordDirectory, exception.Message)];
        }
    }

    /// <summary>Lists bounded valid records without inferring publication or terminating old PIDs.</summary>
    public async Task<ImmutableArray<InterruptedOutput>> FindInterruptedAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(recordDirectory))
        {
            return [];
        }

        PathSafety.RejectReparsePoints(recordDirectory);
        var results = ImmutableArray.CreateBuilder<InterruptedOutput>();

        foreach (var path in Directory.EnumerateFiles(recordDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var record = await ReadRecordAsync(path);
                await ValidateEvidenceAsync(record);
                var canClean = !IsOriginalProcessAlive(record) && !File.Exists(record.Output.CandidatePath);
                results.Add(new(record.Output, record.Stage, canClean));
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or OperationFailureException)
            {
                // A corrupt record never authorizes deletion or process termination.
                results.Add(new(null, "InvalidRecord", false, path));
            }
        }

        return results.ToImmutable();
    }

    /// <summary>Requires both process identity and start time before treating an old owner as active.</summary>
    private static bool IsOriginalProcessAlive(ValidatedOwnershipRecord record)
    {
        try
        {
            using var process = Process.GetProcessById(record.OwnerProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == record.OwnerStartedUtcTicks;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>Converts a caller's output and matching journal into fully checked ownership evidence.</summary>
    private async Task<ValidatedOwnershipRecord> ReadOwnedRecordAsync(OutputRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        var output = ValidateOutput(request.Output);
        var record = await ReadRecordAsync(RecordPath(output.JobId));

        if (record.Output != output)
        {
            throw InvalidOwnership("The output does not match its persisted ownership record.");
        }

        await ValidateEvidenceAsync(record);
        return record;
    }

    /// <summary>Rejects missing fields and unsupported versions before using persisted paths or owner IDs.</summary>
    private async Task<ValidatedOwnershipRecord> ReadRecordAsync(string path)
    {
        var record = await ReadOwnershipJsonAsync<OwnershipRecord>(path);

        if (record is null)
        {
            throw InvalidOwnership("The ownership record is empty.");
        }

        var validOwnerIdentity = record.OwnerProcessId > 0 && record.OwnerStartedUtcTicks > 0 &&
            record.OwnerStartedUtcTicks <= DateTime.MaxValue.Ticks;
        var validVersion = record.Version is LegacyMatroskaRecordVersion or RecordVersion;
        var validHeader = validVersion && validOwnerIdentity;

        if (!validHeader)
        {
            throw InvalidOwnership("The ownership record has an unsupported version or an invalid process identity.");
        }

        var stage = record.Stage switch
        {
            ReservedStage => ReservedStage,
            PublishingStage => PublishingStage,
            _ => throw InvalidOwnership("The ownership record has an invalid stage.")
        };
        var output = ValidateOutput(record.Output);
        var validContainerHeader = record.Version == LegacyMatroskaRecordVersion
            ? record.Container is null && output.Container == MediaContainer.Matroska
            : record.Container is not null && Enum.IsDefined(record.Container.Value) && record.Container == output.Container;

        if (!validContainerHeader)
        {
            throw InvalidOwnership("The record container is missing, unsupported or inconsistent with its output.");
        }

        if (path != RecordPath(output.JobId))
        {
            throw InvalidOwnership("The ownership record filename does not match its operation identifier.");
        }

        return new(output, stage, record.OwnerProcessId, record.OwnerStartedUtcTicks);
    }

    /// <summary>Requires a complete operation identity and an exact canonical adjacent candidate layout.</summary>
    private static OwnedOutput ValidateOutput(OwnedOutput? output)
    {
        if (output is null)
        {
            throw InvalidOwnership("The ownership record has no output description.");
        }

        var validIdentity = output.JobId != Guid.Empty && IsOwnershipToken(output.OwnershipToken) && Enum.IsDefined(output.Container);
        var validPaths = IsCanonicalAbsolutePath(output.DirectoryPath) && IsCanonicalAbsolutePath(output.CandidatePath) &&
            IsCanonicalAbsolutePath(output.DestinationPath);

        if (!validIdentity || !validPaths)
        {
            throw InvalidOwnership("The ownership record has an invalid operation identifier, token or path.");
        }

        var parent = Path.GetDirectoryName(output.DestinationPath);

        if (parent is null)
        {
            throw InvalidOwnership("The output destination has no parent directory.");
        }

        var expectedDirectory = Path.Combine(parent, $".tracktrim-{output.JobId:N}");
        var expectedCandidate = Path.Combine(expectedDirectory, CandidateStem + MediaContainerPolicy.Extension(output.Container));
        var validLayout = output.DirectoryPath == expectedDirectory && output.CandidatePath == expectedCandidate &&
            MediaContainerPolicy.FromExtension(output.DestinationPath) == output.Container;

        if (!validLayout)
        {
            throw InvalidOwnership("The temporary layout does not match its record.");
        }

        return output;
    }

    /// <summary>Checks canonical local path syntax without probing an untrusted filesystem location.</summary>
    private static bool IsCanonicalAbsolutePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var validSyntax = !path.StartsWith(@"\\", StringComparison.Ordinal) &&
                path.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathFullyQualified(path);
            return validSyntax && Path.GetFullPath(path) == path;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Requires the complete random token rather than accepting any string with the right length.</summary>
    private static bool IsOwnershipToken(string? token) =>
        token is { Length: OwnershipTokenCharacters } && token.All(Uri.IsHexDigit);

    /// <summary>Checks the exact marker and path redirection only after the journal structure is valid.</summary>
    private static async Task ValidateEvidenceAsync(ValidatedOwnershipRecord record)
    {
        var output = record.Output;
        PathSafety.RejectReparsePoints(output.DirectoryPath);
        var marker = await ReadOwnershipJsonAsync<OwnerMarker>(Path.Combine(output.DirectoryPath, MarkerName));
        var validMarker = marker is not null && marker.JobId == output.JobId &&
            IsOwnershipToken(marker.OwnershipToken) && marker.OwnershipToken == output.OwnershipToken;

        if (!validMarker)
        {
            throw InvalidOwnership("The ownership marker does not match the complete operation identity and token.");
        }

        if (File.Exists(output.CandidatePath))
        {
            PathSafety.RejectReparsePoints(output.CandidatePath);
        }
    }

    /// <summary>Turns malformed ownership JSON into a typed failure without deleting the original record.</summary>
    private static async Task<T?> ReadOwnershipJsonAsync<T>(string path)
    {
        try
        {
            return await BoundedJson.ReadAsync<T>(path);
        }
        catch (JsonException exception)
        {
            throw InvalidOwnership(exception.Message);
        }
    }

    /// <summary>Creates the stable failure used whenever ownership evidence is insufficient.</summary>
    private static OperationFailureException InvalidOwnership(string detail) => new(new(ErrorCode.OwnershipInvalid, detail));

    /// <summary>Writes the current owner identity and stage to a bounded local record.</summary>
    private Task WriteRecordAsync(RecordWriteRequest request)
    {
        using var process = Process.GetCurrentProcess();
        var record = new OwnershipRecord(RecordVersion, request.Output, request.Stage, process.Id, process.StartTime.ToUniversalTime().Ticks,
            request.Output.Container);
        return BoundedJson.WriteAsync(new JsonWriteRequest<OwnershipRecord>(RecordPath(request.Output.JobId), record));
    }

    /// <summary>Maps an exact job identifier to its one journal file.</summary>
    private string RecordPath(Guid jobId) => Path.Combine(recordDirectory, $"{jobId:N}.json");
}
