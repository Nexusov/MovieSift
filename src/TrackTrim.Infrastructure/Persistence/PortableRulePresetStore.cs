using System.Collections.Immutable;
using System.Text.Json;
using TrackTrim.Application;
using TrackTrim.Application.Ports;
using TrackTrim.Application.Presets;
using TrackTrim.Infrastructure.Files;

namespace TrackTrim.Infrastructure.Persistence;

/// <summary>Stores an explicitly loaded rule catalog beside the portable application without persisting workflow state.</summary>
public sealed class PortableRulePresetStore : IRulePresetStore
{
    internal const string CatalogFileName = "rule-presets.json";
    internal const string LockFileName = "rule-presets.lock";
    private readonly string dataPath;
    private readonly RulePresetFileActions fileActions;

    /// <summary>Uses the portable data directory and fixed atomic filesystem operations.</summary>
    public PortableRulePresetStore(string dataDirectory) : this(dataDirectory, new(
        (temporary, destination) => File.Move(temporary, destination, overwrite: true), File.Delete))
    {
    }

    /// <summary>Allows focused tests of publication and secondary cleanup failures without production switches.</summary>
    internal PortableRulePresetStore(string dataDirectory, RulePresetFileActions fileActions)
    {
        dataPath = Path.GetFullPath(dataDirectory);
        this.fileActions = fileActions;
    }

    /// <summary>Returns a complete validated catalog or an explicit failure, preserving every invalid source byte.</summary>
    public async Task<RulePresetCatalogResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await ReadCatalogAsync(cancellationToken);
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            return Failed(exception);
        }
    }

    /// <summary>Rereads and applies one intent under an exclusive process lock before atomic publication.</summary>
    public async Task<RulePresetCatalogResult> ChangeAsync(RulePresetMutation mutation)
    {
        if (mutation is null)
        {
            return new(null, new(RulePresetErrorCode.InvalidConfiguration, "A preset change is required."));
        }

        RulePresetFileLock? ownedLock = null;
        var writeState = new RulePresetWriteState();
        RulePresetCatalogResult result;

        try
        {
            mutation.CancellationToken.ThrowIfCancellationRequested();
            ValidateExistingDataAncestors();
            Directory.CreateDirectory(dataPath);
            PathSafety.RejectReparsePoints(dataPath);
            ownedLock = RulePresetFileLock.Acquire(Path.Combine(dataPath, LockFileName));
            result = await ChangeLockedAsync(mutation, writeState);
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            result = Failed(exception);
        }
        finally
        {
            ownedLock?.Dispose();
        }

        // Cleanup has its own outcome and never replaces a publication fact or primary failure.
        if (writeState.TemporaryPath is { } temporaryPath)
        {
            try
            {
                fileActions.DeleteTemporary(temporaryPath);
            }
            catch (Exception exception) when (IsExpectedFailure(exception))
            {
                var issue = new RulePresetFailure(RulePresetErrorCode.PersistenceUnavailable,
                    $"The owned preset temporary file could not be removed: {exception.Message}");
                var existing = result.CleanupIssues.IsDefault ? ImmutableArray<RulePresetFailure>.Empty : result.CleanupIssues;
                result = result with { CleanupIssues = existing.Add(issue) };
            }
        }

        return result;
    }

    /// <summary>Uses the latest complete catalog rather than a caller's stale copy as the mutation base.</summary>
    private async Task<RulePresetCatalogResult> ChangeLockedAsync(RulePresetMutation mutation, RulePresetWriteState writeState)
    {
        var current = await ReadCatalogAsync(mutation.CancellationToken);

        if (current.Failure is not null)
        {
            return current;
        }

        var proposed = RulePresetCatalogPolicy.Apply(current.Catalog, mutation);

        if (proposed.Failure is not null || proposed.Catalog is null)
        {
            return proposed;
        }

        if (ReferenceEquals(proposed.Catalog, current.Catalog))
        {
            return proposed;
        }

        var bytes = RulePresetJson.Write(proposed.Catalog);
        mutation.CancellationToken.ThrowIfCancellationRequested();
        var temporaryPath = Path.Combine(dataPath, $".rule-presets.{Guid.NewGuid():N}.tmp");

        await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            writeState.TemporaryPath = temporaryPath;
            await stream.WriteAsync(bytes, mutation.CancellationToken);
            await stream.FlushAsync(mutation.CancellationToken);
            stream.Flush(flushToDisk: true);
        }

        mutation.CancellationToken.ThrowIfCancellationRequested();
        PathSafety.RejectReparsePoints(dataPath);
        ValidateCatalogFileIfPresent();
        fileActions.Publish(temporaryPath, Path.Combine(dataPath, CatalogFileName));
        writeState.TemporaryPath = null;
        return proposed with { Committed = true };
    }

    /// <summary>Bounds bytes before parsing and refuses redirected or nonregular catalog handles.</summary>
    private async Task<RulePresetCatalogResult> ReadCatalogAsync(CancellationToken cancellationToken)
    {
        ValidateExistingDataAncestors();
        var path = Path.Combine(dataPath, CatalogFileName);

        if (!CatalogExists(path))
        {
            return new(new([]));
        }

        await using var stream = FileSourceAccess.OpenRegularReadStream(path);

        if (stream.Length > RulePresetJson.MaximumBytes)
        {
            throw CatalogTooLarge();
        }

        var bytes = new byte[RulePresetJson.MaximumBytes + 1];
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

        if (count > RulePresetJson.MaximumBytes)
        {
            throw CatalogTooLarge();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return RulePresetJson.Read(bytes.AsMemory(0, count));
    }

    /// <summary>Validates existing ancestors before creating portable directories or treating a catalog as missing.</summary>
    private void ValidateExistingDataAncestors()
    {
        string? current = dataPath;

        while (current is not null)
        {
            try
            {
                var attributes = File.GetAttributes(current);
                PathSafety.RejectReparsePoints(current);

                if (!attributes.HasFlag(FileAttributes.Directory))
                {
                    throw new IOException("The portable data path must be a directory.");
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

    /// <summary>Distinguishes a missing catalog from access, directory and unsafe-file failures.</summary>
    private static bool CatalogExists(string path)
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
    private void ValidateCatalogFileIfPresent()
    {
        var path = Path.Combine(dataPath, CatalogFileName);

        if (!CatalogExists(path))
        {
            return;
        }

        using var stream = FileSourceAccess.OpenRegularReadStream(path);
    }

    /// <summary>Retains distinct corruption, cancellation, lock and persistence outcomes.</summary>
    private static RulePresetCatalogResult Failed(Exception exception) => exception switch
    {
        RulePresetStorageException storage => new(null, storage.Failure),
        OperationCanceledException => new(null, new(RulePresetErrorCode.Cancelled, "The preset operation was cancelled.")),
        JsonException => new(null, new(RulePresetErrorCode.InvalidCatalog, "The preset catalog is malformed or incomplete.")),
        _ => new(null, new(RulePresetErrorCode.PersistenceUnavailable, exception.Message)),
    };

    /// <summary>Limits exception translation to expected file, schema and cancellation failures.</summary>
    private static bool IsExpectedFailure(Exception exception) => exception is RulePresetStorageException or
        OperationCanceledException or JsonException or OperationFailureException or IOException or
        UnauthorizedAccessException or ArgumentException or NotSupportedException;

    /// <summary>Reports the same actual byte bound for an oversized initial file or a file enlarged during reading.</summary>
    private static RulePresetStorageException CatalogTooLarge() => new(new(RulePresetErrorCode.LimitExceeded,
        "The preset catalog exceeds 65536 UTF-8 bytes."));
}
