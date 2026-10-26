using MovieSift.Application.Ports;
using MovieSift.Core.Media;
using MovieSift.Infrastructure.Files;

namespace MovieSift.Infrastructure.Persistence;

internal sealed record LanguageSettings(int Version, string Language);
internal sealed record OwnershipRecord(int Version, OwnedOutput? Output, string? Stage, int OwnerProcessId, long OwnerStartedUtcTicks,
    MediaContainer? Container = null, SourceRevision? VerifiedCandidate = null);
internal sealed record ValidatedOwnershipRecord(OwnedOutput Output, string Stage, int OwnerProcessId, long OwnerStartedUtcTicks,
    SourceRevision? VerifiedCandidate = null);
internal sealed record OwnerMarker(Guid JobId, string? OwnershipToken);
internal sealed record JsonWriteRequest<T>(string Path, T Value, CancellationToken CancellationToken = default,
    bool Overwrite = true, JsonFileActions? FileActions = null);
internal sealed record JsonFileActions(Action<string, string, bool> MoveTemporary, Action<string> DeleteTemporary,
    Func<HandleRequest, SourceRevision>? CaptureTemporaryRevision = null);
