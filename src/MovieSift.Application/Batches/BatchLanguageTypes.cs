using System.Collections.Immutable;
using MovieSift.Core.Failures;
using MovieSift.Core.Media;
using MovieSift.Core.Rules;

namespace MovieSift.Application.Batches;

public enum BatchLanguagePreviewState
{
    Changed, Unchanged, ManualReview, Excluded, Completed, NoChange, PendingInspection, Failed, Cancelled
}

public sealed record BatchLanguagePreviewItem(
    Guid ItemId,
    string SourcePath,
    MediaInspection? Inspection,
    BatchLanguagePreviewState State,
    ImmutableArray<int> BeforeRetainedTrackIds,
    ImmutableArray<int> ProposedRetainedTrackIds,
    ImmutableArray<LanguageRuleIssue> Issues,
    bool NoSubtitleMatches = false);

public sealed record BatchLanguagePreview(
    Guid Id,
    long Revision,
    LanguageRules Rules,
    ImmutableArray<BatchLanguagePreviewItem> Items);

public sealed record BatchLanguagePreviewResult(BatchLanguagePreview? Preview, Failure? Failure = null);
public sealed record BatchLanguageApplyResult(bool Applied, ImmutableArray<Guid> ChangedItemIds, Failure? Failure = null);

internal sealed record BatchLanguagePreviewRequest(BatchSnapshot Snapshot, LanguageRules Rules);
internal sealed record BatchLanguageApplyRequest(BatchLanguagePreview Preview, bool IsIssued);
