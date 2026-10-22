using TrackTrim.Application.Ports;

namespace TrackTrim.Infrastructure.Files;

internal sealed record OutputParentRequest(ValidateDestinationRequest Destination, string Parent);
