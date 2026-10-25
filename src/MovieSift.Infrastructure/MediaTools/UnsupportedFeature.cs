using MovieSift.Core.Failures;

namespace MovieSift.Infrastructure.MediaTools;

internal sealed record UnsupportedFeature(ErrorReason Reason, string Detail);
