using System.Collections.Immutable;
using TrackTrim.Application.Batches;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Media;
using TrackTrim.Core.Rules;
using Xunit;

namespace TrackTrim.Application.Tests.Batches;

/// <summary>Checks pure rule review and atomic changed-only application over independent file selections.</summary>
public sealed class BatchLanguageRulesTests
{
    /// <summary>Evaluates each file's own order and IDs without issuing tools, approvals or queue execution.</summary>
    [Fact]
    public async Task PreviewIsPureAndApplicationUsesEachFilesOwnTrackMapping()
    {
        var harness = new BatchTestHarness();
        BatchLanguageTestData.ConfigureDistinctLanguages(harness);
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        var before = harness.Session.Snapshot;
        var changes = 0;
        harness.Session.Changed += _ => changes++;
        var preview = harness.Coordinator.PreviewLanguageRules(BatchLanguageTestData.KeepEnglishAudio).Preview!;

        Assert.Same(before, harness.Session.Snapshot);
        Assert.Equal(0, changes);
        Assert.Equal([0, 1, 2], preview.Items[0].ProposedRetainedTrackIds);
        Assert.Equal([12, 10, 44], preview.Items[1].ProposedRetainedTrackIds);
        Assert.Equal([0, 1, 7, 2], preview.Items[0].BeforeRetainedTrackIds);
        Assert.Equal([12, 10, 44, 11], preview.Items[1].BeforeRetainedTrackIds);
        Assert.All(preview.Items, item => Assert.Equal(BatchLanguagePreviewState.Changed, item.State));
        var applied = harness.Coordinator.ApplyLanguageRules(preview);
        var after = harness.Session.Snapshot;
        Assert.True(applied.Applied);
        Assert.Equal(before.Items.Select(item => item.Id), applied.ChangedItemIds);
        Assert.Equal(before.Revision + 1, after.Revision);
        Assert.Equal(1, changes);
        Assert.Equal(preview.Items[0].ProposedRetainedTrackIds, after.Items[0].CurrentRetainedTrackIds);
        Assert.Equal(preview.Items[1].ProposedRetainedTrackIds, after.Items[1].CurrentRetainedTrackIds);
        Assert.All(after.Items, item =>
        {
            Assert.True(item.Included);
            Assert.Null(item.ApprovedPlan);
            Assert.Null(item.Result);
            Assert.Equal(BatchItemState.NeedsReview, item.State);
        });
        Assert.Same(before.Items[0].Inspection, after.Items[0].Inspection);
        Assert.Same(before.Items[1].Inspection, after.Items[1].Inspection);
        Assert.Empty(harness.RemuxedPaths);
        Assert.Empty(harness.DestinationValidations);
        Assert.Empty(harness.Reservations);
        Assert.Equal(0, harness.VerifyCalls);
        Assert.Equal(0, harness.CleanupCalls);
    }

    /// <summary>Invalidates exactly one changed plan and the aggregate review while preserving an unchanged manual plan.</summary>
    [Fact]
    public async Task ApplyInvalidatesOnlyChangedApprovalsAndIncrementsRevisionOnce()
    {
        var harness = new BatchTestHarness();
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        harness.Approve(harness.Session.Snapshot.Items[0], noChange: true);
        harness.Approve(harness.Session.Snapshot.Items[1]);
        await harness.Coordinator.ReviewAsync(TestContext.Current.CancellationToken);
        var before = harness.Session.Snapshot;
        var destinationChecks = harness.DestinationValidations.Count;
        var preview = harness.Coordinator.PreviewLanguageRules(BatchLanguageTestData.KeepRussianSubtitles).Preview!;
        Assert.Equal(BatchLanguagePreviewState.Changed, preview.Items[0].State);
        Assert.Equal(BatchLanguagePreviewState.Unchanged, preview.Items[1].State);
        Assert.All(preview.Items, item => Assert.True(item.NoSubtitleMatches));

        var result = harness.Coordinator.ApplyLanguageRules(preview);

        Assert.True(result.Applied);
        Assert.Equal(before.Items[0].Id, Assert.Single(result.ChangedItemIds));
        Assert.Equal(before.Revision + 1, harness.Session.Snapshot.Revision);
        Assert.Null(harness.Session.Snapshot.Review);
        Assert.Null(harness.Session.Snapshot.Items[0].ApprovedPlan);
        Assert.Same(before.Items[1], harness.Session.Snapshot.Items[1]);
        Assert.Equal(destinationChecks, harness.DestinationValidations.Count);
        Assert.Empty(harness.RemuxedPaths);
        Assert.Null((await harness.Coordinator.ReviewAsync(TestContext.Current.CancellationToken)).Review);
    }

