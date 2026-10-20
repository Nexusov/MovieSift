using System.Collections.Immutable;
using TrackTrim.Application.Jobs;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Media;
using TrackTrim.Core.Planning;

namespace TrackTrim.Application.Batches;

public enum BatchPhase { Preparing, Inspecting, Reviewing, Ready, Running, Paused, Completed }
public enum BatchItemState { PendingInspection, Inspecting, NeedsReview, Ready, Running, AwaitingWarningReview, Completed, NoChange, Failed, Cancelled }
public enum BatchPauseReason { None, Requested, WarningReview, ItemFailed, Cancelled, CleanupRequired, UnsafeOwnership }

public sealed record BatchItem(
    Guid Id,
    string SourcePath,
    SourceRevision? DiscoveredSource,
    bool Included = true,
    MediaInspection? Inspection = null,
    RemuxPlan? ApprovedPlan = null,
    BatchItemState State = BatchItemState.PendingInspection,
    JobResult? Result = null,
    Failure? Failure = null,
    ImmutableArray<int> RetainedTrackIds = default)
{
    public ImmutableArray<int> CurrentRetainedTrackIds => !RetainedTrackIds.IsDefault
        ? RetainedTrackIds
        : ApprovedPlan?.RetainedTracks.Select(track => track.Id).ToImmutableArray()
            ?? Inspection?.Tracks.Select(track => track.Id).ToImmutableArray() ?? [];
}

public sealed record BatchReviewItem(Guid ItemId, RemuxPlan Plan);
public sealed record BatchReview(Guid Id, long Revision, ImmutableArray<BatchReviewItem> Items);
public sealed record BatchItemFailure(Guid ItemId, Failure Failure);
public sealed record BatchReviewResult(BatchReview? Review, ImmutableArray<BatchItemFailure> ItemFailures, Failure? Failure = null);
public sealed record BatchEditResult(bool Applied, Failure? Failure = null);
public sealed record BatchPlanApprovalRequest(Guid ItemId, RemuxPlan Plan);
public sealed record BatchSelectionRequest(Guid ItemId, ImmutableArray<int> RetainedTrackIds);
public sealed record BatchItemRequest(Guid ItemId, CancellationToken CancellationToken = default);
public sealed record BatchRunRequest(Guid ReviewId, IProgress<BatchProgress>? Progress = null, CancellationToken CancellationToken = default);
public sealed record BatchWarningRequest(Guid ItemId, CancellationToken CancellationToken = default);
public sealed record BatchProgress(Guid ItemId, Guid PlanId, JobProgress Job);

public sealed record BatchSnapshot(
    long Revision,
    BatchPhase Phase,
    ImmutableArray<BatchItem> Items,
    BatchReview? Review = null,
    BatchPauseReason PauseReason = BatchPauseReason.None,
    bool PauseRequested = false,
    Failure? Failure = null)
{
    public bool HasSafetyBlock => PauseReason == BatchPauseReason.UnsafeOwnership || Items.Any(item =>
        item.Failure?.Code is ErrorCode.ProcessTerminationFailed or ErrorCode.PublicationUncertain ||
        item.Result?.Failure?.Code is ErrorCode.ProcessTerminationFailed or ErrorCode.PublicationUncertain ||
        item.Result is { CleanupIssues.IsDefaultOrEmpty: false });
}

internal sealed record UnexpectedBatchFailureRequest(Guid? ItemId, Exception Exception);
