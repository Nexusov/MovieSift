using System.Collections.Immutable;
using TrackTrim.Application;
using TrackTrim.Application.Batches;
using TrackTrim.Application.Ports;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Media;

namespace TrackTrim.Infrastructure.Files;

/// <summary>Discovers a bounded set of local MKV candidates without traversing child folders or redirected paths.</summary>
public sealed class FileBatchInputDiscovery(ISourceAccess sourceAccess) : IBatchInputDiscovery
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Snapshots the request and moves synchronous filesystem inspection off the UI thread.</summary>
    public Task<BatchInputDiscoveryResult> DiscoverAsync(BatchInputDiscoveryRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        var validShape = request.Paths is not null && request.Paths.Count != 0
            && Enum.IsDefined(request.Kind)
            && (request.Kind != BatchInputKind.TopLevelFolder || request.Paths.Count == 1);

        if (!validShape)
        {
            return Task.FromResult(Reject(new Failure(ErrorCode.InvalidSource,
                "Choose explicit MKV files or exactly one top-level folder.", ErrorReason.InvalidBatchInput)));
        }

        if (request.Paths!.Count > BatchDiscoveryLimits.MaximumExaminedEntries)
        {
            return Task.FromResult(Reject(EntryLimitFailure()));
        }

        var snapshot = request with { Paths = request.Paths.ToArray() };
        return Task.Run(() => DiscoverCoreAsync(snapshot), snapshot.CancellationToken);
    }

    /// <summary>Separates folder enumeration failures from individual candidate failures and rejects every overflow.</summary>
    private async Task<BatchInputDiscoveryResult> DiscoverCoreAsync(BatchInputDiscoveryRequest request)
    {
        IReadOnlyList<string> paths;

        try
        {
            paths = request.Kind == BatchInputKind.TopLevelFolder
                ? EnumerateFolder(request) : request.Paths;
        }
        catch (OperationFailureException exception)
        {
            return Reject(AsSourceFailure(exception.Failure));
        }
        catch (Exception exception) when (IsExpectedFileFailure(exception))
        {
            return Reject(new Failure(ErrorCode.InvalidSource, exception.Message));
        }

        var sources = new List<SourceRevision>();
        var issues = new List<BatchDiscoveryIssue>();
        var requestedPaths = new HashSet<string>(PathComparer);
        var canonicalPaths = new HashSet<string>(PathComparer);
        var fileIdentities = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in paths.Order(StringComparer.Ordinal))
        {
            request.CancellationToken.ThrowIfCancellationRequested();

            if (!requestedPaths.Add(path))
            {
                continue;
            }

            try
            {
                var canonicalPath = CanonicalCandidatePath(path);

                if (!canonicalPaths.Add(canonicalPath))
                {
                    continue;
                }

                if (!string.Equals(Path.GetExtension(canonicalPath), ".mkv", StringComparison.OrdinalIgnoreCase))
                {
                    throw new OperationFailureException(new Failure(ErrorCode.UnsupportedContainer,
                        "Only MKV file candidates are included in this phase."));
                }

                var source = await sourceAccess.ReadRevisionAsync(new SourceRequest(
                    canonicalPath, request.CancellationToken)).ConfigureAwait(false);
                var knownDuplicate = !string.IsNullOrEmpty(source.FileIdentity) && !fileIdentities.Add(source.FileIdentity);

                if (knownDuplicate)
                {
                    continue;
                }

                sources.Add(source);
            }
            catch (OperationFailureException exception)
            {
                issues.Add(new BatchDiscoveryIssue(path, AsSourceFailure(exception.Failure)));
            }
            catch (Exception exception) when (IsExpectedFileFailure(exception))
            {
                issues.Add(new BatchDiscoveryIssue(path, new Failure(ErrorCode.InvalidSource, exception.Message)));
            }

            if (sources.Count + issues.Count > BatchDiscoveryLimits.MaximumFiles)
            {
                return Reject(new Failure(ErrorCode.InvalidSource,
                    $"A batch addition may contain at most {BatchDiscoveryLimits.MaximumFiles} entries, including invalid entries.",
                    ErrorReason.BatchFileLimit));
            }
        }

        request.CancellationToken.ThrowIfCancellationRequested();
        return new BatchInputDiscoveryResult(sources.OrderBy(source => source.FullPath, StringComparer.Ordinal).ToImmutableArray(),
            issues.OrderBy(issue => issue.Path, StringComparer.Ordinal).ToImmutableArray());
    }

    /// <summary>Enumerates only immediate entries after applying the existing local and reparse-point policy.</summary>
    private static IReadOnlyList<string> EnumerateFolder(BatchInputDiscoveryRequest request)
    {
        var requestedPath = request.Paths[0];

        if (!Path.IsPathFullyQualified(requestedPath))
        {
            throw new OperationFailureException(new Failure(ErrorCode.InvalidSource,
                "Choose an absolute local folder path.", ErrorReason.InvalidBatchInput));
        }

        var folder = Path.GetFullPath(requestedPath);

        if (folder.StartsWith(@"\\", StringComparison.Ordinal)
            || FileSourceAccess.GetContainingDrive(folder).DriveType == DriveType.Network)
        {
            throw new OperationFailureException(new Failure(ErrorCode.InvalidSource,
                "Network folders are not supported.", ErrorReason.NetworkPath));
        }

        PathSafety.RejectReparsePoints(folder);

        if (!Directory.Exists(folder))
        {
            throw new OperationFailureException(new Failure(ErrorCode.InvalidSource,
                "Choose an existing local folder.", ErrorReason.InvalidBatchInput));
        }

        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
        };
        var candidates = new List<string>();
        var examined = 0;

        foreach (var entry in Directory.EnumerateFileSystemEntries(folder, "*", enumeration))
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            examined++;

            if (examined > BatchDiscoveryLimits.MaximumExaminedEntries)
            {
                throw new OperationFailureException(EntryLimitFailure());
            }

            if (string.Equals(Path.GetExtension(entry), ".mkv", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(entry);
            }
        }

        return candidates;
    }

    /// <summary>Canonicalizes an explicit source path without treating a relative path as approved input.</summary>
    private static string CanonicalCandidatePath(string path)
    {

        if (!Path.IsPathFullyQualified(path))
        {
            throw new OperationFailureException(new Failure(ErrorCode.InvalidSource,
                "Choose an absolute local MKV file path.", ErrorReason.InvalidBatchInput));
        }

        return Path.GetFullPath(path);
    }

    /// <summary>Preserves existing path-policy reasons while converting write-path wording to source failures.</summary>
    private static Failure AsSourceFailure(Failure failure)
    {
        return failure.Code == ErrorCode.InvalidDestination ? failure with { Code = ErrorCode.InvalidSource } : failure;
    }

    /// <summary>Returns an explicit rejection without accepting an arbitrary truncated subset.</summary>
    private static BatchInputDiscoveryResult Reject(Failure failure)
    {
        return new BatchInputDiscoveryResult([], [], failure);
    }

    /// <summary>Reports the independent scan bound even when non-MKV entries dominate a folder.</summary>
    private static Failure EntryLimitFailure()
    {
        return new Failure(ErrorCode.InvalidSource,
            $"The input contains more than {BatchDiscoveryLimits.MaximumExaminedEntries} entries; nothing was added.",
            ErrorReason.BatchEntryLimit);
    }

    /// <summary>Classifies expected per-entry filesystem failures without swallowing cancellation or programming errors.</summary>
    private static bool IsExpectedFileFailure(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
    }
}
