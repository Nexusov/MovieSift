using System.Collections.Immutable;
using TrackTrim.Application.Batches;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Media;
using TrackTrim.Core.Planning;
using TrackTrim.Core.Rules;
using Xunit;

namespace TrackTrim.Application.Tests.Batches;

/// <summary>Checks forced-subtitle configuration through the existing immutable preview and batch ownership guards.</summary>
public sealed class BatchForcedSubtitleTests
{
    private const string SubtitleTitle = "Captions [SDH]";
    private static LanguageRules ForcedRules => new(
        new(LanguageRuleMode.Unchanged, []),
        new(LanguageRuleMode.KeepMatching, ["eng"], new(true, SubtitleTitle)),
        KeepOnlyForcedSubtitles: true);

    /// <summary>Previews all conjunctive matches independently without mutating choices, metadata or workflow state.</summary>
    [Fact]
    public async Task ForcedPreviewIsPureAndUsesEachFilesActualIdentifiers()
    {
        var harness = CreateHarness();
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        var before = harness.Session.Snapshot;
        var rules = ForcedRules;
        var changes = 0;
        harness.Session.Changed += _ => changes++;

        var preview = harness.Coordinator.PreviewLanguageRules(rules).Preview!;

        Assert.Same(before, harness.Session.Snapshot);
        Assert.Same(rules, preview.Rules);
        Assert.True(preview.Rules.KeepOnlyForcedSubtitles);
        Assert.Equal(0, changes);
        Assert.Equal([0, 1, 2, 17, 4, 29, 32], preview.Items[0].BeforeRetainedTrackIds);
        Assert.Equal([12, 10, 44, 11, 37, 8], preview.Items[1].BeforeRetainedTrackIds);
        Assert.Equal([0, 1, 2, 32], preview.Items[0].ProposedRetainedTrackIds);
        Assert.Equal([10, 44, 11], preview.Items[1].ProposedRetainedTrackIds);
        Assert.All(preview.Items, item =>
        {
            Assert.Equal(BatchLanguagePreviewState.Changed, item.State);
            Assert.Empty(item.Issues);
            Assert.False(item.NoSubtitleMatches);
        });
        Assert.False(before.Items[0].Inspection!.Tracks.Single(track => track.Id == 17).IsForced);
        Assert.True(before.Items[0].Inspection!.Tracks.Single(track => track.Id == 2).IsForced);
        Assert.Empty(harness.RemuxedPaths);
        Assert.Empty(harness.DestinationValidations);
        Assert.Empty(harness.Reservations);
        Assert.Equal(0, harness.VerifyCalls);
        Assert.Equal(0, harness.CleanupCalls);
    }

    /// <summary>Applies one changed file in one revision while preserving the already matching file's approval.</summary>
    [Fact]
    public async Task ForcedApplyInvalidatesOnlyActuallyChangedApprovals()
    {
        var harness = CreateHarness();
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        Assert.True(harness.Session.SetSelection(new(harness.Session.Snapshot.Items[1].Id, [10, 44, 11])).Applied);
        ApproveCurrent(harness);
        await harness.Coordinator.ReviewAsync(TestContext.Current.CancellationToken);
        var before = harness.Session.Snapshot;
        var validationCount = harness.DestinationValidations.Count;
        var preview = harness.Coordinator.PreviewLanguageRules(ForcedRules).Preview!;
        Assert.Equal(BatchLanguagePreviewState.Changed, preview.Items[0].State);
        Assert.Equal(BatchLanguagePreviewState.Unchanged, preview.Items[1].State);
        var changes = 0;
        harness.Session.Changed += _ => changes++;

        var result = harness.Coordinator.ApplyLanguageRules(preview);
        var after = harness.Session.Snapshot;

        Assert.True(result.Applied);
        Assert.Equal(before.Items[0].Id, Assert.Single(result.ChangedItemIds));
        Assert.Equal(before.Revision + 1, after.Revision);
        Assert.Equal(1, changes);
        Assert.Null(after.Review);
        Assert.Equal([0, 1, 2, 32], after.Items[0].CurrentRetainedTrackIds);
        Assert.Null(after.Items[0].ApprovedPlan);
        Assert.Equal(BatchItemState.NeedsReview, after.Items[0].State);
        Assert.Same(before.Items[1], after.Items[1]);
        Assert.Same(before.Items[1].ApprovedPlan, after.Items[1].ApprovedPlan);
        Assert.Equal(validationCount, harness.DestinationValidations.Count);
        Assert.Empty(harness.RemuxedPaths);
        Assert.Equal(0, harness.CleanupCalls);
    }

