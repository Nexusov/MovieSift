using MovieSift.Application.Batches;
using MovieSift.Core.Failures;
using MovieSift.Core.Media;
using Xunit;

namespace MovieSift.Application.Tests.Batches;

/// <summary>Checks local approval isolation, bounded additions and aggregate review invalidation.</summary>
public sealed class BatchSessionTests
{
    /// <summary>Deduplicates path aliases and file identities while retaining invalid rows for review.</summary>
    [Fact]
    public void DiscoveryRetainsIssuesAndDeduplicatesAcrossAdditions()
    {
        var harness = new BatchTestHarness(1);
        var original = Assert.Single(harness.Session.Snapshot.Items).DiscoveredSource!;
        var alias = original with { FullPath = Path.Combine(Path.GetTempPath(), "hardlink.mkv") };
        var missing = Path.Combine(Path.GetTempPath(), "missing.mkv");
        var result = harness.Session.AddDiscoveryResult(new([original, alias], [new(missing, new(ErrorCode.InvalidSource))]));

        Assert.True(result.Applied);
        Assert.Equal(2, harness.Session.Snapshot.Items.Length);
        var failed = Assert.Single(harness.Session.Snapshot.Items, item => item.State == BatchItemState.Failed);
        Assert.True(failed.Included);
        Assert.Equal(missing, failed.SourcePath);
        Assert.Equal(ErrorCode.InvalidSource, failed.Failure!.Code);
    }

    /// <summary>Rejects a discovery rejection and a later over-limit addition without accepting a subset.</summary>
    [Fact]
    public void AdditionLimitIsAtomicAcrossExistingRows()
    {
        var harness = new BatchTestHarness(1);
        var before = harness.Session.Snapshot;
        var rejected = harness.Session.AddDiscoveryResult(new([], [], new(ErrorCode.InvalidSource, Reason: ErrorReason.BatchEntryLimit)));
        Assert.False(rejected.Applied);
        Assert.Same(before, harness.Session.Snapshot);

        var additions = Enumerable.Range(1, BatchSession.MaximumItems).Select(index => new SourceRevision(
            Path.Combine(Path.GetTempPath(), $"batch-extra-{index}.mkv"), $"extra-{index}", 100, 200));
        var overflow = harness.Session.AddSources(additions);
        Assert.False(overflow.Applied);
        Assert.Equal(ErrorReason.BatchFileLimit, overflow.Failure!.Reason);
        Assert.Same(before, harness.Session.Snapshot);
    }

    /// <summary>Invalidates only the changed file while rejecting an aggregate review from older inputs.</summary>
    [Fact]
    public async Task ItemEditPreservesOtherApprovalAndInvalidatesBatchReview()
    {
        var harness = new BatchTestHarness();
        var review = await harness.PrepareAsync();
        var before = harness.Session.Snapshot;
        var first = before.Items[0];
        var second = before.Items[1];

        Assert.True(harness.Session.InvalidatePlan(first.Id).Applied);
        Assert.Null(harness.Session.Snapshot.Items[0].ApprovedPlan);
        Assert.Same(second.ApprovedPlan, harness.Session.Snapshot.Items[1].ApprovedPlan);
        Assert.Null(harness.Session.Snapshot.Review);
        Assert.NotNull(before.Items[0].ApprovedPlan);

        var stale = await harness.Coordinator.RunAsync(new(review.Id));
        Assert.Equal(ErrorCode.StalePlan, stale.Failure!.Code);
        Assert.Empty(harness.RemuxedPaths);
    }

    /// <summary>Excludes invalid discovery rows explicitly and keeps eligible sibling inspection independent.</summary>
    [Fact]
    public async Task IncludedDiscoveryFailureBlocksReviewUntilExcluded()
    {
        var harness = new BatchTestHarness(1);
        harness.Session.AddDiscoveryResult(new([], [new("missing", new(ErrorCode.InvalidSource))]));
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        harness.Approve(harness.Session.Snapshot.Items[0]);

        var rejected = await harness.Coordinator.ReviewAsync(TestContext.Current.CancellationToken);
        var failed = Assert.Single(rejected.ItemFailures);
        Assert.Equal(ErrorCode.InvalidSource, failed.Failure.Code);
        Assert.True(harness.Session.SetIncluded(failed.ItemId, false).Applied);
        var approved = await harness.Coordinator.ReviewAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(approved.Review);
        Assert.Single(approved.Review.Items);
    }

    /// <summary>Rejects a plan from another file despite otherwise valid numeric selections.</summary>
    [Fact]
    public async Task PlanCannotBeReusedForAnotherEpisode()
    {
        var harness = new BatchTestHarness();
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        var first = harness.Session.Snapshot.Items[0];
        var second = harness.Session.Snapshot.Items[1];
        var plan = harness.Approve(first);

        var result = harness.Session.ApprovePlan(new(second.Id, plan));
        Assert.False(result.Applied);
        Assert.Equal(ErrorCode.StalePlan, result.Failure!.Code);
        Assert.Null(harness.Session.Snapshot.Items[1].ApprovedPlan);
        Assert.NotEqual(first.Inspection!.Tracks[0].Id, second.Inspection!.Tracks[0].Id);
    }

    /// <summary>Rejects every duplicate destination and a destination aliasing even an excluded source.</summary>
    [Fact]
    public async Task ReviewRejectsDuplicateDestinationsAndAllSourceAliases()
    {
        var harness = new BatchTestHarness(3);
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        var items = harness.Session.Snapshot.Items;
        var destination = Path.Combine(Path.GetTempPath(), "duplicate output.mkv");
        harness.Approve(items[0], destination);
        harness.Approve(items[1], Path.Combine(Path.GetDirectoryName(destination)!, ".", Path.GetFileName(destination)));
        Assert.True(harness.Session.SetIncluded(items[2].Id, false).Applied);

        var duplicate = await harness.Coordinator.ReviewAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, duplicate.ItemFailures.Length);
        Assert.All(duplicate.ItemFailures, failure => Assert.Equal(ErrorCode.InvalidDestination, failure.Failure.Code));
        Assert.Empty(harness.RemuxedPaths);

        harness.Approve(harness.Session.Snapshot.Items[0], items[2].SourcePath);
        harness.Approve(harness.Session.Snapshot.Items[1]);
        var alias = await harness.Coordinator.ReviewAsync(TestContext.Current.CancellationToken);
        var failure = Assert.Single(alias.ItemFailures);
        Assert.Equal(items[0].Id, failure.ItemId);
        Assert.Equal(ErrorCode.InvalidDestination, failure.Failure.Code);
    }

    /// <summary>Checks source revisions and the destination boundary for each included plan before dispatch.</summary>
    [Fact]
    public async Task ReviewAggregatesIndependentSourceAndDestinationFailures()
    {
        var harness = new BatchTestHarness();
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        var items = harness.Session.Snapshot.Items;
        harness.Approve(items[0]);
        var secondPlan = harness.Approve(items[1]);
        harness.Revisions[items[0].SourcePath] = items[0].Inspection!.Source with { Length = 101 };
        harness.DestinationFailures[secondPlan.DestinationPath] = new(ErrorCode.DestinationExists);

        var review = await harness.Coordinator.ReviewAsync(TestContext.Current.CancellationToken);
        Assert.Null(review.Review);
        Assert.Equal([ErrorCode.SourceChanged, ErrorCode.DestinationExists], review.ItemFailures.Select(failure => failure.Failure.Code));
        Assert.Single(harness.DestinationValidations);
        Assert.Empty(harness.Reservations);
    }
}
