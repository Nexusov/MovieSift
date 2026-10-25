using MovieSift.Application.Ports;

namespace MovieSift.Infrastructure.Files;

internal sealed record RecordWriteRequest(OwnedOutput Output, string Stage);
