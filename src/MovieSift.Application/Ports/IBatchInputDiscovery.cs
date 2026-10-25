using MovieSift.Application.Batches;

namespace MovieSift.Application.Ports;

public interface IBatchInputDiscovery
{
    Task<BatchInputDiscoveryResult> DiscoverAsync(BatchInputDiscoveryRequest request);
}
