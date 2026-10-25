using System.Collections.Immutable;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Media;

namespace TrackTrim.Application.Batches;

/// <summary>Applies aggregate edits at the same revision-bound boundary as individual selections.</summary>
public sealed partial class BatchSession
{
    /// <summary>Returns every actual occurrence in the included inspection union without mutating the session.</summary>
    public BatchTrackProjection CreateTrackProjection()
    {
        lock (synchronization)
        {
            return BatchTrackProjectionBuilder.Build(snapshot);
        }
    }

    /// <summary>Atomically edits explicitly represented members after validating all captured identities.</summary>
    public BatchEditResult SetTrackSelection(BatchTrackSelectionRequest request)
    {
        BatchSnapshot updated;

        lock (synchronization)
        {
            var failure = EditFailure();

            if (failure is not null)
            {
                return new(false, DescribeResetFailure(failure));
            }

            var invalidRequest = request.Projection.Revision != snapshot.Revision || request.Members.IsDefaultOrEmpty ||
                request.Members.Select(member => (member.ItemId, member.Track.Id)).Distinct().Count() != request.Members.Length;

            if (invalidRequest)
            {
                return new(false, new(ErrorCode.StalePlan));
            }

            foreach (var member in request.Members)
            {
                var item = snapshot.Items.FirstOrDefault(item => item.Id == member.ItemId);
                var represented = request.Projection.Groups.SelectMany(group => group.Members)
                    .Any(projected => ReferenceEquals(projected, member));
                var staleMember = item is null || !item.Included || !ReferenceEquals(item.Inspection, member.Inspection) ||
                    !member.Inspection.Tracks.Any(track => ReferenceEquals(track, member.Track)) ||
                    item.CurrentRetainedTrackIds.Contains(member.Track.Id) != member.Keep;

                if (!represented || staleMember)
                {
                    return new(false, new(ErrorCode.StalePlan));
                }

                var lockedMember = !member.IsEditable || member.Track.Kind == TrackKind.Video ||
                    item!.State is BatchItemState.Completed or BatchItemState.NoChange;

                if (lockedMember)
                {
                    return new(false, new(ErrorCode.InvalidSelection));
                }
            }

            var edits = request.Members.GroupBy(member => member.ItemId).ToDictionary(group => group.Key,
                group => group.Select(member => member.Track.Id).ToHashSet());
            var changed = false;
            var items = snapshot.Items.Select(item =>
            {
                if (!edits.TryGetValue(item.Id, out var identifiers))
                {
                    return item;
                }

                var retained = item.CurrentRetainedTrackIds.ToHashSet();

                if (request.Keep)
                {
                    retained.UnionWith(identifiers);
                }

                if (!request.Keep)
                {
                    retained.ExceptWith(identifiers);
                }

                var normalized = item.Inspection!.Tracks.Where(track => retained.Contains(track.Id))
                    .Select(track => track.Id).ToImmutableArray();

                if (normalized.SequenceEqual(item.CurrentRetainedTrackIds))
                {
                    return item;
                }

                changed = true;
                return item with
                {
                    RetainedTrackIds = normalized,
                    ApprovedPlan = null,
                    State = BatchItemState.NeedsReview,
                    Failure = null
                };
            }).ToImmutableArray();

            if (!changed)
            {
                return new(true);
            }

            updated = Edited(items);
        }

        Changed?.Invoke(updated);
        return new(true);
    }
}