    /// <summary>Preserves all approvals and the exact aggregate review when the proposal changes no Keep IDs.</summary>
    [Fact]
    public async Task NoOpApplyPreservesExactSnapshotReviewAndApprovals()
    {
        var harness = new BatchTestHarness();
        await harness.PrepareAsync();
        var before = harness.Session.Snapshot;
        var changes = 0;
        harness.Session.Changed += _ => changes++;
        var preview = harness.Coordinator.PreviewLanguageRules(BatchLanguageTestData.Unchanged).Preview!;
        Assert.All(preview.Items, item => Assert.Equal(BatchLanguagePreviewState.Unchanged, item.State));
        var result = harness.Coordinator.ApplyLanguageRules(preview);

        Assert.True(result.Applied);
        Assert.Empty(result.ChangedItemIds);
        Assert.Same(before, harness.Session.Snapshot);
        Assert.NotNull(before.Review);
        Assert.Equal(0, changes);
        Assert.Empty(harness.RemuxedPaths);
    }

    /// <summary>Leaves an unresolved whole file and its explicit manual approval untouched while applying a reliable sibling.</summary>
    [Fact]
    public async Task UnresolvedRowKeepsItsSelectionAndManualApproval()
    {
        var harness = new BatchTestHarness();
        var path = harness.Inspections.Keys.First();
        var inspection = harness.Inspections[path];
        harness.Inspections[path] = inspection with
        {
            Tracks = inspection.Tracks.Select(track => track.Kind == TrackKind.Audio
                ? track with { Language = "und", LanguageIetf = null } : track).ToImmutableArray()
        };
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        Assert.All(harness.Session.Snapshot.Items, item => harness.Approve(item, noChange: true));
        await harness.Coordinator.ReviewAsync(TestContext.Current.CancellationToken);
        var before = harness.Session.Snapshot;
        var rules = new LanguageRules(new(LanguageRuleMode.KeepMatching, ["eng"]),
            new(LanguageRuleMode.KeepMatching, ["rus"]));
        var preview = harness.Coordinator.PreviewLanguageRules(rules).Preview!;
        var unresolved = preview.Items[0];
        Assert.Equal(BatchLanguagePreviewState.ManualReview, unresolved.State);
        Assert.Contains(unresolved.Issues, issue => issue.Reason == LanguageRuleIssueReason.UnknownLanguage);
        Assert.Equal(unresolved.BeforeRetainedTrackIds, unresolved.ProposedRetainedTrackIds);
        Assert.False(unresolved.NoSubtitleMatches);
        Assert.Equal(BatchLanguagePreviewState.Changed, preview.Items[1].State);

        var result = harness.Coordinator.ApplyLanguageRules(preview);

        Assert.True(result.Applied);
        Assert.Equal(before.Items[1].Id, Assert.Single(result.ChangedItemIds));
        Assert.Same(before.Items[0], harness.Session.Snapshot.Items[0]);
        Assert.Same(before.Items[0].ApprovedPlan, harness.Session.Snapshot.Items[0].ApprovedPlan);
        Assert.Null(harness.Session.Snapshot.Items[1].ApprovedPlan);
        Assert.Equal(unresolved.BeforeRetainedTrackIds, harness.Session.Snapshot.Items[0].CurrentRetainedTrackIds);
        Assert.NotEmpty(preview.Items[0].Issues);
        Assert.Empty(harness.RemuxedPaths);
    }

