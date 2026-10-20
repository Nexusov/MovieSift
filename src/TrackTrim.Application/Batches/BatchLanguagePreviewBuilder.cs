using System.Collections.Immutable;
using TrackTrim.Core.Rules;

namespace TrackTrim.Application.Batches;

/// <summary>Evaluates file-local rule proposals and retains every ineligible row visibly unchanged.</summary>
internal static class BatchLanguagePreviewBuilder
{
    /// <summary>Creates an immutable before-and-after view without approving or editing any batch item.</summary>
    internal static BatchLanguagePreview Build(BatchLanguagePreviewRequest request)
    {
        var items = request.Snapshot.Items.Select(item => PreviewItem(item, request.Rules)).ToImmutableArray();
        return new(Guid.NewGuid(), request.Snapshot.Revision, request.Rules, items);
    }

    /// <summary>Runs the pure matcher only for included inspected items that can still be planned.</summary>
    private static BatchLanguagePreviewItem PreviewItem(BatchItem item, LanguageRules rules)
    {
        var before = item.CurrentRetainedTrackIds;
        var skipped = SkippedState(item);

        if (skipped is { } state)
        {
            return new(item.Id, item.SourcePath, item.Inspection, state, before, before, []);
        }

        var proposal = LanguageRuleMatcher.Preview(new(item.Inspection!, before, rules));
        var resultState = !proposal.Issues.IsEmpty ? BatchLanguagePreviewState.ManualReview :
            proposal.HasChanges ? BatchLanguagePreviewState.Changed : BatchLanguagePreviewState.Unchanged;
        return new(item.Id, item.SourcePath, item.Inspection, resultState, before, proposal.RetainedTrackIds,
            proposal.Issues, proposal.NoSubtitleMatches);
    }

    /// <summary>Provides explicit unchanged outcomes for excluded, finished, failed and uninspected rows.</summary>
    private static BatchLanguagePreviewState? SkippedState(BatchItem item)
    {
        if (!item.Included)
        {
            return BatchLanguagePreviewState.Excluded;
        }

        return item.State switch
        {
            BatchItemState.Completed => BatchLanguagePreviewState.Completed,
            BatchItemState.NoChange => BatchLanguagePreviewState.NoChange,
            BatchItemState.Failed => BatchLanguagePreviewState.Failed,
            BatchItemState.Cancelled => BatchLanguagePreviewState.Cancelled,
            BatchItemState.NeedsReview or BatchItemState.Ready when item.Inspection is not null => null,
            _ => BatchLanguagePreviewState.PendingInspection
        };
    }
}
