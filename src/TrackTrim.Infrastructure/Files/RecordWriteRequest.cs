using TrackTrim.Application.Ports;

namespace TrackTrim.Infrastructure.Files;

internal sealed record RecordWriteRequest(OwnedOutput Output, string Stage);