    /// <summary>Exposes every skipped or manually actionable row and never edits a completed, excluded or unresolved item.</summary>
    [Fact]
    public async Task PreviewEnumeratesCompletedFailedCancelledExcludedAndPendingRows()
    {
        var harness = new BatchTestHarness(6);
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        var originals = harness.Session.Snapshot.Items;

        foreach (var item in originals)
        {
            harness.Approve(item, noChange: item.Id == originals[0].Id || item.Id == originals[5].Id);
        }

        harness.ExitCodes[originals[2].SourcePath] = 2;
        var firstReview = (await harness.Coordinator.ReviewAsync(TestContext.Current.CancellationToken)).Review!;
        await harness.Coordinator.RunAsync(new(firstReview.Id, CancellationToken: TestContext.Current.CancellationToken));
        Assert.True(harness.Session.SetIncluded(originals[2].Id, false).Applied);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        harness.OnRemux = _ => { cancellation.Cancel(); return Task.CompletedTask; };
        var nextReview = (await harness.Coordinator.ReviewAsync(TestContext.Current.CancellationToken)).Review!;
        await harness.Coordinator.RunAsync(new(nextReview.Id, CancellationToken: cancellation.Token));
        Assert.True(harness.Session.SetIncluded(originals[2].Id, true).Applied);
        Assert.True(harness.Session.SetIncluded(originals[4].Id, false).Applied);
        Assert.True(harness.Session.AddSources([new SourceRevision(
            Path.Combine(Path.GetTempPath(), "uninspected later episode.mkv"), "pending", 100, 200)]).Applied);
        var before = harness.Session.Snapshot;
        var preview = harness.Coordinator.PreviewLanguageRules(BatchLanguageTestData.KeepRussianSubtitles).Preview!;
        Assert.Equal([BatchLanguagePreviewState.NoChange, BatchLanguagePreviewState.Completed, BatchLanguagePreviewState.Failed,
            BatchLanguagePreviewState.Cancelled, BatchLanguagePreviewState.Excluded, BatchLanguagePreviewState.Changed,
            BatchLanguagePreviewState.PendingInspection], preview.Items.Select(item => item.State));

        var result = harness.Coordinator.ApplyLanguageRules(preview);

        Assert.True(result.Applied);
        Assert.Equal(originals[5].Id, Assert.Single(result.ChangedItemIds));
        Assert.Equal(before.Items.Length, harness.Session.Snapshot.Items.Length);

        foreach (var item in before.Items.Where(item => item.Id != originals[5].Id))
        {
            Assert.Same(item, harness.Session.Snapshot.Items.Single(current => current.Id == item.Id));
        }

        Assert.NotNull(harness.Session.Snapshot.Items[1].Result!.PublishedPath);
        Assert.False(harness.Session.SetSelection(new(originals[0].Id, [0, 1])).Applied);
        Assert.False(harness.Session.SetSelection(new(originals[1].Id, [10, 11])).Applied);
    }

