using System.Collections.Immutable;
using TrackTrim.Application.Batches;

namespace TrackTrim.Application.QueueLists;

public enum QueueListErrorCode
{
    Busy, Conflict, InvalidRecord, UnsupportedVersion, LimitExceeded, PersistenceUnavailable, Cancelled
}

public sealed record QueueListEntry(
    string SourcePath,
    string? DestinationPath,
    bool Included = true,
    FolderSourceOrigin? FolderOrigin = null,
    string? OutputDirectoryRoot = null);

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
