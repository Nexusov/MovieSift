using System.Collections.Immutable;
using TrackTrim.Core.Media;

namespace TrackTrim.Infrastructure.MediaTools;

internal sealed record Mp4Structure(string MajorBrand, uint MovieTimescale,
    ImmutableArray<Mp4TrackHeader> Tracks, ImmutableHashSet<uint> ChapterTrackIds,
    ImmutableArray<string> ArtworkCodecs = default, ImmutableArray<Mp4NeroChapter> NeroChapters = default);
internal sealed record Mp4NeroChapter(long StartTicks, string Title);
internal sealed record Mp4TrackHeader(uint Id, long FlagsOffset, uint Flags, string SampleEntry)
{
    internal const uint SupportedFlags = 7;
}
internal sealed record Mp4ProbeRequest(string Json, SourceRevision Source, Mp4Structure Structure);
internal sealed record Mp4Chapter(string Start, string End, ImmutableDictionary<string, string> Tags);
internal sealed record Mp4TrackComparison(IReadOnlyList<MediaTrack> Expected, IReadOnlyList<MediaTrack> Actual,
    bool IgnoreHeaderFlags = false);
internal sealed record Mp4PacketFingerprint(long Count, string Hash);
internal sealed record Mp4PacketValidationRequest(Dictionary<string, string> Fields, TrackKind Kind, bool IsArtwork);
