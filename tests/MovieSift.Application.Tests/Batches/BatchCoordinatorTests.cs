using MovieSift.Application.Batches;
using MovieSift.Application.Jobs;
using MovieSift.Core.Failures;
using Xunit;

namespace MovieSift.Application.Tests.Batches;

/// <summary>Proves sequential dispatch and pause boundaries with deterministic lifecycle gates.</summary>
public sealed class BatchCoordinatorTests
{
    /// <summary>Waits for each source inspection before allowing the next file's independent probe.</summary>
    [Fact]
    public async Task InspectionsAreSequentialAndRetainFileLocalTrackIdentities()
    {
        var harness = new BatchTestHarness();
        var entered = BatchTestHarness.Signal();
        var release = BatchTestHarness.Signal();
        harness.OnInspect = async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        var inspection = harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);

        try
        {
            await BatchTestHarness.Await(entered.Task);
            Assert.Single(harness.InspectedPaths);
            Assert.Equal(BatchItemState.PendingInspection, harness.Session.Snapshot.Items[1].State);
            Assert.False(harness.Session.SetIncluded(harness.Session.Snapshot.Items[1].Id, false).Applied);
        }
        finally
        {
            release.TrySetResult();
        }

        await BatchTestHarness.Await(inspection);
        Assert.Equal(1, harness.MaximumActiveInspections);
        Assert.Equal(2, harness.InspectedPaths.Count);
        Assert.All(harness.Session.Snapshot.Items, item => Assert.Equal(BatchItemState.NeedsReview, item.State));
        Assert.NotEqual(harness.Session.Snapshot.Items[0].Inspection!.Tracks[1].Id, harness.Session.Snapshot.Items[1].Inspection!.Tracks[1].Id);
    }

    /// <summary>Never overlaps jobs and rejects a second dispatcher while the first worker is gated.</summary>
    [Fact]
    public async Task JobsAreSequentialAndConcurrentRunIsRejected()
    {
        var harness = new BatchTestHarness();
        var review = await harness.PrepareAsync();
        var entered = BatchTestHarness.Signal();
        var release = BatchTestHarness.Signal();
        harness.OnRemux = async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        var run = harness.Coordinator.RunAsync(new(review.Id));

        try
        {
            await BatchTestHarness.Await(entered.Task);
            Assert.Single(harness.RemuxedPaths);
            Assert.Equal(BatchItemState.Ready, harness.Session.Snapshot.Items[1].State);
            var concurrent = await harness.Coordinator.RunAsync(new(review.Id));
            Assert.Equal(ErrorCode.Busy, concurrent.Failure!.Code);
            Assert.False(harness.Session.Remove(harness.Session.Snapshot.Items[1].Id).Applied);
        }
        finally
        {
            release.TrySetResult();
        }

        var completed = await run.WaitAsync(TimeSpan.FromSeconds(BatchTestHarness.TestTimeoutSeconds), TestContext.Current.CancellationToken);
        Assert.Equal(BatchPhase.Completed, completed.Phase);
        Assert.Equal(1, harness.MaximumActiveRemuxes);
        Assert.Equal(2, harness.PublishedPaths.Count);
        Assert.All(completed.Items, item => Assert.Equal(BatchItemState.Completed, item.State));
    }

    /// <summary>Finishes the active job on pause and requires a new explicit review before remaining work.</summary>
    [Fact]
    public async Task PauseFinishesActiveJobAndResumeDoesNotRepeatCompletedItem()
    {
        var harness = new BatchTestHarness();
        var review = await harness.PrepareAsync();
        var entered = BatchTestHarness.Signal();
        var release = BatchTestHarness.Signal();
        harness.OnRemux = async _ => { entered.TrySetResult(); await release.Task; };
        var run = harness.Coordinator.RunAsync(new(review.Id));
        await BatchTestHarness.Await(entered.Task);
        harness.Coordinator.Pause();
        release.TrySetResult();
        var paused = await run;

        Assert.Equal(BatchPhase.Paused, paused.Phase);
        Assert.Equal(BatchPauseReason.Requested, paused.PauseReason);
        Assert.Equal(BatchItemState.Completed, paused.Items[0].State);
        Assert.Equal(BatchItemState.Ready, paused.Items[1].State);
        Assert.Single(harness.RemuxedPaths);
        var resume = await harness.Coordinator.ReviewSelectionsAsync(new(harness.Session.Snapshot.Revision, [], TestContext.Current.CancellationToken));
        Assert.Single(resume.Review!.Items);
        Assert.Equal(BatchPauseReason.None, harness.Session.Snapshot.PauseReason);
        var completed = await harness.Coordinator.RunAsync(new(resume.Review.Id));
        Assert.Equal(BatchPhase.Completed, completed.Phase);
        Assert.Equal(2, harness.RemuxedPaths.Count);
        Assert.Equal(2, harness.RemuxedPaths.Distinct().Count());
    }

    /// <summary>Awaits cancellation cleanup and preserves untouched pending work until explicit exclusion.</summary>
    [Fact]
    public async Task CancelAwaitsCleanupAndDoesNotStartNextItem()
    {
        var harness = new BatchTestHarness();
        var review = await harness.PrepareAsync();
        var remuxEntered = BatchTestHarness.Signal();
        var cleanupEntered = BatchTestHarness.Signal();
        var cleanupRelease = BatchTestHarness.Signal();
        harness.OnRemux = async request =>
        {
            remuxEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, request.CancellationToken);
        };
        harness.OnCleanup = async _ => { cleanupEntered.TrySetResult(); await cleanupRelease.Task; };
        var run = harness.Coordinator.RunAsync(new(review.Id));
        await BatchTestHarness.Await(remuxEntered.Task);
        harness.Coordinator.Cancel();

        try
        {
            await BatchTestHarness.Await(cleanupEntered.Task);
            Assert.False(run.IsCompleted);
            Assert.Single(harness.RemuxedPaths);
            Assert.Equal(BatchItemState.Ready, harness.Session.Snapshot.Items[1].State);
        }
        finally
        {
            cleanupRelease.TrySetResult();
        }

        var paused = await run;
        Assert.Equal(BatchItemState.Cancelled, paused.Items[0].State);
        Assert.Empty(harness.PublishedPaths);
        Assert.Null(paused.Review);
        var obsolete = await harness.Coordinator.RunAsync(new(review.Id));
        Assert.Equal(ErrorCode.StalePlan, obsolete.Failure!.Code);
        Assert.Single(harness.RemuxedPaths);
        Assert.True(harness.Session.SetIncluded(paused.Items[0].Id, false).Applied);
        harness.OnRemux = null;
        var resume = await harness.Coordinator.ReviewSelectionsAsync(new(harness.Session.Snapshot.Revision, [], TestContext.Current.CancellationToken));
        var completed = await harness.Coordinator.RunAsync(new(resume.Review!.Id));
        Assert.Equal(BatchPhase.Completed, completed.Phase);
        Assert.Equal(BatchItemState.Cancelled, completed.Items[0].State);
        Assert.Equal(BatchItemState.Completed, completed.Items[1].State);
    }

    /// <summary>Consumes a pre-cancelled run without starting jobs or relabelling pending rows as cancelled.</summary>
    [Fact]
    public async Task CancelBeforeDispatchPreservesAllPendingStates()
    {
        var harness = new BatchTestHarness();
        var review = await harness.PrepareAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var paused = await harness.Coordinator.RunAsync(new(review.Id, CancellationToken: cancellation.Token));

        Assert.Equal(BatchPauseReason.Cancelled, paused.PauseReason);
        Assert.All(paused.Items, item => Assert.Equal(BatchItemState.Ready, item.State));
        Assert.Empty(harness.RemuxedPaths);
        Assert.Equal(ErrorCode.StalePlan, (await harness.Coordinator.RunAsync(new(review.Id))).Failure!.Code);
    }

    /// <summary>Publishes the warning owner's candidate without remuxing or auto-starting the next item.</summary>
    [Fact]
    public async Task WarningApprovalRemainsPausedAndNeverRemuxesAgain()
    {
        var harness = new BatchTestHarness();
        var review = await harness.PrepareAsync();
        var first = harness.Session.Snapshot.Items[0];
        harness.ExitCodes[first.SourcePath] = 1;
        var warning = await harness.Coordinator.RunAsync(new(review.Id));
        Assert.Equal(BatchPauseReason.WarningReview, warning.PauseReason);
        Assert.Equal(BatchItemState.AwaitingWarningReview, warning.Items[0].State);
        Assert.Empty(harness.PublishedPaths);

        var wrongOwner = await harness.Coordinator.PublishWarningsAsync(new(warning.Items[1].Id));
        Assert.Equal(ErrorCode.StalePlan, wrongOwner.Failure!.Code);
        var published = await harness.Coordinator.PublishWarningsAsync(new(first.Id));
        Assert.Equal(BatchPhase.Paused, published.Phase);
        Assert.Equal(BatchItemState.Completed, published.Items[0].State);
        Assert.Equal(BatchItemState.Ready, published.Items[1].State);
        Assert.Single(harness.RemuxedPaths);
        Assert.Equal(2, harness.VerifyCalls);

        var resume = await harness.Coordinator.ReviewSelectionsAsync(new(harness.Session.Snapshot.Revision, [], TestContext.Current.CancellationToken));
        var completed = await harness.Coordinator.RunAsync(new(resume.Review!.Id));
        Assert.Equal(BatchPhase.Completed, completed.Phase);
        Assert.Equal(2, harness.RemuxedPaths.Count);
    }

    /// <summary>Retains warning ownership when approval's gate wait is cancelled and permits fresh discard.</summary>
    [Fact]
    public async Task PreCancelledWarningApprovalDoesNotLoseCandidate()
    {
        var harness = new BatchTestHarness();
        var review = await harness.PrepareAsync();
        var first = harness.Session.Snapshot.Items[0];
        harness.ExitCodes[first.SourcePath] = 1;
        await harness.Coordinator.RunAsync(new(review.Id));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var retained = await harness.Coordinator.PublishWarningsAsync(new(first.Id, cancellation.Token));
        Assert.Equal(BatchItemState.AwaitingWarningReview, retained.Items[0].State);
        Assert.Equal(0, harness.CleanupCalls);
        var discarded = await harness.Coordinator.DiscardWarningsAsync(new(first.Id));
        Assert.Equal(BatchPhase.Paused, discarded.Phase);
        Assert.Equal(BatchItemState.Cancelled, discarded.Items[0].State);
        Assert.Equal(1, harness.CleanupCalls);
        Assert.Single(harness.RemuxedPaths);
        Assert.Empty(harness.PublishedPaths);
        Assert.Null(discarded.Review);
        Assert.Equal(ErrorCode.StalePlan, (await harness.Coordinator.RunAsync(new(review.Id))).Failure!.Code);
        var freshReview = await harness.Coordinator.ReviewSelectionsAsync(new(harness.Session.Snapshot.Revision, [], TestContext.Current.CancellationToken));
        Assert.NotNull(freshReview.Review);
        Assert.NotEqual(first.ApprovedPlan!.Id, freshReview.Review.Items[0].Plan.Id);
        Assert.Single(harness.RemuxedPaths);
    }

    /// <summary>Stops on an item failure and requires explicit correction with a newly approved plan.</summary>
    [Fact]
    public async Task FailedItemRequiresCorrectionBeforeResume()
    {
        var harness = new BatchTestHarness();
        var review = await harness.PrepareAsync();
        var first = harness.Session.Snapshot.Items[0];
        harness.ExitCodes[first.SourcePath] = 2;
        var failed = await harness.Coordinator.RunAsync(new(review.Id));

        Assert.Equal(BatchPauseReason.ItemFailed, failed.PauseReason);
        Assert.Equal(BatchItemState.Failed, failed.Items[0].State);
        Assert.Single(harness.RemuxedPaths);
        var staleRun = await harness.Coordinator.RunAsync(new(review.Id));
        Assert.Equal(ErrorCode.StalePlan, staleRun.Failure!.Code);
        Assert.Single(harness.RemuxedPaths);
        harness.ExitCodes[first.SourcePath] = 0;
        await harness.SelectAndReviewAsync(failed.Items[0]);
        var corrected = await harness.Coordinator.ReviewSelectionsAsync(new(harness.Session.Snapshot.Revision, [], TestContext.Current.CancellationToken));
        var completed = await harness.Coordinator.RunAsync(new(corrected.Review!.Id));
        Assert.Equal(BatchPhase.Completed, completed.Phase);
        Assert.Equal(3, harness.RemuxedPaths.Count);
    }

    /// <summary>Keeps a published output visible while cleanup failure permanently prevents unsafe continuation.</summary>
    [Fact]
    public async Task PublishedCleanupFailureCannotBeErasedToResume()
    {
        var harness = new BatchTestHarness();
        var review = await harness.PrepareAsync();
        harness.CleanupFails = true;
        var stopped = await harness.Coordinator.RunAsync(new(review.Id));
        var first = stopped.Items[0];

        Assert.Equal(BatchPauseReason.CleanupRequired, stopped.PauseReason);
        Assert.True(stopped.HasSafetyBlock);
        Assert.Equal(BatchItemState.Completed, first.State);
        Assert.NotNull(first.Result!.PublishedPath);
        Assert.False(harness.Session.Remove(first.Id).Applied);
        Assert.False(harness.Session.SetIncluded(first.Id, false).Applied);
        Assert.False(harness.Session.InvalidatePlan(first.Id).Applied);
        Assert.Null((await harness.Coordinator.ReviewSelectionsAsync(new(harness.Session.Snapshot.Revision, [], TestContext.Current.CancellationToken))).Review);
        Assert.Single(harness.RemuxedPaths);
    }

    /// <summary>Blocks unsafe continuation after uncertain worker termination or publication.</summary>
    [Theory]
    [InlineData(ErrorCode.ProcessTerminationFailed)]
    [InlineData(ErrorCode.PublicationUncertain)]
    public async Task UnresolvedOwnershipCannotBeBypassed(ErrorCode error)
    {
        var harness = new BatchTestHarness();
        var review = await harness.PrepareAsync();

        if (error == ErrorCode.ProcessTerminationFailed)
        {
            harness.RemuxFailure = new(error);
        }

        if (error == ErrorCode.PublicationUncertain)
        {
            harness.PublicationFailure = new(error);
        }

        var stopped = await harness.Coordinator.RunAsync(new(review.Id));
        Assert.Equal(BatchPauseReason.UnsafeOwnership, stopped.PauseReason);
        Assert.True(stopped.HasSafetyBlock);
        Assert.False(harness.Session.SetIncluded(stopped.Items[0].Id, false).Applied);
        Assert.Null((await harness.Coordinator.ReviewSelectionsAsync(new(harness.Session.Snapshot.Revision, [], TestContext.Current.CancellationToken))).Review);
        Assert.Single(harness.RemuxedPaths);
    }

    /// <summary>Retains completed publication after a late cancel and prevents dispatch of the next item.</summary>
    [Fact]
    public async Task LateCancellationPreservesPublishedResult()
    {
        var harness = new BatchTestHarness();
        var review = await harness.PrepareAsync();
        harness.OnPublish = _ => harness.Coordinator.Cancel();
        var stopped = await harness.Coordinator.RunAsync(new(review.Id));

        Assert.Equal(BatchPauseReason.Cancelled, stopped.PauseReason);
        Assert.Equal(BatchItemState.Completed, stopped.Items[0].State);
        Assert.True(stopped.Items[0].Result!.CancellationRequested);
        Assert.NotNull(stopped.Items[0].Result!.PublishedPath);
        Assert.Single(harness.PublishedPaths);
        Assert.Single(harness.RemuxedPaths);
        Assert.Equal(BatchItemState.Ready, stopped.Items[1].State);
    }

    /// <summary>Lets the existing no-change lifecycle skip media writes and continue to the changed sibling.</summary>
    [Fact]
    public async Task NoChangeItemDoesNotRemuxOrReserveOutput()
    {
        var harness = new BatchTestHarness();
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        await harness.SelectAndReviewAsync(harness.Session.Snapshot.Items[0], noChange: true);
        await harness.SelectAndReviewAsync(harness.Session.Snapshot.Items[1]);
        var review = await harness.Coordinator.ReviewSelectionsAsync(new(harness.Session.Snapshot.Revision, [], TestContext.Current.CancellationToken));
        var completed = await harness.Coordinator.RunAsync(new(review.Review!.Id));

        Assert.Equal(BatchItemState.NoChange, completed.Items[0].State);
        Assert.Equal(JobPhase.NoChange, completed.Items[0].Result!.Phase);
        Assert.Null(completed.Items[0].Result!.PublishedPath);
        Assert.Single(harness.RemuxedPaths);
        Assert.Single(harness.Reservations);
        Assert.Single(harness.PublishedPaths);
    }

    /// <summary>Cancels warning re-verification through the same active job owner and awaits its cleanup.</summary>
    [Fact]
    public async Task CancelDuringWarningApprovalStopsVerificationAndKeepsQueuePaused()
    {
        var harness = new BatchTestHarness();
        var review = await harness.PrepareAsync();
        var first = harness.Session.Snapshot.Items[0];
        harness.ExitCodes[first.SourcePath] = 1;
        await harness.Coordinator.RunAsync(new(review.Id));
        var entered = BatchTestHarness.Signal();
        harness.OnVerify = async request =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, request.CancellationToken);
        };

        var approval = harness.Coordinator.PublishWarningsAsync(new(first.Id));
        await BatchTestHarness.Await(entered.Task);
        harness.Coordinator.Cancel();
        var cancelled = await approval.WaitAsync(TimeSpan.FromSeconds(BatchTestHarness.TestTimeoutSeconds), TestContext.Current.CancellationToken);

        Assert.Equal(BatchPhase.Paused, cancelled.Phase);
        Assert.Equal(BatchItemState.Cancelled, cancelled.Items[0].State);
        Assert.Equal(BatchItemState.Ready, cancelled.Items[1].State);
        Assert.Single(harness.RemuxedPaths);
        Assert.Empty(harness.PublishedPaths);
        Assert.Equal(1, harness.CleanupCalls);
    }

    /// <summary>Rechecks a sibling destination inside its job even after a successful aggregate review.</summary>
    [Fact]
    public async Task DestinationAppearingAfterReviewStopsBeforeSecondRemux()
    {
        var harness = new BatchTestHarness();
        var review = await harness.PrepareAsync();
        var destination = harness.Session.Snapshot.Items[1].ApprovedPlan!.DestinationPath;
        harness.OnPublish = _ => harness.DestinationFailures[destination] = new(ErrorCode.DestinationExists);

        var stopped = await harness.Coordinator.RunAsync(new(review.Id));

        Assert.Equal(BatchPauseReason.ItemFailed, stopped.PauseReason);
        Assert.Equal(BatchItemState.Completed, stopped.Items[0].State);
        Assert.Equal(BatchItemState.Failed, stopped.Items[1].State);
        Assert.Equal(ErrorCode.DestinationExists, stopped.Items[1].Result!.Failure!.Code);
        Assert.Single(harness.RemuxedPaths);
        Assert.Single(harness.Reservations);
        Assert.Single(harness.PublishedPaths);
    }

    /// <summary>Retains uncertain probe ownership even when inspection never produced a job result.</summary>
    [Fact]
    public async Task InspectionTerminationFailureBlocksExclusionRemovalAndRun()
    {
        var harness = new BatchTestHarness();
        harness.OnInspect = _ => throw new OperationFailureException(new(ErrorCode.ProcessTerminationFailed));
        var inspected = await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        var first = inspected.Items[0];

        Assert.Null(first.Result);
        Assert.Equal(ErrorCode.ProcessTerminationFailed, first.Failure!.Code);
        Assert.True(inspected.HasSafetyBlock);
        Assert.Equal(BatchPauseReason.UnsafeOwnership, inspected.PauseReason);
        Assert.Single(harness.InspectedPaths);
        Assert.False(harness.Session.SetIncluded(first.Id, false).Applied);
        Assert.False(harness.Session.Remove(first.Id).Applied);
        Assert.Null((await harness.Coordinator.ReviewSelectionsAsync(new(harness.Session.Snapshot.Revision, [], TestContext.Current.CancellationToken))).Review);
        Assert.Empty(harness.RemuxedPaths);
    }
}
