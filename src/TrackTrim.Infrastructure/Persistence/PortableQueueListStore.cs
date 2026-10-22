using System.Collections.Immutable;
using System.Text.Json;
using TrackTrim.Application;
using TrackTrim.Application.Ports;
using TrackTrim.Application.QueueLists;
using TrackTrim.Infrastructure.Files;

namespace TrackTrim.Infrastructure.Persistence;

/// <summary>Persists only unfinished listings with bounded reads, fail-fast process exclusion and revision checks.</summary>
public sealed class PortableQueueListStore : IQueueListStore
{
    internal const string RecordFileName = "queue-list.json";
    internal const string LockFileName = "queue-list.lock";
    private readonly string dataPath;
    private readonly QueueListFileActions fileActions;

    /// <summary>Uses a sibling atomic move and deletes only this operation's still-owned temporary path.</summary>
    public PortableQueueListStore(string dataDirectory) : this(dataDirectory, new(
        (temporary, destination) => File.Move(temporary, destination, overwrite: true), File.Delete))
    {
    }

    /// <summary>Allows controlled write and cleanup failure tests without adding production switches.</summary>
    internal PortableQueueListStore(string dataDirectory, QueueListFileActions fileActions)
    {
        dataPath = Path.GetFullPath(dataDirectory);
        this.fileActions = fileActions;
    }

