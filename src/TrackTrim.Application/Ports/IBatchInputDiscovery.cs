using TrackTrim.Application.Batches;

namespace TrackTrim.Application.Ports;

public interface IBatchInputDiscovery
{
    Task<BatchInputDiscoveryResult> DiscoverAsync(BatchInputDiscoveryRequest request);
}