    /// <summary>Rejects every previously captured selection preview after a real session edit or a new batch.</summary>
    [Theory]
    [InlineData("selection")]
    [InlineData("destination")]
    [InlineData("inclusion")]
    [InlineData("approval")]
    [InlineData("remove")]
    [InlineData("addition")]
    [InlineData("inspection")]
    [InlineData("new-batch")]
    public async Task ChangedInputsRejectStalePreviewAtomically(string edit)
    {
        var harness = new BatchTestHarness();
        await harness.PrepareAsync();
        var first = harness.Session.Snapshot.Items[0];
        var preview = harness.Coordinator.PreviewLanguageRules(BatchLanguageTestData.KeepEnglishAudio).Preview!;

        switch (edit)
        {
            case "selection":
                Assert.True(harness.Session.SetSelection(new(first.Id, [0, 1, 2])).Applied);
                break;
            case "destination":
                Assert.True(harness.Session.InvalidatePlan(first.Id).Applied);
                break;
            case "inclusion":
                Assert.True(harness.Session.SetIncluded(first.Id, false).Applied);
                break;
            case "approval":
                harness.Approve(first);
                break;
            case "remove":
                Assert.True(harness.Session.Remove(first.Id).Applied);
                break;
            case "addition":
                Assert.True(harness.Session.AddSources([new SourceRevision(
                    Path.Combine(Path.GetTempPath(), "another input.mkv"), "another", 100, 200)]).Applied);
                break;
            case "inspection":
                await harness.Coordinator.InspectItemAsync(new(first.Id, TestContext.Current.CancellationToken));
                break;
            case "new-batch":
                Assert.True(harness.Coordinator.StartNewBatch().Applied);
                break;
        }

        var edited = harness.Session.Snapshot;
        var result = harness.Coordinator.ApplyLanguageRules(preview);
        Assert.False(result.Applied);
        Assert.Equal(ErrorCode.StalePlan, result.Failure!.Code);
        Assert.Empty(result.ChangedItemIds);
        Assert.Same(edited, harness.Session.Snapshot);
        Assert.Empty(harness.RemuxedPaths);
    }

    /// <summary>Rejects copied or forged preview records while retaining the actual issued preview for explicit application.</summary>
    [Theory]
    [InlineData("copy")]
    [InlineData("rules")]
    [InlineData("rows")]
    [InlineData("selection")]
    public async Task TamperedPreviewCannotChangeAnySelection(string tampering)
    {
        var harness = new BatchTestHarness();
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        var preview = harness.Coordinator.PreviewLanguageRules(BatchLanguageTestData.KeepRussianSubtitles).Preview!;
        var before = harness.Session.Snapshot;
        var tampered = tampering switch
        {
            "rules" => preview with { Rules = BatchLanguageTestData.Unchanged },
            "rows" => preview with { Items = preview.Items.RemoveAt(0) },
            "selection" => preview with { Items = preview.Items.SetItem(0, preview.Items[0] with { ProposedRetainedTrackIds = [] }) },
            _ => preview with { }
        };

        var rejected = harness.Coordinator.ApplyLanguageRules(tampered);

        Assert.False(rejected.Applied);
        Assert.Equal(ErrorCode.StalePlan, rejected.Failure!.Code);
        Assert.Same(before, harness.Session.Snapshot);
        Assert.True(harness.Coordinator.ApplyLanguageRules(preview).Applied);
    }

    /// <summary>Accepts only the latest issued preview even when two previews share the same session revision.</summary>
    [Fact]
    public async Task SupersededPreviewCannotApplyAndNoOpSelectionDoesNotStaleLatestPreview()
    {
        var harness = new BatchTestHarness();
        await harness.PrepareAsync();
        var first = harness.Coordinator.PreviewLanguageRules(BatchLanguageTestData.KeepEnglishAudio).Preview!;
        var latest = harness.Coordinator.PreviewLanguageRules(BatchLanguageTestData.Unchanged).Preview!;
        var before = harness.Session.Snapshot;
        Assert.True(harness.Session.SetSelection(new(before.Items[0].Id, [0, 1])).Applied);
        Assert.Same(before, harness.Session.Snapshot);
        Assert.False(harness.Coordinator.ApplyLanguageRules(first).Applied);
        Assert.Same(before, harness.Session.Snapshot);
        Assert.False(harness.Coordinator.StartNewBatch(before.Revision - 1).Applied);
        Assert.Same(before, harness.Session.Snapshot);
        Assert.True(harness.Coordinator.ApplyLanguageRules(latest).Applied);
        Assert.Same(before, harness.Session.Snapshot);
    }
}
