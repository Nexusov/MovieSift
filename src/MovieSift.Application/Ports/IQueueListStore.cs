using MovieSift.Application.QueueLists;

namespace MovieSift.Application.Ports;

/// <summary>Stores bounded unfinished listings without inspection, approval or media-ownership authority.</summary>
public interface IQueueListStore
{
    /// <summary>Returns an empty list with no revision only when no queue record exists.</summary>
    Task<QueueListResult> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Replaces the complete list only if its last observed revision remains current.</summary>
    Task<QueueListResult> WriteAsync(QueueListWriteRequest request);
}
