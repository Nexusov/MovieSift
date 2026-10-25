using System.Collections.Immutable;
using TrackTrim.Core.Media;
using TrackTrim.Core.Rules;

namespace TrackTrim.Application.Batches;

/// <summary>Binds one actual occurrence to its current inspected file, not to a group-local ordinal.</summary>
public sealed record BatchTrackMember(
    Guid ItemId,
    MediaInspection Inspection,
    MediaTrack Track,
    bool Keep,
    bool IsEditable,
    BatchItemState State = BatchItemState.NeedsReview);

/// <summary>Projects current choices; file coverage and occurrence count deliberately measure different things.</summary>
public sealed record BatchTrackGroup(
    TrackGroupingKey Key,
    ImmutableArray<BatchTrackMember> Members,
    LanguageRuleIssueReason? LanguageIssue = null)
{
    public bool? Keep
    {
        get
        {
            var applicable = Members.Any(member => member.IsEditable)
                ? Members.Where(member => member.IsEditable) : Members;
            return applicable.All(member => member.Keep) ? true :
                applicable.All(member => !member.Keep) ? false : null;
        }
    }
    public int FileCoverage => Members.Select(member => member.ItemId).Distinct().Count();
    public int OccurrenceCount => Members.Length;
}

public sealed record BatchTrackProjection(
    long Revision,
    ImmutableArray<BatchTrackGroup> Groups,
    int IncludedFileCount,
    int UnresolvedFileCount);
public sealed record BatchTrackSelectionRequest(BatchTrackProjection Projection, ImmutableArray<BatchTrackMember> Members, bool Keep);
public sealed record BatchSelectionReviewRequest(
    long ExpectedRevision,
    ImmutableArray<Guid> ZeroAudioConfirmedItemIds,
    CancellationToken CancellationToken = default);
internal sealed record BatchSelectionReviewPreparation(BatchSnapshot Snapshot, ImmutableArray<BatchItemFailure> Failures);
