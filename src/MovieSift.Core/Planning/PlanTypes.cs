using System.Collections.Immutable;
using MovieSift.Core.Failures;
using MovieSift.Core.Media;

namespace MovieSift.Core.Planning;

public sealed record PlanRequest(
    MediaInspection Inspection,
    IReadOnlyCollection<int> RetainedTrackIds,
    string DestinationPath,
    bool ZeroAudioConfirmed,
    string? OutputDirectoryRoot = null,
    OutputMode OutputMode = OutputMode.Copy);

public sealed record RemuxPlan(
    Guid Id,
    MediaInspection Inspection,
    ImmutableArray<MediaTrack> RetainedTracks,
    string DestinationPath,
    bool ZeroAudioConfirmed,
    string? OutputDirectoryRoot = null,
    OutputMode OutputMode = OutputMode.Copy)
{
    public bool IsNoChange => RetainedTracks.Length == Inspection.Tracks.Length;
    public bool RemovesDefaultAudio => Inspection.Tracks.Any(track =>
        track.Kind == TrackKind.Audio && track.IsDefault && !RetainedTracks.Contains(track));
    public bool HasDefaultAudio => RetainedTracks.Any(track => track.Kind == TrackKind.Audio && track.IsDefault);
}

public sealed record PlanResult(RemuxPlan? Plan, Failure? Failure)
{
    public bool IsSuccess => Plan is not null;
}
