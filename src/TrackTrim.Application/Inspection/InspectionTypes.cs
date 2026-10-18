using TrackTrim.Core.Failures;
using TrackTrim.Core.Media;

namespace TrackTrim.Application.Inspection;

public sealed record InspectionResult(MediaInspection? Inspection, Failure? Failure, bool IsStale = false);
