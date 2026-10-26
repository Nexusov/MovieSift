using System.Collections.Immutable;
using MovieSift.Application.Batches;
using MovieSift.Core.Planning;

namespace MovieSift.Application.QueueLists;

public enum QueueListErrorCode
{
    Busy, Conflict, InvalidRecord, UnsupportedVersion, LimitExceeded, PersistenceUnavailable, Cancelled
}

public sealed record QueueListEntry(
    string SourcePath,
    string? DestinationPath,
    bool Included = true,
    FolderSourceOrigin? FolderOrigin = null,
    string? OutputDirectoryRoot = null,
    OutputMode OutputMode = OutputMode.Copy);

public sealed record QueueList(ImmutableArray<QueueListEntry> Entries);
public sealed record QueueListFailure(QueueListErrorCode Code, string Detail = "");
public sealed record QueueListResult(
    QueueList? List,
    Guid? Revision,
    QueueListFailure? Failure = null,
    bool Committed = false,
    ImmutableArray<QueueListFailure> CleanupIssues = default);
public sealed record QueueListWriteRequest(
    QueueList List,
    Guid? ExpectedRevision,
    CancellationToken CancellationToken = default);