    /// <summary>A missing record is distinct from malformed, incompatible or inaccessible persisted bytes.</summary>
    public async Task<QueueListResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await ReadRecordAsync(cancellationToken);
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            return Failed(exception);
        }
    }

    /// <summary>Rereads under an OS lock and refuses to overwrite another instance's newer listing.</summary>
    public async Task<QueueListResult> WriteAsync(QueueListWriteRequest request)
    {
        if (request is null)
        {
            return new(null, null, new(QueueListErrorCode.InvalidRecord, "A queue-list write is required."));
        }

        var validation = QueueListPolicy.Validate(request.List);

        if (validation is not null)
        {
            return new(null, null, validation);
        }

        var writeState = new QueueListWriteState();
        QueueListResult result;

        try
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            ValidateExistingDataAncestors();
            Directory.CreateDirectory(dataPath);
            PathSafety.RejectReparsePoints(dataPath);
            using var ownedLock = PortableRecordFileLock.Acquire(Path.Combine(dataPath, LockFileName));
            result = await WriteLockedAsync(request, writeState);
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            result = Failed(exception);
        }

        if (writeState.TemporaryPath is not { } temporaryPath)
        {
            return result;
        }

        try
        {
            if (RecordExists(temporaryPath))
            {
                var current = await new FileSourceAccess().ReadRevisionAsync(new(temporaryPath));

                if (current.FileIdentity != writeState.FileIdentity)
                {
                    throw new IOException("The temporary path no longer identifies this operation's file; it was preserved.");
                }

                fileActions.DeleteTemporary(temporaryPath);
            }
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            var issue = new QueueListFailure(QueueListErrorCode.PersistenceUnavailable,
                $"The owned queue-list temporary file could not be removed: {exception.Message}");
            var existing = result.CleanupIssues.IsDefault ? ImmutableArray<QueueListFailure>.Empty : result.CleanupIssues;
            result = result with { CleanupIssues = existing.Add(issue) };
        }

        return result;
    }

    /// <summary>Compares the last observed revision before publishing a complete newly versioned record.</summary>
    private async Task<QueueListResult> WriteLockedAsync(QueueListWriteRequest request, QueueListWriteState writeState)
    {
        var current = await ReadRecordAsync(request.CancellationToken);

        if (current.Failure is not null)
        {
            return current;
        }

        if (current.Revision != request.ExpectedRevision)
        {
            return new(null, current.Revision, new(QueueListErrorCode.Conflict,
                "Another application instance changed the saved queue list; refresh it before writing."));
        }

        if (current.Revision.HasValue && QueueListPolicy.ContentEquals(current.List, request.List))
        {
            return current;
        }

        var revision = Guid.NewGuid();
        var bytes = QueueListJson.Write(request.List, revision);
        request.CancellationToken.ThrowIfCancellationRequested();
        var temporaryPath = Path.Combine(dataPath, $".queue-list.{Guid.NewGuid():N}.tmp");

        await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            writeState.TemporaryPath = temporaryPath;
            var identity = await new FileSourceAccess().ReadRevisionAsync(new(temporaryPath));
            writeState.FileIdentity = identity.FileIdentity;
            await stream.WriteAsync(bytes, request.CancellationToken);
            await stream.FlushAsync(request.CancellationToken);
            stream.Flush(flushToDisk: true);
        }

        request.CancellationToken.ThrowIfCancellationRequested();
        ValidateExistingDataAncestors();
        ValidateRecordIfPresent();
        fileActions.Publish(temporaryPath, Path.Combine(dataPath, RecordFileName));
        writeState.TemporaryPath = null;
        return new(request.List, revision, Committed: true);
    }

    /// <summary>Opens a verified regular file before reading its length and enforces the actual UTF-8 byte budget.</summary>
    private async Task<QueueListResult> ReadRecordAsync(CancellationToken cancellationToken)
    {
        ValidateExistingDataAncestors();
        var path = Path.Combine(dataPath, RecordFileName);

        if (!RecordExists(path))
        {
            return new(new([]), null);
        }

        await using var stream = FileSourceAccess.OpenRegularReadStream(path);

        if (stream.Length > QueueListJson.MaximumBytes)
        {
            throw RecordTooLarge();
        }

        var bytes = new byte[QueueListJson.MaximumBytes + 1];
        var count = 0;

        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken);

            if (read == 0)
            {
                break;
            }

            count += read;
        }

        if (count > QueueListJson.MaximumBytes)
        {
            throw RecordTooLarge();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return QueueListJson.Read(bytes.AsMemory(0, count));
    }

    /// <summary>Checks existing local directory ancestors before reads, creation and replacement.</summary>
    private void ValidateExistingDataAncestors()
    {
        if (dataPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new IOException("The portable queue data directory must be local.");
        }

        string? current = dataPath;

        while (current is not null)
        {
            try
            {
                var attributes = File.GetAttributes(current);
                PathSafety.RejectReparsePoints(current);
                var invalidDirectory = !attributes.HasFlag(FileAttributes.Directory) ||
                    FileSourceAccess.GetContainingDrive(current).DriveType == DriveType.Network;

                if (invalidDirectory)
                {
                    throw new IOException("The portable queue data path must be a local directory.");
                }

                return;
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                current = Path.GetDirectoryName(current);
            }
        }

        throw new IOException("No accessible portable data ancestor was found.");
    }

    /// <summary>Distinguishes absence from access or file-type failures without following redirected content.</summary>
    private static bool RecordExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Rejects a substituted destination link or special file immediately before replacement.</summary>
    private void ValidateRecordIfPresent()
    {
        var path = Path.Combine(dataPath, RecordFileName);

        if (!RecordExists(path))
        {
            return;
        }

        using var stream = FileSourceAccess.OpenRegularReadStream(path);
    }

    /// <summary>Preserves distinct corruption, contention, conflict, cancellation and filesystem outcomes.</summary>
    private static QueueListResult Failed(Exception exception) => exception switch
    {
        QueueListStorageException storage => new(null, null, storage.Failure),
        PortableRecordLockException { IsBusy: true } => new(null, null, new(QueueListErrorCode.Busy,
            "Another application instance is writing the queue list; retry the operation.")),
        OperationCanceledException => new(null, null, new(QueueListErrorCode.Cancelled, "The queue-list operation was cancelled.")),
        JsonException => new(null, null, new(QueueListErrorCode.InvalidRecord, "The saved queue list is malformed or incomplete.")),
        _ => new(null, null, new(QueueListErrorCode.PersistenceUnavailable, exception.Message)),
    };

    /// <summary>Translates expected boundary failures while leaving programming errors visible.</summary>
    private static bool IsExpectedFailure(Exception exception) => exception is QueueListStorageException or
        PortableRecordLockException or OperationCanceledException or JsonException or OperationFailureException or
        IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    /// <summary>Uses the same explicit limit for initially large records and files enlarged during a read.</summary>
    private static QueueListStorageException RecordTooLarge() => new(new(QueueListErrorCode.LimitExceeded,
        "The queue list exceeds 65536 UTF-8 bytes."));
}
