using System.Collections.Immutable;
using MovieSift.Application.Batches;
using MovieSift.Core.Failures;
using MovieSift.Core.Media;
using MovieSift.Core.Rules;
using Xunit;

namespace MovieSift.Application.Tests.Batches;

/// <summary>Checks title-rule review against immutable file-local selections and existing batch approvals.</summary>
public sealed class BatchTitleFilterTests
{
    private const string LiteralTitle = "Main [cut].*";

    private static LanguageRules TitleRules => new(
        new(LanguageRuleMode.KeepMatching, ["eng"], new(true, " main [CUT].* ")),
        new(LanguageRuleMode.Unchanged, []));

    /// <summary>Combines language and literal title matching using each inspection's own order and identifiers.</summary>
    [Fact]
    public async Task CombinedPreviewIsPureAndApplicationUsesIndependentFileMappings()
    {
        var harness = new BatchTestHarness();
        ConfigureTitles(harness);
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        var before = harness.Session.Snapshot;
        var rules = TitleRules;
        var changes = 0;
        harness.Session.Changed += _ => changes++;

        var preview = harness.Coordinator.PreviewLanguageRules(rules).Preview!;

        Assert.Same(before, harness.Session.Snapshot);
        Assert.Same(rules, preview.Rules);
        Assert.Equal(0, changes);
        Assert.Equal([0, 1, 7, 3, 2], preview.Items[0].BeforeRetainedTrackIds);
        Assert.Equal([12, 10, 44, 11, 99], preview.Items[1].BeforeRetainedTrackIds);
        Assert.Equal([0, 1, 2], preview.Items[0].ProposedRetainedTrackIds);
        Assert.Equal([12, 10, 44], preview.Items[1].ProposedRetainedTrackIds);
        Assert.All(preview.Items, item => Assert.Equal(BatchLanguagePreviewState.Changed, item.State));

        var result = harness.Coordinator.ApplyLanguageRules(preview);
        var after = harness.Session.Snapshot;

        Assert.True(result.Applied);
        Assert.Equal(before.Items.Select(item => item.Id), result.ChangedItemIds);
        Assert.Equal(before.Revision + 1, after.Revision);
        Assert.Equal(1, changes);
        Assert.Equal([0, 1, 2], after.Items[0].CurrentRetainedTrackIds);
        Assert.Equal([12, 10, 44], after.Items[1].CurrentRetainedTrackIds);
        Assert.Same(before.Items[0].Inspection, after.Items[0].Inspection);
        Assert.Same(before.Items[1].Inspection, after.Items[1].Inspection);
        Assert.All(after.Items, item =>
        {
            Assert.True(item.Included);
            Assert.Equal(BatchItemState.NeedsReview, item.State);
            Assert.Null(item.ApprovedPlan);
            Assert.Null(item.Result);
        });
        Assert.Empty(harness.RemuxedPaths);
        Assert.Empty(harness.DestinationValidations);
        Assert.Empty(harness.Reservations);
        Assert.Equal(0, harness.VerifyCalls);
    }

    /// <summary>Invalidates only the approval whose current Keep IDs change and clears aggregate review once.</summary>
    [Fact]
    public async Task CombinedApplyPreservesAnAlreadyMatchingManualApproval()
    {
        var harness = new BatchTestHarness();
        ConfigureTitles(harness);
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        Assert.True(harness.Session.SetSelection(new(harness.Session.Snapshot.Items[1].Id, [12, 10, 44])).Applied);
        await ApproveCurrentSelectionsAsync(harness);
        await harness.Coordinator.ReviewSelectionsAsync(new(harness.Session.Snapshot.Revision, [], TestContext.Current.CancellationToken));
        var before = harness.Session.Snapshot;
        Assert.NotNull(before.Review);
        var validationCount = harness.DestinationValidations.Count;
        var changes = 0;
        harness.Session.Changed += _ => changes++;
        var preview = harness.Coordinator.PreviewLanguageRules(TitleRules).Preview!;
        Assert.Equal(BatchLanguagePreviewState.Changed, preview.Items[0].State);
        Assert.Equal(BatchLanguagePreviewState.Unchanged, preview.Items[1].State);

        var result = harness.Coordinator.ApplyLanguageRules(preview);
        var after = harness.Session.Snapshot;

        Assert.True(result.Applied);
        Assert.Equal(before.Items[0].Id, Assert.Single(result.ChangedItemIds));
        Assert.Equal(before.Revision + 1, after.Revision);
        Assert.Equal(1, changes);
        Assert.Null(after.Review);
        Assert.Null(after.Items[0].ApprovedPlan);
        Assert.Equal(BatchItemState.NeedsReview, after.Items[0].State);
        Assert.Same(before.Items[1], after.Items[1]);
        Assert.Same(before.Items[1].ApprovedPlan, after.Items[1].ApprovedPlan);
        Assert.Equal(validationCount, harness.DestinationValidations.Count);
        Assert.Empty(harness.RemuxedPaths);
    }

