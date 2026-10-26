using System.Collections.Immutable;
using MovieSift.Application;
using MovieSift.Application.Batches;
using MovieSift.Application.Ports;
using MovieSift.Core.Failures;
using MovieSift.Core.Media;
using MovieSift.Core.Planning;

namespace MovieSift.Infrastructure.Files;

/// <summary>Discovers bounded local MKV/MP4 candidates through explicitly checked folder traversal.</summary>
public sealed class FileBatchInputDiscovery(ISourceAccess sourceAccess, IOutputStore? ownedOutputs = null,
    IReadOnlyList<string>? workingDirectories = null) : IBatchInputDiscovery
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly string[] workingDirectorySnapshot = workingDirectories?.ToArray() ?? [];
    private readonly Func<string, DriveType> readDirectoryDriveType = path => FileSourceAccess.GetContainingDrive(path).DriveType;

    /// <summary>Substitutes only mount classification for controlled network-directory policy regressions.</summary>
    internal FileBatchInputDiscovery(ISourceAccess sourceAccess, IOutputStore? ownedOutputs,
        IReadOnlyList<string>? workingDirectories, Func<string, DriveType> readDirectoryDriveType)
        : this(sourceAccess, ownedOutputs, workingDirectories)
    {
        this.readDirectoryDriveType = readDirectoryDriveType;
    }

    /// <summary>Snapshots the request and moves synchronous filesystem inspection off the UI thread.</summary>
    public Task<BatchInputDiscoveryResult> DiscoverAsync(BatchInputDiscoveryRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        var validShape = request.Paths is not null && request.Paths.Count != 0
            && Enum.IsDefined(request.Kind)
            && !(request.UseSourceDirectories && request.OutputRoot is not null)
            && (request.Kind == BatchInputKind.ExplicitFiles || request.Paths.Count == 1);

        if (!validShape)
        {
            return Task.FromResult(Reject(new Failure(ErrorCode.InvalidSource,
                "Choose explicit MKV/MP4 files or exactly one folder.", ErrorReason.InvalidBatchInput)));
        }

        if (request.Paths!.Count > BatchDiscoveryLimits.MaximumExaminedEntries)
        {
            return Task.FromResult(Reject(EntryLimitFailure()));
        }

        var snapshot = request with { Paths = request.Paths.ToArray() };
        return Task.Run(() => DiscoverCoreAsync(snapshot), snapshot.CancellationToken);
    }

    /// <summary>Rejects incomplete traversal before accepting independently validated candidate rows.</summary>
    private async Task<BatchInputDiscoveryResult> DiscoverCoreAsync(BatchInputDiscoveryRequest request)
    {
        IReadOnlyList<string> paths;
        string? folderRoot = null;

        try
        {
            paths = request.Kind == BatchInputKind.ExplicitFiles
                ? request.Paths : await EnumerateFolderAsync(request).ConfigureAwait(false);
            folderRoot = request.Kind == BatchInputKind.ExplicitFiles ? null : Path.GetFullPath(request.Paths[0]);
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
        var origins = ImmutableDictionary.CreateBuilder<string, FolderSourceOrigin>(PathComparer);
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

                if (MediaContainerPolicy.FromExtension(canonicalPath) is null)
                {
                    throw new OperationFailureException(new Failure(ErrorCode.UnsupportedContainer,
                        "Only MKV and MP4 file candidates are supported."));
                }

                var source = await sourceAccess.ReadRevisionAsync(new SourceRequest(
                    canonicalPath, request.CancellationToken)).ConfigureAwait(false);
                var knownDuplicate = !string.IsNullOrEmpty(source.FileIdentity) && !fileIdentities.Add(source.FileIdentity);

                if (knownDuplicate)
                {
                    continue;
                }

                sources.Add(source);

                if (folderRoot is not null)
                {
                    origins[source.FullPath] = new(folderRoot, Path.GetRelativePath(folderRoot, canonicalPath));
                }
            }
            catch (OperationFailureException exception)
            {
                issues.Add(new BatchDiscoveryIssue(path, AsSourceFailure(exception.Failure)));
                AddIssueOrigin(path);
            }
            catch (Exception exception) when (IsExpectedFileFailure(exception))
            {
                issues.Add(new BatchDiscoveryIssue(path, new Failure(ErrorCode.InvalidSource, exception.Message)));
                AddIssueOrigin(path);
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
            issues.OrderBy(issue => issue.Path, StringComparer.Ordinal).ToImmutableArray(),
            FolderOrigins: folderRoot is null ? null : origins.ToImmutable());

        /// <summary>Retains folder context for an explicitly reported invalid candidate.</summary>
        void AddIssueOrigin(string path)
        {
            if (folderRoot is not null)
            {
                origins[path] = new(folderRoot, Path.GetRelativePath(folderRoot, path));
            }
        }
    }

    /// <summary>Enumerates top-level or bounded recursive input without creating output paths or following redirected folders.</summary>
    private async Task<IReadOnlyList<string>> EnumerateFolderAsync(BatchInputDiscoveryRequest request)
    {
        var folder = ValidateFolder(request.Paths[0]);
        var recursive = request.Kind == BatchInputKind.RecursiveFolder;
        var exclusions = recursive ? await GetExcludedDirectoriesAsync(request, folder).ConfigureAwait(false) : [];
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((folder, 0));
        var candidates = new List<string>();
        var examined = 0;
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
        };

        while (pending.TryPop(out var current))
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            ValidateFolder(current.Path);
            var entries = new List<string>();

            foreach (var entry in Directory.EnumerateFileSystemEntries(current.Path, "*", enumeration))
            {
                request.CancellationToken.ThrowIfCancellationRequested();
                examined++;

                if (examined > BatchDiscoveryLimits.MaximumExaminedEntries)
                {
                    throw new OperationFailureException(EntryLimitFailure());
                }

                entries.Add(entry);
            }

            foreach (var entry in entries.OrderDescending(StringComparer.Ordinal))
            {
                request.CancellationToken.ThrowIfCancellationRequested();

                if (recursive && exclusions.Any(excluded => OutputPathPolicy.IsWithinDirectory(excluded, entry)))
                {
                    continue;
                }

                var isDirectory = recursive && File.GetAttributes(entry).HasFlag(FileAttributes.Directory);

                if (isDirectory)
                {
                    PathSafety.RejectReparsePoints(entry);
                    var depth = current.Depth + 1;

                    if (depth > BatchDiscoveryLimits.MaximumDirectoryDepth)
                    {
                        throw new OperationFailureException(new(ErrorCode.InvalidSource,
                            $"The folder exceeds {BatchDiscoveryLimits.MaximumDirectoryDepth} nested directories; nothing was added.",
                            ErrorReason.BatchDepthLimit));
                    }

                    pending.Push((entry, depth));
                    continue;
                }

                if (MediaContainerPolicy.FromExtension(entry) is not null)
                {
                    candidates.Add(entry);
                }
            }
        }

        return candidates;
    }

    /// <summary>Combines the chosen output subtree with actual validated ownership and application working locations.</summary>
    private async Task<IReadOnlyList<string>> GetExcludedDirectoriesAsync(BatchInputDiscoveryRequest request, string folder)
    {
        if (!request.UseSourceDirectories && string.IsNullOrWhiteSpace(request.OutputRoot))
        {
            throw new OperationFailureException(new(ErrorCode.InvalidSource,
                "Choose an existing output folder before including subfolders.", ErrorReason.RecursiveOutputRequired));
        }

        var outputRoot = request.UseSourceDirectories ? null : ValidateFolder(request.OutputRoot!);

        if (outputRoot is not null && OutputPathPolicy.IsWithinDirectory(outputRoot, folder))
        {
            throw new OperationFailureException(new(ErrorCode.InvalidSource,
                "The recursive output root cannot equal or contain the selected source root.", ErrorReason.RecursiveRootConflict));
        }

        var exclusions = new HashSet<string>(workingDirectorySnapshot.Select(Path.GetFullPath), PathComparer);

        if (outputRoot is not null)
        {
            exclusions.Add(outputRoot);
        }

        if (ownedOutputs is not null)
        {
            var interrupted = await ownedOutputs.FindInterruptedAsync(request.CancellationToken).ConfigureAwait(false);

            foreach (var entry in interrupted)
            {
                if (entry.Output is { } output)
                {
                    exclusions.Add(output.DirectoryPath);
                }
            }
        }

        if (exclusions.Any(excluded => OutputPathPolicy.IsWithinDirectory(excluded, folder)))
        {
            throw new OperationFailureException(new(ErrorCode.InvalidSource,
                "An application-owned working directory cannot be used as a source root.", ErrorReason.InvalidBatchInput));
        }

        return exclusions.ToArray();
    }

    /// <summary>Checks the established local, existing-folder and reparse-point policy before descent.</summary>
    private string ValidateFolder(string requestedPath)
    {
        if (!Path.IsPathFullyQualified(requestedPath))
        {
            throw new OperationFailureException(new Failure(ErrorCode.InvalidSource,
                "Choose an absolute local folder path.", ErrorReason.InvalidBatchInput));
        }

        var folder = Path.GetFullPath(requestedPath);

        if (folder.StartsWith(@"\\", StringComparison.Ordinal)
            || readDirectoryDriveType(folder) == DriveType.Network)
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

        return folder;
    }

    /// <summary>Canonicalizes an explicit source path without treating a relative path as approved input.</summary>
    private static string CanonicalCandidatePath(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new OperationFailureException(new Failure(ErrorCode.InvalidSource,
                "Choose an absolute local MKV or MP4 file path.", ErrorReason.InvalidBatchInput));
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

    /// <summary>Classifies expected filesystem failures without swallowing cancellation or programming errors.</summary>
    private static bool IsExpectedFileFailure(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
    }
}