    /// <summary>Preserves the exact approved snapshot and review when every forced proposal is already selected.</summary>
    [Fact]
    public async Task NoOpForcedApplyPreservesApprovalsAndAggregateReview()
    {
        var harness = CreateHarness();
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        var first = harness.Coordinator.PreviewLanguageRules(ForcedRules).Preview!;
        Assert.True(harness.Coordinator.ApplyLanguageRules(first).Applied);
        ApproveCurrent(harness);
        await harness.Coordinator.ReviewAsync(TestContext.Current.CancellationToken);
        var before = harness.Session.Snapshot;
        var changes = 0;
        harness.Session.Changed += _ => changes++;
        var preview = harness.Coordinator.PreviewLanguageRules(ForcedRules).Preview!;
        Assert.All(preview.Items, item => Assert.Equal(BatchLanguagePreviewState.Unchanged, item.State));

        var result = harness.Coordinator.ApplyLanguageRules(preview);

        Assert.True(result.Applied);
        Assert.Empty(result.ChangedItemIds);
        Assert.Same(before, harness.Session.Snapshot);
        Assert.NotNull(before.Review);
        Assert.All(before.Items, item => Assert.NotNull(item.ApprovedPlan));
        Assert.Equal(0, changes);
        Assert.Empty(harness.RemuxedPaths);
    }

    /// <summary>Ignores forced-only when subtitle mode is Unchanged, including kept non-forced and excluded forced tracks.</summary>
    [Fact]
    public async Task UnchangedSubtitlesIgnoreTheForcedSettingCompletely()
    {
        var harness = CreateHarness();
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        Assert.True(harness.Session.SetSelection(new(harness.Session.Snapshot.Items[0].Id, [0, 1, 17])).Applied);
        ApproveCurrent(harness);
        await harness.Coordinator.ReviewAsync(TestContext.Current.CancellationToken);
        var before = harness.Session.Snapshot;
        var rules = new LanguageRules(new(LanguageRuleMode.KeepMatching, ["en"]),
            new(LanguageRuleMode.Unchanged, ["invalid"], new(true, " ")), KeepOnlyForcedSubtitles: true);

        var preview = harness.Coordinator.PreviewLanguageRules(rules).Preview!;
        var result = harness.Coordinator.ApplyLanguageRules(preview);

        Assert.All(preview.Items, item => Assert.Equal(BatchLanguagePreviewState.Unchanged, item.State));
        Assert.Equal([0, 1, 17], preview.Items[0].ProposedRetainedTrackIds);
        Assert.True(result.Applied);
        Assert.Empty(result.ChangedItemIds);
        Assert.Same(before, harness.Session.Snapshot);
        Assert.NotNull(before.Review);
        Assert.False(before.Items[0].Inspection!.Tracks.Single(track => track.Id == 17).IsForced);
        Assert.Empty(harness.RemuxedPaths);
    }