    /// <summary>Retains the exact approved snapshot when a repeated title proposal changes no selection.</summary>
    [Fact]
    public async Task RepeatedMatchingTitleApplyPreservesExactAggregateReview()
    {
        var harness = new BatchTestHarness();
        ConfigureTitles(harness);
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        var first = harness.Coordinator.PreviewLanguageRules(TitleRules).Preview!;
        Assert.True(harness.Coordinator.ApplyLanguageRules(first).Applied);
        await ApproveCurrentSelectionsAsync(harness);
        await harness.Coordinator.ReviewSelectionsAsync(new(harness.Session.Snapshot.Revision, [], TestContext.Current.CancellationToken));
        var before = harness.Session.Snapshot;
        Assert.NotNull(before.Review);
        var changes = 0;
        harness.Session.Changed += _ => changes++;
        var preview = harness.Coordinator.PreviewLanguageRules(TitleRules).Preview!;
        Assert.All(preview.Items, item => Assert.Equal(BatchLanguagePreviewState.Unchanged, item.State));

        var result = harness.Coordinator.ApplyLanguageRules(preview);

        Assert.True(result.Applied);
        Assert.Empty(result.ChangedItemIds);
        Assert.Same(before, harness.Session.Snapshot);
        Assert.Equal(0, changes);
        Assert.Empty(harness.RemuxedPaths);
    }

    /// <summary>Keeps the whole unresolved file and its approval while allowing a reliable sibling proposal.</summary>
    [Fact]
    public async Task NoAudioTitleMatchesPreservesWholeManualRowAndApproval()
    {
        var harness = new BatchTestHarness();
        ConfigureTitles(harness);
        var firstPath = harness.Inspections.Keys.First();
        var firstInspection = harness.Inspections[firstPath];
        harness.Inspections[firstPath] = firstInspection with
        {
            Tracks = firstInspection.Tracks.Select(track => track.Kind == TrackKind.Audio
                ? track with { Title = "Commentary" } : track).ToImmutableArray()
        };
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        await ApproveCurrentSelectionsAsync(harness);
        await harness.Coordinator.ReviewSelectionsAsync(new(harness.Session.Snapshot.Revision, [], TestContext.Current.CancellationToken));
        var before = harness.Session.Snapshot;
        var rules = TitleRules with { Subtitles = new(LanguageRuleMode.KeepMatching, ["rus"]) };
        var preview = harness.Coordinator.PreviewLanguageRules(rules).Preview!;
        var manual = preview.Items[0];
        Assert.Equal(BatchLanguagePreviewState.ManualReview, manual.State);
        Assert.Contains(manual.Issues, issue => issue.Reason == LanguageRuleIssueReason.NoAudioMatches);
        Assert.Equal(manual.BeforeRetainedTrackIds, manual.ProposedRetainedTrackIds);
        Assert.False(manual.NoSubtitleMatches);
        Assert.Equal(BatchLanguagePreviewState.Changed, preview.Items[1].State);

        var result = harness.Coordinator.ApplyLanguageRules(preview);

        Assert.True(result.Applied);
        Assert.Equal(before.Items[1].Id, Assert.Single(result.ChangedItemIds));
        Assert.Same(before.Items[0], harness.Session.Snapshot.Items[0]);
        Assert.NotNull(harness.Session.Snapshot.Items[0].ApprovedPlan);
        Assert.True(harness.Session.Snapshot.Items[0].Included);
        Assert.Contains(2, harness.Session.Snapshot.Items[0].CurrentRetainedTrackIds);
        Assert.Empty(harness.RemuxedPaths);
    }

