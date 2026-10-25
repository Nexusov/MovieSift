using MovieSift.Application.Ports;
using MovieSift.Application.QueueLists;

namespace MovieSift.Desktop.Tests;

/// <summary>Models controlled metadata-only revision conflicts without claiming filesystem persistence.</summary>
internal sealed class BatchUiQueueListStore : IQueueListStore
{
    public QueueList List { get; set; } = new([]);
    public Guid? Revision { get; set; }
    public QueueListFailure? ReadFailure { get; set; }
    public QueueListFailure? WriteFailure { get; set; }
    public Func<Task>? OnRead { get; set; }
    public Func<Task>? OnWrite { get; set; }
    public int Writes { get; private set; }

    /// <summary>Returns a complete immutable listing after an optional deterministic read gate.</summary>
    public async Task<QueueListResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (OnRead is not null)
        {
            await OnRead();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ReadFailure is { } failure ? new(null, null, failure) : new(List, Revision);
    }

    /// <summary>Applies a controlled compare-and-swap without touching sources, candidates or outputs.</summary>
    public async Task<QueueListResult> WriteAsync(QueueListWriteRequest request)
    {
        if (OnWrite is not null)
        {
            await OnWrite();
        }

        request.CancellationToken.ThrowIfCancellationRequested();
        Writes++;
        var failure = WriteFailure ?? QueueListPolicy.Validate(request.List);

        if (failure is not null)
        {
            return new(null, Revision, failure);
        }

        if (Revision != request.ExpectedRevision)
        {
            return new(null, Revision, new(QueueListErrorCode.Conflict));
        }

        if (QueueListPolicy.ContentEquals(List, request.List))
        {
            return new(List, Revision, Committed: true);
        }

        List = request.List;
        Revision = Guid.NewGuid();
        return new(List, Revision, Committed: true);
    }
}
