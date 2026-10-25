using MovieSift.Application;
using MovieSift.Application.Batches;
using MovieSift.Application.Inspection;
using MovieSift.Application.Jobs;
using MovieSift.Application.Ports;
using MovieSift.Core.Failures;
using MovieSift.Core.Media;
using Xunit;

namespace MovieSift.Application.Tests.Batches;

/// <summary>Contains escaped port failures without inferring that an unknown worker or candidate is safe.</summary>
public sealed class BatchUnexpectedFailureTests
{
    private const string UnexpectedDiagnostic = "Unexpected fake-port lifecycle failure.";

    /// <summary>Stops both inspection entry points after one unexpected probe failure and preserves blocking evidence.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedInspectionFailureStopsFurtherWorkAndCannotBeErased(bool inspectSelected)
    {
        var harness = new BatchTestHarness();
        harness.OnInspect = _ => throw new InvalidOperationException(UnexpectedDiagnostic);
        var first = harness.Session.Snapshot.Items[0];
        var stopped = inspectSelected
            ? await harness.Coordinator.InspectItemAsync(new(first.Id, TestContext.Current.CancellationToken))
            : await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);

        AssertUnknownOwnership(stopped);
        Assert.Equal(BatchItemState.PendingInspection, stopped.Items[1].State);
        Assert.Null(stopped.Items[0].Result);
        Assert.Single(harness.InspectedPaths);
        Assert.Empty(harness.RemuxedPaths);
        Assert.Empty(harness.Reservations);
        Assert.Equal(0, harness.CleanupCalls);
        await AssertCannotRestartOrEraseEvidenceAsync(harness);
    }

    /// <summary>Stops after an escaped remux, verification, or cleanup failure without starting the next file.</summary>
    [Theory]
    [InlineData("remux")]
    [InlineData("verification")]
    [InlineData("cleanup")]
    public async Task UnexpectedJobFailureStopsDispatchWithoutAnotherCleanup(string boundary)
    {
        var harness = new BatchTestHarness();
        var review = await harness.PrepareAsync();

        switch (boundary)
        {
            case "remux":
                harness.OnRemux = _ => throw new InvalidOperationException(UnexpectedDiagnostic);
                break;
            case "verification":
                harness.OnVerify = _ => throw new InvalidOperationException(UnexpectedDiagnostic);
                break;
            case "cleanup":
                harness.OnCleanup = _ => throw new InvalidOperationException(UnexpectedDiagnostic);
                break;
        }

        var stopped = await harness.Coordinator.RunAsync(new(review.Id, CancellationToken: TestContext.Current.CancellationToken));

        AssertUnknownOwnership(stopped);
        Assert.Equal(BatchItemState.Ready, stopped.Items[1].State);
        Assert.Single(harness.RemuxedPaths);
        Assert.Single(harness.Reservations);
        Assert.Equal(1, harness.CleanupCalls);
        Assert.Equal(boundary == "cleanup" ? 1 : 0, harness.PublishedPaths.Count);
        await AssertCannotRestartOrEraseEvidenceAsync(harness);
    }

    /// <summary>Preserves the original warning result when explicit publication or discard escapes unexpectedly.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedWarningFailureNeverRetriesCandidateOrStartsSibling(bool publish)
    {
        var harness = new BatchTestHarness();
        var review = await harness.PrepareAsync();
        harness.ExitCodes[harness.Session.Snapshot.Items[0].SourcePath] = 1;
        var warned = await harness.Coordinator.RunAsync(new(review.Id, CancellationToken: TestContext.Current.CancellationToken));
        var first = warned.Items[0];
        Assert.Equal(BatchItemState.AwaitingWarningReview, first.State);

        if (publish)
        {
            harness.OnVerify = _ => throw new InvalidOperationException(UnexpectedDiagnostic);
        }

        if (!publish)
        {
            harness.OnCleanup = _ => throw new InvalidOperationException(UnexpectedDiagnostic);
        }

        var request = new BatchWarningRequest(first.Id, TestContext.Current.CancellationToken);
        var stopped = publish
            ? await harness.Coordinator.PublishWarningsAsync(request)
            : await harness.Coordinator.DiscardWarningsAsync(request);

        AssertUnknownOwnership(stopped);
        Assert.Same(first.Result, stopped.Items[0].Result);
        Assert.Equal(BatchItemState.Ready, stopped.Items[1].State);
        Assert.Single(harness.RemuxedPaths);
        Assert.Empty(harness.PublishedPaths);
        var cleanupCalls = publish ? 0 : 1;
        Assert.Equal(cleanupCalls, harness.CleanupCalls);
        await AssertCannotRestartOrEraseEvidenceAsync(harness);
        await harness.Coordinator.PublishWarningsAsync(request);
        await harness.Coordinator.DiscardWarningsAsync(request);
        Assert.Equal(cleanupCalls, harness.CleanupCalls);
        Assert.Single(harness.RemuxedPaths);
    }

    /// <summary>Restores preparation after an unexpected read-only review error and requires a fresh explicit review.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedReviewFailureReturnsDiagnosticWithoutClaimingWorkerOwnership(bool throwOnRead)
    {
        var harness = new BatchTestHarness();
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);

        foreach (var item in harness.Session.Snapshot.Items)
        {
            await harness.SelectAndReviewAsync(item);
        }

        var sourceAccess = new ReviewFaultSourceAccess(harness) { ThrowOnRead = throwOnRead };
        var coordinator = new BatchCoordinator(harness.Session, new InspectionService(harness, harness),
            new JobCoordinator(harness, harness, harness), sourceAccess);
        var rejected = await coordinator.ReviewSelectionsAsync(new(harness.Session.Snapshot.Revision, [], TestContext.Current.CancellationToken));
        var stopped = harness.Session.Snapshot;

        Assert.Null(rejected.Review);
        Assert.Equal(ErrorCode.IoFailure, rejected.Failure?.Code);
        Assert.Contains(UnexpectedDiagnostic, rejected.Failure!.Detail, StringComparison.Ordinal);
        Assert.Equal(BatchPhase.Preparing, stopped.Phase);
        Assert.Equal(BatchPauseReason.None, stopped.PauseReason);
        Assert.False(stopped.HasSafetyBlock);
        Assert.Null(stopped.Review);
        Assert.Equal(rejected.Failure, stopped.Failure);
        Assert.All(stopped.Items, item => Assert.Equal(BatchItemState.Ready, item.State));
        Assert.Empty(harness.RemuxedPaths);
        Assert.Empty(harness.Reservations);
        Assert.Equal(0, harness.CleanupCalls);

        sourceAccess.ShouldThrow = false;
        var corrected = await coordinator.ReviewSelectionsAsync(new(harness.Session.Snapshot.Revision, [], TestContext.Current.CancellationToken));
        Assert.NotNull(corrected.Review);
        Assert.Null(corrected.Failure);
        Assert.Null(harness.Session.Snapshot.Failure);
        Assert.Empty(harness.RemuxedPaths);
    }

    /// <summary>Keeps an earlier publication intact when a later active worker fails or is cancelled.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterActiveStopPreservesPublishedSiblingAndDoesNotDispatchThird(bool cancelActiveWorker)
    {
        var harness = new BatchTestHarness(3);
        var review = await harness.PrepareAsync();
        var items = harness.Session.Snapshot.Items;
        var firstDestination = items[0].ApprovedPlan!.DestinationPath;
        var secondEntered = BatchTestHarness.Signal();
        var failureRelease = BatchTestHarness.Signal();
        harness.OnRemux = async request =>
        {
            if (request.Plan.Inspection.Source.FullPath != items[1].SourcePath)
            {
                return;
            }

            secondEntered.TrySetResult();

            if (cancelActiveWorker)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, request.CancellationToken);
                return;
            }

            await failureRelease.Task;
            throw new OperationFailureException(new(ErrorCode.ToolFailed, "Expected later worker failure."));
        };
        var run = harness.Coordinator.RunAsync(new(review.Id, CancellationToken: TestContext.Current.CancellationToken));

        try
        {
            await BatchTestHarness.Await(secondEntered.Task);
            var firstPublished = Assert.IsType<JobResult>(harness.Session.Snapshot.Items[0].Result);
            Assert.Equal(JobPhase.Completed, firstPublished.Phase);
            Assert.Equal(firstDestination, firstPublished.PublishedPath);
            Assert.Equal(1, harness.CleanupCalls);
            Assert.Equal(BatchItemState.Running, harness.Session.Snapshot.Items[1].State);
            Assert.Equal(BatchItemState.Ready, harness.Session.Snapshot.Items[2].State);

            if (cancelActiveWorker)
            {
                harness.Coordinator.Cancel();
            }

            if (!cancelActiveWorker)
            {
                failureRelease.TrySetResult();
            }

            await BatchTestHarness.Await(run);
            var stopped = await run;
            Assert.Equal(BatchPhase.Paused, stopped.Phase);
            Assert.Equal(cancelActiveWorker ? BatchPauseReason.Cancelled : BatchPauseReason.ItemFailed, stopped.PauseReason);
            Assert.Equal(cancelActiveWorker ? BatchItemState.Cancelled : BatchItemState.Failed, stopped.Items[1].State);
            Assert.Equal(BatchItemState.Ready, stopped.Items[2].State);
            Assert.Same(firstPublished, stopped.Items[0].Result);
            Assert.Equal(firstDestination, Assert.Single(harness.PublishedPaths));
            Assert.Equal([items[0].SourcePath, items[1].SourcePath], harness.RemuxedPaths);
            Assert.Equal(2, harness.Reservations.Count);
            Assert.Equal(2, harness.CleanupCalls);

            Assert.Null(stopped.Review);
            Assert.Equal(ErrorCode.StalePlan, (await harness.Coordinator.RunAsync(new(review.Id))).Failure!.Code);
            var freshReview = await harness.Coordinator.ReviewSelectionsAsync(new(harness.Session.Snapshot.Revision, [], TestContext.Current.CancellationToken));
            Assert.NotNull(freshReview.Review);
            Assert.DoesNotContain(freshReview.Review.Items, item => item.ItemId == items[0].Id);
            Assert.NotEqual(items[1].ApprovedPlan!.Id, freshReview.Review.Items[0].Plan.Id);
            Assert.Same(firstPublished, harness.Session.Snapshot.Items[0].Result);
            Assert.Equal(2, harness.CleanupCalls);
            Assert.DoesNotContain(items[2].SourcePath, harness.RemuxedPaths);
        }
        finally
        {
            failureRelease.TrySetResult();
            harness.Coordinator.Cancel();
            await BatchTestHarness.Await(run);
        }
    }

    /// <summary>Rejects a changed later source before reserving or remuxing it while preserving the earlier result.</summary>
    [Fact]
    public async Task LaterSourceChangedAfterReviewPreservesFirstOutputAndStopsBeforeRemux()
    {
        var harness = new BatchTestHarness(3);
        var review = await harness.PrepareAsync();
        var items = harness.Session.Snapshot.Items;
        var firstDestination = items[0].ApprovedPlan!.DestinationPath;
        var secondRevision = harness.Revisions[items[1].SourcePath];
        harness.OnPublish = _ => harness.Revisions[items[1].SourcePath] = secondRevision with { Length = secondRevision.Length + 1 };

        var stopped = await harness.Coordinator.RunAsync(new(review.Id, CancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(BatchPhase.Paused, stopped.Phase);
        Assert.Equal(BatchPauseReason.ItemFailed, stopped.PauseReason);
        Assert.Equal(BatchItemState.Completed, stopped.Items[0].State);
        Assert.Equal(firstDestination, stopped.Items[0].Result!.PublishedPath);
        Assert.Equal(JobPhase.Completed, stopped.Items[0].Result!.Phase);
        Assert.Equal(BatchItemState.Failed, stopped.Items[1].State);
        Assert.Equal(ErrorCode.SourceChanged, stopped.Items[1].Result!.Failure?.Code);
        Assert.Equal(BatchItemState.Ready, stopped.Items[2].State);
        Assert.Equal(items[0].SourcePath, Assert.Single(harness.RemuxedPaths));
        Assert.Equal(firstDestination, Assert.Single(harness.PublishedPaths));
        Assert.Single(harness.Reservations);
        Assert.Equal(1, harness.CleanupCalls);
    }

    /// <summary>Checks the per-item diagnostic and explicit ownership pause even without a returned job result.</summary>
    private static void AssertUnknownOwnership(BatchSnapshot snapshot)
    {
        Assert.Equal(BatchPhase.Paused, snapshot.Phase);
        Assert.Equal(BatchPauseReason.UnsafeOwnership, snapshot.PauseReason);
        Assert.True(snapshot.HasSafetyBlock);
        Assert.Null(snapshot.Review);
        Assert.Equal(BatchItemState.Failed, snapshot.Items[0].State);
        Assert.Equal(ErrorCode.IoFailure, snapshot.Items[0].Failure?.Code);
        Assert.Contains(UnexpectedDiagnostic, snapshot.Items[0].Failure!.Detail, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), snapshot.Items[0].Failure!.Detail, StringComparison.Ordinal);
        Assert.Equal(snapshot.Items[0].Failure, snapshot.Failure);
    }

    /// <summary>Proves edits, reinspection and fresh execution cannot erase or bypass the retained uncertainty.</summary>
    private static async Task AssertCannotRestartOrEraseEvidenceAsync(BatchTestHarness harness)
    {
        var snapshot = harness.Session.Snapshot;
        var first = snapshot.Items[0];
        var inspectionCalls = harness.InspectedPaths.Count;
        var remuxCalls = harness.RemuxedPaths.Count;
        var cleanupCalls = harness.CleanupCalls;
        Assert.False(harness.Session.SetIncluded(first.Id, false).Applied);
        Assert.False(harness.Session.Remove(first.Id).Applied);
        Assert.False(harness.Session.InvalidatePlan(first.Id).Applied);
        Assert.False(harness.Session.AddSources([first.DiscoveredSource!]).Applied);
        Assert.Null((await harness.Coordinator.ReviewSelectionsAsync(new(harness.Session.Snapshot.Revision, [], TestContext.Current.CancellationToken))).Review);
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        await harness.Coordinator.InspectItemAsync(new(first.Id, TestContext.Current.CancellationToken));
        var rejectedRun = await harness.Coordinator.RunAsync(new(Guid.NewGuid(), CancellationToken: TestContext.Current.CancellationToken));
        Assert.True(rejectedRun.HasSafetyBlock);
        Assert.Same(snapshot, harness.Session.Snapshot);
        Assert.Equal(inspectionCalls, harness.InspectedPaths.Count);
        Assert.Equal(remuxCalls, harness.RemuxedPaths.Count);
        Assert.Equal(cleanupCalls, harness.CleanupCalls);
    }

    /// <summary>Injects an unexpected read-only review failure independently of worker and output ports.</summary>
    private sealed class ReviewFaultSourceAccess(ISourceAccess inner) : ISourceAccess
    {
        public bool ThrowOnRead { get; init; }
        public bool ShouldThrow { get; set; } = true;

        /// <summary>Fails before worker ownership when the requested review read is under test.</summary>
        public Task<SourceRevision> ReadRevisionAsync(SourceRequest request)
        {
            if (ShouldThrow && ThrowOnRead)
            {
                throw new InvalidOperationException(UnexpectedDiagnostic);
            }

            return inner.ReadRevisionAsync(request);
        }

        /// <summary>Implements the explicit preparation boundary without adding filesystem effects to this controlled port.</summary>
        public Task PrepareDestinationAsync(ValidateDestinationRequest request) => inner.PrepareDestinationAsync(request);

        /// <summary>Fails before worker ownership when the requested destination review is under test.</summary>
        public Task ValidateDestinationAsync(ValidateDestinationRequest request)
        {
            if (ShouldThrow && !ThrowOnRead)
            {
                throw new InvalidOperationException(UnexpectedDiagnostic);
            }

            return inner.ValidateDestinationAsync(request);
        }
    }
}
