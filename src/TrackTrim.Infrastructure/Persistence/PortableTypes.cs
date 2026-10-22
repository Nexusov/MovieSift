using TrackTrim.Application.Ports;
using TrackTrim.Core.Media;

namespace TrackTrim.Infrastructure.Persistence;

internal sealed record LanguageSettings(int Version, string Language);
internal sealed record OwnershipRecord(int Version, OwnedOutput? Output, string? Stage, int OwnerProcessId, long OwnerStartedUtcTicks,
    MediaContainer? Container = null);
internal sealed record ValidatedOwnershipRecord(OwnedOutput Output, string Stage, int OwnerProcessId, long OwnerStartedUtcTicks);
internal sealed record OwnerMarker(Guid JobId, string? OwnershipToken);
internal sealed record JsonWriteRequest<T>(string Path, T Value);
