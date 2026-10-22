using System.Collections.Immutable;
using TrackTrim.Application.Batches;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Planning;
using Xunit;

namespace TrackTrim.Application.Tests.Batches;

/// <summary>Checks authoritative file-local Keep selections separately from approval and execution.</summary>
public sealed class BatchSelectionTests
{
    /// <summary>Initializes all inspected IDs, changes only one selection, and preserves every other approval.</summary>
    [Fact]
    public async Task SelectionIsIndependentAndInvalidatesOnlyItsOwnApproval()
    {
        var harness = new BatchTestHarness();
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        Assert.Equal([0, 1, 2], harness.Session.Snapshot.Items[0].CurrentRetainedTrackIds);
        Assert.Equal([10, 11, 12], harness.Session.Snapshot.Items[1].CurrentRetainedTrackIds);
        Assert.All(harness.Session.Snapshot.Items, item => harness.Approve(item, noChange: true));
        await harness.Coordinator.ReviewAsync(TestContext.Current.CancellationToken);
        var before = harness.Session.Snapshot;
        var changes = 0;
        harness.Session.Changed += _ => changes++;

        var result = harness.Session.SetSelection(new(before.Items[0].Id, [1, 0]));
        var after = harness.Session.Snapshot;

        Assert.True(result.Applied);
        Assert.Equal(before.Revision + 1, after.Revision);
        Assert.Equal([0, 1], after.Items[0].CurrentRetainedTrackIds);
        Assert.Null(after.Items[0].ApprovedPlan);
        Assert.Equal(BatchItemState.NeedsReview, after.Items[0].State);
        Assert.Same(before.Items[1], after.Items[1]);
        Assert.Null(after.Review);
        Assert.Equal(1, changes);
        Assert.Empty(harness.RemuxedPaths);
        Assert.Equal([0, 1, 2], before.Items[0].CurrentRetainedTrackIds);
    }

    /// <summary>Treats a reordered equal Keep set as a no-op that preserves the exact snapshot and review.</summary>
    [Fact]
    public async Task EqualSelectionPreservesExactSnapshotApprovalsAndReview()
    {
        var harness = new BatchTestHarness();
        await harness.PrepareAsync();
        var before = harness.Session.Snapshot;
        var changes = 0;
        harness.Session.Changed += _ => changes++;

        var result = harness.Session.SetSelection(new(before.Items[0].Id, [1, 0]));

        Assert.True(result.Applied);
        Assert.Same(before, harness.Session.Snapshot);
        Assert.NotNull(before.Review);
        Assert.NotNull(before.Items[0].ApprovedPlan);
        Assert.Equal(0, changes);
    }

    /// <summary>Rejects absent, repeated, foreign and video-removing IDs without mutating approved state.</summary>
    [Theory]
    [InlineData("default")]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("foreign")]
    [InlineData("video")]
    public async Task InvalidSelectionLeavesExactSnapshotUnchanged(string invalidSelection)
    {
        var harness = new BatchTestHarness();
        await harness.PrepareAsync();
        var before = harness.Session.Snapshot;
        ImmutableArray<int> identifiers = invalidSelection switch
        {
            "default" => default,
            "empty" => [],
            "duplicate" => [0, 1, 1],
            "foreign" => [0, 11],
            _ => [1, 2]
        };

        var result = harness.Session.SetSelection(new(before.Items[0].Id, identifiers));

        Assert.False(result.Applied);
        Assert.Equal(ErrorCode.InvalidSelection, result.Failure!.Code);
        Assert.Same(before, harness.Session.Snapshot);
    }

    /// <summary>Keeps explicit manual zero-audio selection distinct from approval and resets it on reinspection.</summary>
    [Fact]
    public async Task ManualApprovalAdoptsIdsAndReinspectionRestoresAllIds()
    {
        var harness = new BatchTestHarness(1);
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        var item = harness.Session.Snapshot.Items[0];
        Assert.True(harness.Session.SetSelection(new(item.Id, [0])).Applied);
        Assert.Equal([0], harness.Session.Snapshot.Items[0].CurrentRetainedTrackIds);
        Assert.Null(harness.Session.Snapshot.Items[0].ApprovedPlan);
        var unconfirmed = PlanBuilder.Build(new(item.Inspection!, [0], "output.mkv", false));
        Assert.Equal(ErrorCode.ZeroAudioConfirmationRequired, unconfirmed.Failure!.Code);
        var approved = PlanBuilder.Build(new(item.Inspection!, [0, 1], "output.mkv", false)).Plan!;
        Assert.True(harness.Session.ApprovePlan(new(item.Id, approved)).Applied);
        Assert.Equal([0, 1], harness.Session.Snapshot.Items[0].CurrentRetainedTrackIds);
        Assert.True(harness.Session.InvalidatePlan(item.Id).Applied);
        Assert.Equal([0, 1], harness.Session.Snapshot.Items[0].CurrentRetainedTrackIds);

        await harness.Coordinator.InspectItemAsync(new(item.Id, TestContext.Current.CancellationToken));

        Assert.Equal([0, 1, 2], harness.Session.Snapshot.Items[0].CurrentRetainedTrackIds);
        Assert.Null(harness.Session.Snapshot.Items[0].ApprovedPlan);
        Assert.Empty(harness.RemuxedPaths);
    }
}
