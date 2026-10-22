using TrackTrim.Core.Failures;

namespace TrackTrim.Infrastructure.MediaTools;

internal sealed record UnsupportedFeature(ErrorReason Reason, string Detail);
