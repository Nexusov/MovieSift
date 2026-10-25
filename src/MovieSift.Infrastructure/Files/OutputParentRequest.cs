using MovieSift.Application.Ports;

namespace MovieSift.Infrastructure.Files;

internal sealed record OutputParentRequest(ValidateDestinationRequest Destination, string Parent);