    /// <summary>Does not hide an uncertain unforced track behind the flag or title constraint.</summary>
    [Fact]
    public async Task UnknownUnforcedLanguageKeepsTheWholeManualRowAndApproval()
    {
        var harness = CreateHarness();
        var firstPath = harness.Inspections.Keys.First();
        var inspection = harness.Inspections[firstPath];
        harness.Inspections[firstPath] = inspection with
        {
            Tracks = inspection.Tracks.Select(track => track.Id == 17
                ? track with { Language = "und", LanguageIetf = null, Title = "Unrelated title" } : track).ToImmutableArray()
        };
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        ApproveCurrent(harness);
        await harness.Coordinator.ReviewAsync(TestContext.Current.CancellationToken);
        var before = harness.Session.Snapshot;

        var preview = harness.Coordinator.PreviewLanguageRules(ForcedRules).Preview!;
        var result = harness.Coordinator.ApplyLanguageRules(preview);

        Assert.Equal(BatchLanguagePreviewState.ManualReview, preview.Items[0].State);
        Assert.Contains(preview.Items[0].Issues, issue => issue.TrackId == 17 && issue.Reason == LanguageRuleIssueReason.UnknownLanguage);
        Assert.Equal(preview.Items[0].BeforeRetainedTrackIds, preview.Items[0].ProposedRetainedTrackIds);
        Assert.False(preview.Items[0].NoSubtitleMatches);
        Assert.True(result.Applied);
        Assert.Equal(before.Items[1].Id, Assert.Single(result.ChangedItemIds));
        Assert.Same(before.Items[0], harness.Session.Snapshot.Items[0]);
        Assert.NotNull(harness.Session.Snapshot.Items[0].ApprovedPlan);
        Assert.True(harness.Session.Snapshot.Items[0].Included);
        Assert.Contains(17, harness.Session.Snapshot.Items[0].CurrentRetainedTrackIds);
        Assert.Empty(harness.RemuxedPaths);
    }

    /// <summary>Retains completed output evidence and an excluded row while a remaining file receives forced choices.</summary>
    [Fact]
    public async Task CompletedAndExcludedRowsRemainUntouched()
    {
        var harness = CreateHarness(3);
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        var original = harness.Session.Snapshot.Items;

        foreach (var item in original)
        {
            harness.Approve(item);
        }

        Assert.True(harness.Session.SetIncluded(original[1].Id, false).Applied);
        Assert.True(harness.Session.SetIncluded(original[2].Id, false).Applied);
        var review = (await harness.Coordinator.ReviewAsync(TestContext.Current.CancellationToken)).Review!;
        await harness.Coordinator.RunAsync(new(review.Id, CancellationToken: TestContext.Current.CancellationToken));
        Assert.True(harness.Session.SetIncluded(original[1].Id, true).Applied);
        var before = harness.Session.Snapshot;
        var remuxCount = harness.RemuxedPaths.Count;

        var preview = harness.Coordinator.PreviewLanguageRules(ForcedRules).Preview!;
        var result = harness.Coordinator.ApplyLanguageRules(preview);

        Assert.Equal([BatchLanguagePreviewState.Completed, BatchLanguagePreviewState.Changed,
            BatchLanguagePreviewState.Excluded], preview.Items.Select(item => item.State));
        Assert.True(result.Applied);
        Assert.Equal(before.Items[1].Id, Assert.Single(result.ChangedItemIds));
        Assert.Same(before.Items[0], harness.Session.Snapshot.Items[0]);
        Assert.NotNull(before.Items[0].Result!.PublishedPath);
        Assert.Same(before.Items[2], harness.Session.Snapshot.Items[2]);
        Assert.Equal([10, 44, 11], harness.Session.Snapshot.Items[1].CurrentRetainedTrackIds);
        Assert.Equal(remuxCount, harness.RemuxedPaths.Count);
    }

    /// <summary>Rejects a copied preview with a modified forced setting without consuming the actual issued proposal.</summary>
    [Fact]
    public async Task TamperedForcedConfigurationCannotApply()
    {
        var harness = CreateHarness();
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        var preview = harness.Coordinator.PreviewLanguageRules(ForcedRules).Preview!;
        var tampered = preview with { Rules = preview.Rules with { KeepOnlyForcedSubtitles = false } };
        var before = harness.Session.Snapshot;

        var rejected = harness.Coordinator.ApplyLanguageRules(tampered);

        Assert.False(rejected.Applied);
        Assert.Equal(ErrorCode.StalePlan, rejected.Failure!.Code);
        Assert.Same(before, harness.Session.Snapshot);
        Assert.True(preview.Rules.KeepOnlyForcedSubtitles);
        Assert.False(tampered.Rules.KeepOnlyForcedSubtitles);
        Assert.True(harness.Coordinator.ApplyLanguageRules(preview).Applied);
        Assert.Equal([0, 1, 2, 32], harness.Session.Snapshot.Items[0].CurrentRetainedTrackIds);
        Assert.Empty(harness.RemuxedPaths);
    }