    /// <summary>Reports an enabled empty filter without silently clearing choices or revoking manual approvals.</summary>
    [Fact]
    public async Task EmptyTitleConfigurationLeavesAllManualRowsAndReviewUntouched()
    {
        var harness = new BatchTestHarness();
        ConfigureTitles(harness);
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        await ApproveCurrentSelectionsAsync(harness);
        await harness.Coordinator.ReviewSelectionsAsync(new(harness.Session.Snapshot.Revision, [], TestContext.Current.CancellationToken));
        var before = harness.Session.Snapshot;
        var rules = TitleRules with
        {
            Audio = TitleRules.Audio with { TitleFilter = new(true, " \t ") },
            Subtitles = new(LanguageRuleMode.KeepMatching, ["rus"])
        };
        var preview = harness.Coordinator.PreviewLanguageRules(rules).Preview!;
        Assert.All(preview.Items, item =>
        {
            Assert.Equal(BatchLanguagePreviewState.ManualReview, item.State);
            Assert.Contains(item.Issues, issue => issue.Reason == LanguageRuleIssueReason.EmptyTitleFilter);
            Assert.Equal(item.BeforeRetainedTrackIds, item.ProposedRetainedTrackIds);
            Assert.False(item.NoSubtitleMatches);
        });

        var result = harness.Coordinator.ApplyLanguageRules(preview);

        Assert.True(result.Applied);
        Assert.Empty(result.ChangedItemIds);
        Assert.Same(before, harness.Session.Snapshot);
        Assert.NotNull(before.Review);
        Assert.All(before.Items, item => Assert.NotNull(item.ApprovedPlan));
        Assert.Empty(harness.RemuxedPaths);
    }

    /// <summary>Leaves completed publication evidence and excluded rows intact when a remaining file changes.</summary>
    [Fact]
    public async Task TitleApplyPreservesCompletedAndExcludedItems()
    {
        var harness = new BatchTestHarness(3);
        ConfigureTitles(harness);
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        var original = harness.Session.Snapshot.Items;

        foreach (var item in original)
        {
            await harness.SelectAndReviewAsync(item);
        }

        Assert.True(harness.Session.SetIncluded(original[1].Id, false).Applied);
        Assert.True(harness.Session.SetIncluded(original[2].Id, false).Applied);
        var review = (await harness.Coordinator.ReviewSelectionsAsync(new(harness.Session.Snapshot.Revision, [], TestContext.Current.CancellationToken))).Review!;
        await harness.Coordinator.RunAsync(new(review.Id, CancellationToken: TestContext.Current.CancellationToken));
        Assert.True(harness.Session.SetIncluded(original[1].Id, true).Applied);
        var before = harness.Session.Snapshot;
        Assert.Equal(BatchItemState.Completed, before.Items[0].State);
        Assert.NotNull(before.Items[0].Result!.PublishedPath);
        var remuxCount = harness.RemuxedPaths.Count;
        var preview = harness.Coordinator.PreviewLanguageRules(TitleRules).Preview!;
        Assert.Equal([BatchLanguagePreviewState.Completed, BatchLanguagePreviewState.Changed,
            BatchLanguagePreviewState.Excluded], preview.Items.Select(item => item.State));

        var result = harness.Coordinator.ApplyLanguageRules(preview);

        Assert.True(result.Applied);
        Assert.Equal(before.Items[1].Id, Assert.Single(result.ChangedItemIds));
        Assert.Same(before.Items[0], harness.Session.Snapshot.Items[0]);
        Assert.Same(before.Items[2], harness.Session.Snapshot.Items[2]);
        Assert.Equal(remuxCount, harness.RemuxedPaths.Count);
        Assert.Equal([10, 44], harness.Session.Snapshot.Items[1].CurrentRetainedTrackIds);
    }

