using MovieSift.Core.Failures;
using MovieSift.Core.Media;

namespace MovieSift.Application.Inspection;

public sealed record InspectionResult(MediaInspection? Inspection, Failure? Failure, bool IsStale = false);
