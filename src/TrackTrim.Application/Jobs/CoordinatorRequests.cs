using TrackTrim.Application.Ports;
using TrackTrim.Core.Media;
using TrackTrim.Core.Planning;

namespace TrackTrim.Application.Jobs;

internal sealed record PlanValidationRequest(RemuxPlan Plan, CancellationToken CancellationToken);
internal sealed record PublicationRequest(RemuxPlan Plan, OwnedOutput Output, JobResult Result, SourceRevision CandidateRevision, CancellationToken CancellationToken);
internal sealed record CandidateValidationRequest(string Path, SourceRevision ExpectedRevision, CancellationToken CancellationToken);
