using TrackTrim.Application.Batches;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Media;
using Xunit;

namespace TrackTrim.Application.Tests.Batches;

/// <summary>Checks that one explicit Review issues separate current plans while retaining established validation.</summary>
public sealed class BatchSelectionReviewTests
{
    /// <summary>One-item use needs no separate lifecycle and a no-op edit preserves the exact approval/review.</summary>
    [Fact]
    public async Task OneReviewApprovesEachPlanAndNoOpRetainsAuthority()
    {
        var harness = new BatchTestHarness(1);
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        SetDestinations(harness);
        var reviewed = await Review(harness);
        Assert.NotNull(reviewed.Review);
        Assert.Single(reviewed.Review.Items);
        var before = harness.Session.Snapshot;
        var projection = harness.Session.CreateTrackProjection();
        var audio = Assert.Single(projection.Groups, group => group.Key.Kind == TrackKind.Audio);
        Assert.True(harness.Session.SetTrackSelection(new(projection, audio.Members, true)).Applied);
        Assert.Same(before, harness.Session.Snapshot);
        var rereviewed = await Review(harness);
        Assert.NotNull(rereviewed.Review);
        Assert.Same(before.Items[0].ApprovedPlan, harness.Session.Snapshot.Items[0].ApprovedPlan);
        Assert.Empty(harness.RemuxedPaths);
    }

    /// <summary>Unresolved included files block Review until explicitly excluded, preserving valid sibling plans.</summary>
    [Fact]
    public async Task UnresolvedIncludedFilesAreNeverSilentlySkipped()
    {
        var harness = new BatchTestHarness(1);
        harness.Session.AddDiscoveryResult(new([], [new("missing.mkv", new(ErrorCode.InvalidSource))]));
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        SetDestinations(harness);
        var failed = await Review(harness);
        Assert.Null(failed.Review);
        Assert.Equal(ErrorCode.InvalidSource, Assert.Single(failed.ItemFailures).Failure.Code);
        Assert.NotNull(harness.Session.Snapshot.Items[0].ApprovedPlan);
        Assert.True(harness.Session.SetIncluded(harness.Session.Snapshot.Items[1].Id, false).Applied);
        Assert.NotNull((await Review(harness)).Review);
        Assert.Empty(harness.RemuxedPaths);
    }

    /// <summary>Consent is exact-file/revision bound and expires whenever the actual zero-audio selection changes.</summary>
    [Fact]
    public async Task ZeroAudioNeedsExplicitPerFileCurrentConsent()
    {
        var harness = new BatchTestHarness(1);
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        SetDestinations(harness);
        var item = harness.Session.Snapshot.Items[0];
        Assert.True(harness.Session.SetSelection(new(item.Id, [0, 2])).Applied);
        var noConsent = await Review(harness);
        Assert.Equal(ErrorCode.ZeroAudioConfirmationRequired, Assert.Single(noConsent.ItemFailures).Failure.Code);
        Assert.Null(harness.Session.Snapshot.Items[0].ApprovedPlan);
        var consentRevision = harness.Session.Snapshot.Revision;
        var approved = await harness.Coordinator.ReviewSelectionsAsync(new(consentRevision, [item.Id]));
        Assert.NotNull(approved.Review);
        Assert.True(harness.Session.Snapshot.Items[0].ApprovedPlan!.ZeroAudioConfirmed);
        Assert.True(harness.Session.SetSelection(new(item.Id, [0])).Applied);
        var afterChange = harness.Session.Snapshot;
        var stale = await harness.Coordinator.ReviewSelectionsAsync(new(consentRevision, [item.Id]));
        Assert.Equal(ErrorCode.StalePlan, stale.Failure!.Code);
        Assert.Same(afterChange, harness.Session.Snapshot);
        Assert.Equal(ErrorCode.ZeroAudioConfirmationRequired, Assert.Single((await Review(harness)).ItemFailures).Failure.Code);
    }

    /// <summary>Existing source-change and destination-collision checks remain part of the one-click boundary.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CurrentSourceAndDestinationAreValidated(bool changedSource)
    {
        var harness = new BatchTestHarness(1);
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        SetDestinations(harness);
        var item = harness.Session.Snapshot.Items[0];

        if (changedSource)
        {
            harness.Revisions[item.SourcePath] = item.Inspection!.Source with { LastWriteUtcTicks = 999 };
        }

        if (!changedSource)
        {
            harness.DestinationFailures[item.IntendedDestinationPath!] = new(ErrorCode.DestinationExists);
        }

        var result = await Review(harness);
        Assert.Null(result.Review);
        Assert.Equal(changedSource ? ErrorCode.SourceChanged : ErrorCode.DestinationExists, Assert.Single(result.ItemFailures).Failure.Code);
        Assert.Empty(harness.Reservations);
    }

    /// <summary>Review owns the edit gate during asynchronous validation and stale projections cannot interrupt it.</summary>
    [Fact]
    public async Task BusyInspectionRejectsAggregateEditsAndReviewWithoutStateLoss()
    {
        var harness = new BatchTestHarness(1);
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        SetDestinations(harness);
        var projection = harness.Session.CreateTrackProjection();
        var entered = BatchTestHarness.Signal();
        var release = BatchTestHarness.Signal();
        harness.OnInspect = async _ => { entered.SetResult(); await release.Task; };
        var inspect = harness.Coordinator.InspectItemAsync(new(harness.Session.Snapshot.Items[0].Id));
        await BatchTestHarness.Await(entered.Task);
        var before = harness.Session.Snapshot;
        Assert.Equal(ErrorCode.Busy, harness.Session.SetTrackSelection(new(projection,
            projection.Groups.Single(group => group.Key.Kind == TrackKind.Audio).Members, false)).Failure!.Code);
        Assert.Equal(ErrorCode.Busy, (await Review(harness)).Failure!.Code);
        Assert.Same(before, harness.Session.Snapshot);
        release.SetResult();
        await BatchTestHarness.Await(inspect);
    }

    /// <summary>Captures the session revision for the single explicit review command.</summary>
    private static Task<BatchReviewResult> Review(BatchTestHarness harness) => harness.Coordinator.ReviewSelectionsAsync(
        new(harness.Session.Snapshot.Revision, [], TestContext.Current.CancellationToken));

    /// <summary>Records separate destinations as input, without pre-approving selections.</summary>
    private static void SetDestinations(BatchTestHarness harness)
    {
        foreach (var item in harness.Session.Snapshot.Items)
        {
            Assert.True(harness.Session.SetDestination(new(item.Id, Path.ChangeExtension(item.SourcePath, ".trimmed.mkv"))).Applied);
        }
    }
}