    /// <summary>Requires the latest issued flag configuration even when both previews have the same batch revision.</summary>
    [Fact]
    public async Task NewForcedSettingPreviewSupersedesTheEarlierProposal()
    {
        var harness = CreateHarness();
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        var before = harness.Session.Snapshot;
        var forced = harness.Coordinator.PreviewLanguageRules(ForcedRules).Preview!;
        var unrestricted = harness.Coordinator.PreviewLanguageRules(ForcedRules with { KeepOnlyForcedSubtitles = false }).Preview!;
        Assert.Equal(forced.Revision, unrestricted.Revision);
        Assert.Same(before, harness.Session.Snapshot);
        Assert.Equal([0, 1, 2, 32], forced.Items[0].ProposedRetainedTrackIds);
        Assert.Equal([0, 1, 2, 17, 32], unrestricted.Items[0].ProposedRetainedTrackIds);

        Assert.Equal(ErrorCode.StalePlan, harness.Coordinator.ApplyLanguageRules(forced).Failure!.Code);
        Assert.Same(before, harness.Session.Snapshot);
        Assert.True(harness.Coordinator.ApplyLanguageRules(unrestricted).Applied);
        Assert.Equal([0, 1, 2, 17, 32], harness.Session.Snapshot.Items[0].CurrentRetainedTrackIds);
        Assert.Equal([12, 10, 44, 11], harness.Session.Snapshot.Items[1].CurrentRetainedTrackIds);
        Assert.Empty(harness.RemuxedPaths);
    }

