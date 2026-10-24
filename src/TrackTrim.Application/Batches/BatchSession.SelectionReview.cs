using System.Collections.Immutable;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Planning;

namespace TrackTrim.Application.Batches;

/// <summary>Freezes one explicit workspace review without requiring individual editor approval clicks.</summary>
public sealed partial class BatchSession
{
    /// <summary>Builds independent plans under the captured revision and locks out conflicting edits.</summary>
    internal BatchSelectionReviewPreparation? BeginSelectionReview(BatchSelectionReviewRequest request, out Failure? failure)
    {
        BatchSnapshot updated;
        BatchSelectionReviewPreparation preparation;

        lock (synchronization)
        {
            failure = EditFailure();

            if (failure is not null)
            {
                return null;
            }

            if (request.ExpectedRevision != snapshot.Revision)
            {
                failure = new(ErrorCode.StalePlan);
                return null;
            }

            var confirmations = request.ZeroAudioConfirmedItemIds.IsDefault
                ? new HashSet<Guid>() : request.ZeroAudioConfirmedItemIds.ToHashSet();
            var failures = ImmutableArray.CreateBuilder<BatchItemFailure>();
            var items = snapshot.Items.Select(item => PrepareReviewedItem(item, confirmations, failures)).ToImmutableArray();
            updated = snapshot = snapshot with { Phase = BatchPhase.Reviewing, Review = null, Failure = null };
            preparation = new(snapshot with { Items = items }, failures.ToImmutable());
        }

        Changed?.Invoke(updated);
        return preparation;
    }

    /// <summary>Commits only current validated inputs; a delayed result cannot install authority in another session.</summary>
    internal BatchReviewResult CompleteSelectionReview(BatchSelectionReviewPreparation preparation, BatchReviewResult validation)
    {
        BatchSnapshot updated;
        BatchReviewResult result;

        lock (synchronization)
        {
            var stale = snapshot.Phase != BatchPhase.Reviewing || snapshot.Revision != preparation.Snapshot.Revision ||
                snapshot.Items.Length != preparation.Snapshot.Items.Length;

            if (stale)
            {
                return new(null, [], new(ErrorCode.StalePlan));
            }

            var changed = snapshot.Items.Zip(preparation.Snapshot.Items)
                .Any(pair => !ReferenceEquals(pair.First, pair.Second));
            var revision = changed ? snapshot.Revision + 1 : snapshot.Revision;
            var review = validation.Review is null ? null : validation.Review with { Revision = revision };
            updated = snapshot = snapshot with
            {
                Revision = revision,
                Items = preparation.Snapshot.Items,
                Review = review,
                Phase = review is null ? BatchPhase.Preparing : BatchPhase.Ready,
                Failure = validation.Failure,
                PauseReason = BatchPauseReason.None,
                PauseRequested = false
            };
            result = validation with { Review = review };
        }

        Changed?.Invoke(updated);
        return result;
    }

    /// <summary>Preserves equivalent authority and binds new zero-audio consent to this file's exact current selection.</summary>
    private static BatchItem PrepareReviewedItem(BatchItem item, HashSet<Guid> confirmations,
        ImmutableArray<BatchItemFailure>.Builder failures)
    {
        var skipped = !item.Included || item.State is BatchItemState.Completed or BatchItemState.NoChange;

        if (skipped)
        {
            return item;
        }

        if (item.Inspection is null)
        {
            failures.Add(new(item.Id, item.Failure ?? new(ErrorCode.StalePlan)));
            return item;
        }

        var destination = item.IntendedDestinationPath;
        var existing = item.ApprovedPlan;
        var equivalent = item.State == BatchItemState.Ready && item.Result is null &&
            existing is not null && ReferenceEquals(existing.Inspection, item.Inspection) &&
            existing.RetainedTracks.Select(track => track.Id).SequenceEqual(item.CurrentRetainedTrackIds) &&
            existing.DestinationPath == destination && existing.OutputDirectoryRoot == item.IntendedOutputDirectoryRoot;

        if (equivalent)
        {
            return item;
        }

        var built = PlanBuilder.Build(new(item.Inspection, item.CurrentRetainedTrackIds,
            destination ?? string.Empty, confirmations.Contains(item.Id), item.IntendedOutputDirectoryRoot));

        if (built.Plan is null)
        {
            failures.Add(new(item.Id, built.Failure!));
            return item;
        }

        return item with { ApprovedPlan = built.Plan, State = BatchItemState.Ready, Failure = null, Result = null };
    }
}