    /// <summary>Rejects a title proposal after any later selection, destination, inclusion, inspection or batch reset.</summary>
    [Theory]
    [InlineData("selection")]
    [InlineData("destination")]
    [InlineData("inclusion")]
    [InlineData("inspection")]
    [InlineData("new-batch")]
    public async Task BatchEditsMakeTitlePreviewStaleWithoutPartialApplication(string edit)
    {
        var harness = new BatchTestHarness();
        ConfigureTitles(harness);
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        await ApproveCurrentSelectionsAsync(harness);
        var first = harness.Session.Snapshot.Items[0];
        var preview = harness.Coordinator.PreviewLanguageRules(TitleRules).Preview!;

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

    /// <summary>Rejects copied previews with modified nested title configuration even at the same batch revision.</summary>
    [Theory]
    [InlineData("text")]
    [InlineData("enabled")]
    [InlineData("removed")]
    public async Task TamperedTitleConfigurationCannotApplyIssuedProposal(string tampering)
    {
        var harness = new BatchTestHarness();
        ConfigureTitles(harness);
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        var preview = harness.Coordinator.PreviewLanguageRules(TitleRules).Preview!;
        var filter = preview.Rules.Audio.TitleFilter!;
        var tamperedFilter = tampering switch
        {
            "text" => filter with { Text = "Commentary" },
            "enabled" => filter with { Enabled = false },
            _ => null
        };
        var tampered = preview with
        {
            Rules = preview.Rules with { Audio = preview.Rules.Audio with { TitleFilter = tamperedFilter } }
        };
        var before = harness.Session.Snapshot;

        var result = harness.Coordinator.ApplyLanguageRules(tampered);

        Assert.False(result.Applied);
        Assert.Equal(ErrorCode.StalePlan, result.Failure!.Code);
        Assert.Empty(result.ChangedItemIds);
        Assert.Same(before, harness.Session.Snapshot);
        Assert.True(harness.Coordinator.ApplyLanguageRules(preview).Applied);
        Assert.Equal([0, 1, 2], harness.Session.Snapshot.Items[0].CurrentRetainedTrackIds);
    }

    /// <summary>Requires a newly issued proposal for changed title input and never reuses the older before-and-after rows.</summary>
    [Fact]
    public async Task NewTitlePreviewSupersedesOldPreviewWithoutEditingSession()
    {
        var harness = new BatchTestHarness();
        ConfigureTitles(harness);
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        var before = harness.Session.Snapshot;
        var rules = TitleRules;
        var first = harness.Coordinator.PreviewLanguageRules(rules).Preview!;
        var changedRules = rules with { Audio = rules.Audio with { TitleFilter = new(true, "Commentary") } };
        var latest = harness.Coordinator.PreviewLanguageRules(changedRules).Preview!;
        Assert.Equal(first.Revision, latest.Revision);
        Assert.Same(before, harness.Session.Snapshot);
        Assert.Equal([0, 1, 2], first.Items[0].ProposedRetainedTrackIds);
        Assert.Equal([0, 3, 2], latest.Items[0].ProposedRetainedTrackIds);
        Assert.Equal(BatchLanguagePreviewState.ManualReview, latest.Items[1].State);

        var stale = harness.Coordinator.ApplyLanguageRules(first);

        Assert.False(stale.Applied);
        Assert.Equal(ErrorCode.StalePlan, stale.Failure!.Code);
        Assert.Same(before, harness.Session.Snapshot);
        var applied = harness.Coordinator.ApplyLanguageRules(latest);
        Assert.True(applied.Applied);
        Assert.Equal(before.Items[0].Id, Assert.Single(applied.ChangedItemIds));
        Assert.Equal([0, 3, 2], harness.Session.Snapshot.Items[0].CurrentRetainedTrackIds);
        Assert.Same(before.Items[1], harness.Session.Snapshot.Items[1]);
        Assert.Empty(harness.RemuxedPaths);
    }

    /// <summary>Creates literal-title distractors with separate language matches and reordered file-local identifiers.</summary>
    private static void ConfigureTitles(BatchTestHarness harness)
    {
        BatchLanguageTestData.ConfigureDistinctLanguages(harness);
        var paths = harness.Inspections.Keys.ToArray();
        var first = harness.Inspections[paths[0]];
        var second = harness.Inspections[paths[1]];
        harness.Inspections[paths[0]] = first with
        {
            Tracks = [first.Tracks[0], first.Tracks[1] with { Title = LiteralTitle },
                first.Tracks[2] with { Title = LiteralTitle },
                first.Tracks[1] with { Id = 3, Uid = 300, Title = "Commentary" }, first.Tracks[3]]
        };
        harness.Inspections[paths[1]] = second with
        {
            Tracks = [second.Tracks[0], second.Tracks[1], second.Tracks[2] with { Title = "Original " + LiteralTitle },
                second.Tracks[3] with { Title = LiteralTitle },
                second.Tracks[2] with { Id = 99, Uid = 9900, Title = "Main cut anything" }]
        };
    }

    /// <summary>Approves each current explicit Keep set without inferring another item's local track identifiers.</summary>
    private static async Task ApproveCurrentSelectionsAsync(BatchTestHarness harness)
    {
        foreach (var item in harness.Session.Snapshot.Items)
        {
            Assert.True(harness.Session.SetDestination(new(item.Id,
                Path.ChangeExtension(item.SourcePath, ".title-trimmed.mkv"))).Applied);
        }

        var reviewed = await harness.Coordinator.ReviewSelectionsAsync(new(harness.Session.Snapshot.Revision, [], TestContext.Current.CancellationToken));
        Assert.NotNull(reviewed.Review);
    }
}