    /// <summary>Rejects stale forced proposals after selection edits, replacement inspections or a new batch.</summary>
    [Theory]
    [InlineData("selection")]
    [InlineData("inspection")]
    [InlineData("new-batch")]
    public async Task RelevantBatchChangesRejectForcedPreviewAtomically(string change)
    {
        var harness = CreateHarness();
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        ApproveCurrent(harness);
        var item = harness.Session.Snapshot.Items[0];
        var preview = harness.Coordinator.PreviewLanguageRules(ForcedRules).Preview!;

        switch (change)
        {
            case "selection":
                Assert.True(harness.Session.SetSelection(new(item.Id, [0, 1, 17])).Applied);
                break;
            case "inspection":
                harness.Inspections[item.SourcePath] = item.Inspection! with { Title = "Replacement inspection" };
                await harness.Coordinator.InspectItemAsync(new(item.Id, TestContext.Current.CancellationToken));
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

    /// <summary>Uses the same nonblocking owner gate while a real coordinator request is paused in its controlled worker port.</summary>
    [Fact]
    public async Task ActiveRemuxRejectsForcedPreviewAndApply()
    {
        var harness = CreateHarness(1);
        var review = await harness.PrepareAsync();
        var preview = harness.Coordinator.PreviewLanguageRules(ForcedRules).Preview!;
        var entered = BatchTestHarness.Signal();
        var release = BatchTestHarness.Signal();
        harness.OnRemux = async request =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(request.CancellationToken);
        };
        var run = harness.Coordinator.RunAsync(new(review.Id, CancellationToken: TestContext.Current.CancellationToken));

        try
        {
            await BatchTestHarness.Await(entered.Task);
            var active = harness.Session.Snapshot;
            Assert.Equal(ErrorCode.Busy, harness.Coordinator.PreviewLanguageRules(ForcedRules).Failure!.Code);
            Assert.Equal(ErrorCode.Busy, harness.Coordinator.ApplyLanguageRules(preview).Failure!.Code);
            Assert.Same(active, harness.Session.Snapshot);
            Assert.Single(harness.RemuxedPaths);
        }
        finally
        {
            release.TrySetResult();
            await BatchTestHarness.Await(run);
        }
    }

    /// <summary>Preserves warning ownership and subsequent failed-cleanup evidence without any extra lifecycle action.</summary>
    [Fact]
    public async Task WarningAndUnsafeCleanupRejectForcedPreviewAndApply()
    {
        var harness = CreateHarness(1);
        var review = await harness.PrepareAsync();
        var preview = harness.Coordinator.PreviewLanguageRules(ForcedRules).Preview!;
        harness.ExitCodes[harness.Session.Snapshot.Items[0].SourcePath] = 1;
        var warning = await harness.Coordinator.RunAsync(new(review.Id, CancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(BatchItemState.AwaitingWarningReview, warning.Items[0].State);
        Assert.Equal(ErrorCode.Busy, harness.Coordinator.PreviewLanguageRules(ForcedRules).Failure!.Code);
        Assert.Equal(ErrorCode.Busy, harness.Coordinator.ApplyLanguageRules(preview).Failure!.Code);
        Assert.Same(warning, harness.Session.Snapshot);
        Assert.Equal(0, harness.CleanupCalls);
        harness.CleanupFails = true;
        var unsafeCleanup = await harness.Coordinator.DiscardWarningsAsync(new(warning.Items[0].Id, TestContext.Current.CancellationToken));

        Assert.True(unsafeCleanup.HasSafetyBlock);
        Assert.Equal(ErrorCode.OwnershipInvalid, harness.Coordinator.PreviewLanguageRules(ForcedRules).Failure!.Code);
        Assert.Equal(ErrorCode.OwnershipInvalid, harness.Coordinator.ApplyLanguageRules(preview).Failure!.Code);
        Assert.Same(unsafeCleanup, harness.Session.Snapshot);
        Assert.Equal(1, harness.CleanupCalls);
        Assert.Single(harness.RemuxedPaths);
    }

    /// <summary>Provides matching and distracting subtitle metadata with different source-local identifiers and ordering.</summary>
    private static BatchTestHarness CreateHarness(int count = 2)
    {
        var harness = new BatchTestHarness(count);
        var paths = harness.Inspections.Keys.ToArray();

        foreach (var path in paths)
        {
            var inspection = harness.Inspections[path];
            var subtitle = inspection.Tracks[2] with { Title = SubtitleTitle, IsForced = true };
            var isFirst = path == paths[0];
            harness.Inspections[path] = inspection with
            {
                Tracks = isFirst
                    ? [inspection.Tracks[0], inspection.Tracks[1], subtitle,
                        subtitle with { Id = 17, Uid = 1700, IsForced = false },
                        subtitle with { Id = 4, Uid = 400, Title = "Alternative" },
                        subtitle with { Id = 29, Uid = 2900, Language = "rus", LanguageIetf = "ru" },
                        subtitle with { Id = 32, Uid = 3200, Title = SubtitleTitle + " alt" }]
                    : [subtitle with { IsForced = false }, inspection.Tracks[0],
                        subtitle with { Id = 44, Uid = 4400 }, inspection.Tracks[1],
                        subtitle with { Id = 37, Uid = 3700, Language = "fre", LanguageIetf = "fr" },
                        subtitle with { Id = 8, Uid = 800, Title = "Other" }]
            };
        }

        return harness;
    }

    /// <summary>Approves current file-local selections explicitly without automatically accepting a rule proposal.</summary>
    private static void ApproveCurrent(BatchTestHarness harness)
    {
        foreach (var item in harness.Session.Snapshot.Items)
        {
            var result = PlanBuilder.Build(new(item.Inspection!, item.CurrentRetainedTrackIds,
                Path.ChangeExtension(item.SourcePath, ".forced-trimmed.mkv"), false));
            Assert.NotNull(result.Plan);
            Assert.True(harness.Session.ApprovePlan(new(item.Id, result.Plan)).Applied);
        }
    }
}
