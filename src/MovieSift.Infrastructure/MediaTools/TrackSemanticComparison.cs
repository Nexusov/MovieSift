using MovieSift.Core.Media;

namespace MovieSift.Infrastructure.MediaTools;

internal sealed record TrackSemanticComparison(MediaTrack Expected, MediaTrack Actual);
