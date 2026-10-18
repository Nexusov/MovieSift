using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using TrackTrim.Application;
using TrackTrim.Application.Ports;
using TrackTrim.Core.Failures;
using TrackTrim.Infrastructure.Persistence;

namespace TrackTrim.Infrastructure.Files;

/// <summary>Publishes only uniquely owned candidates and never overwrites a destination.</summary>
public sealed class OwnedOutputStore(string dataDirectory) : IOutputStore
{
    private const int RecordVersion = 1;
    private const int OwnershipTokenBytes = 32;
    private const string CandidateName = "candidate.mkv";
    private const string MarkerName = "owner.json";
    private readonly string recordDirectory = Path.Combine(Path.GetFullPath(dataDirectory), "ownership");

    /// <summary>Records ownership before any media tool can write the candidate.</summary>
    public async Task<OwnedOutput> ReserveAsync(ReserveOutputRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        var destination = Path.GetFullPath(request.Plan.DestinationPath);
        var parent = Path.GetDirectoryName(destination)!;
        var directory = Path.Combine(parent, $".tracktrim-{request.Plan.Id:N}");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(OwnershipTokenBytes));
        var output = new OwnedOutput(request.Plan.Id, directory, Path.Combine(directory, CandidateName), destination, token);

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

            await WriteRecordAsync(new(output, "Reserved"));
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
        request.CancellationToken.ThrowIfCancellationRequested();
        var output = request.Output;
        var parent = Path.GetDirectoryName(output.DestinationPath);
        var expectedDirectory = parent is null ? "" : Path.Combine(parent, $".tracktrim-{output.JobId:N}");
        var expectedCandidate = Path.Combine(output.DirectoryPath, CandidateName);
        var invalidLayout = output.DirectoryPath != expectedDirectory || output.CandidatePath != expectedCandidate ||
            output.OwnershipToken.Length != OwnershipTokenBytes * 2;

        if (invalidLayout)
        {
            throw new OperationFailureException(new(ErrorCode.OwnershipInvalid, "The temporary layout does not match its record."));
        }

        PathSafety.RejectReparsePoints(output.DirectoryPath);
        var marker = await BoundedJson.ReadAsync<OwnerMarker>(Path.Combine(output.DirectoryPath, MarkerName));
        var record = await BoundedJson.ReadAsync<OwnershipRecord>(RecordPath(output.JobId));
        var invalidOwnership = marker != new OwnerMarker(output.JobId, output.OwnershipToken) ||
            record?.Version != RecordVersion || record.Output != output;

        if (invalidOwnership)
        {
            throw new OperationFailureException(new(ErrorCode.OwnershipInvalid, "The ownership token does not match."));
        }

        if (File.Exists(output.CandidatePath))
        {
            PathSafety.RejectReparsePoints(output.CandidatePath);
        }
    }

    /// <summary>Flushes an interruption record before entering the publication operation.</summary>
    public async Task MarkPublishingAsync(OutputRequest request)
    {
        await ValidateOwnershipAsync(request);
        await WriteRecordAsync(new(request.Output, "Publishing"));
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
            await ValidateOwnershipAsync(request);
            var record = await BoundedJson.ReadAsync<OwnershipRecord>(RecordPath(request.Output.JobId));
            using var currentProcess = Process.GetCurrentProcess();
            var ownsCurrentProcess = record!.OwnerProcessId == currentProcess.Id &&
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
            return [new(request.Output.DirectoryPath, exception.Message)];
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
                var record = await BoundedJson.ReadAsync<OwnershipRecord>(path);

                if (record is null || record.Version != RecordVersion || path != RecordPath(record.Output.JobId))
                {
                    results.Add(new(null, "InvalidRecord", false, path));
                    continue;
                }

                await ValidateOwnershipAsync(new(record.Output, cancellationToken));
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
    private static bool IsOriginalProcessAlive(OwnershipRecord record)
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

    /// <summary>Writes the current owner identity and stage to a bounded local record.</summary>
    private Task WriteRecordAsync(RecordWriteRequest request)
    {
        using var process = Process.GetCurrentProcess();
        var record = new OwnershipRecord(RecordVersion, request.Output, request.Stage, process.Id, process.StartTime.ToUniversalTime().Ticks);
        return BoundedJson.WriteAsync(new JsonWriteRequest<OwnershipRecord>(RecordPath(request.Output.JobId), record));
    }

    /// <summary>Maps an exact job identifier to its one journal file.</summary>
    private string RecordPath(Guid jobId) => Path.Combine(recordDirectory, $"{jobId:N}.json");
}
