using System.Collections.Immutable;

namespace TrackTrim.Application.Ports;

public interface IOutputStore
{
    Task<OwnedOutput> ReserveAsync(ReserveOutputRequest request);
    Task ValidateOwnershipAsync(OutputRequest request);
    Task MarkPublishingAsync(OutputRequest request);
    Task<PublishedOutput> PublishAsync(OutputRequest request);
    Task<ImmutableArray<CleanupIssue>> CleanupAsync(OutputRequest request);
    Task<ImmutableArray<InterruptedOutput>> FindInterruptedAsync(CancellationToken cancellationToken = default);
}
