using System.Collections.Immutable;
using System.Text.Json.Serialization;
using TrackTrim.Application.Batches;
using TrackTrim.Application.QueueLists;

namespace TrackTrim.Infrastructure.Persistence;

internal sealed record QueueListDocument(
    [property: JsonRequired] int Version,
    [property: JsonRequired] Guid Revision,
    [property: JsonRequired] ImmutableArray<QueueListRecord> Entries);

internal sealed record QueueListRecord(
    [property: JsonRequired] string SourcePath,
    [property: JsonRequired] string? DestinationPath,
    [property: JsonRequired] bool Included,
    [property: JsonRequired] FolderSourceOrigin? FolderOrigin,
    [property: JsonRequired] string? OutputDirectoryRoot);

internal sealed record QueueListFileActions(Action<string, string> Publish, Action<string> DeleteTemporary);

internal sealed class QueueListWriteState
{
    internal string? TemporaryPath { get; set; }
    internal string? FileIdentity { get; set; }
}

internal sealed class QueueListStorageException(QueueListFailure failure) : Exception(failure.Detail)
{
    internal QueueListFailure Failure { get; } = failure;
}
