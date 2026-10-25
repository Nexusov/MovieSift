using System.Collections.Immutable;
using MovieSift.Application.Ports;
using MovieSift.Core.Failures;
using MovieSift.Core.Planning;

namespace MovieSift.Application.Jobs;

public enum JobPhase
{
    Validating, Remuxing, Verifying, AwaitingWarningReview, Publishing,
    Completed, NoChange, Failed, Cancelled
}

public sealed record JobRequest(RemuxPlan Plan, IProgress<JobProgress>? Progress = null, CancellationToken CancellationToken = default);
public sealed record WarningRequest(Guid JobId, CancellationToken CancellationToken = default);
public sealed record JobProgress(JobPhase Phase, double? Percent = null);

public sealed record JobResult(
    Guid JobId,
    JobPhase Phase,
    ToolOutcome? Tool = null,
    VerificationOutcome? Verification = null,
    string? PublishedPath = null,
    Failure? Failure = null,
    bool CancellationRequested = false,
    ImmutableArray<CleanupIssue> CleanupIssues = default,
    long? OutputLength = null);

internal sealed record PendingWarning(RemuxPlan Plan, OwnedOutput Output, JobResult Result);
