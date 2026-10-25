using System.Collections.Immutable;
using MovieSift.Application;
using MovieSift.Application.Batches;
using MovieSift.Application.Inspection;
using MovieSift.Application.Jobs;
using MovieSift.Application.Ports;
using MovieSift.Core.Failures;
using MovieSift.Core.Media;
using MovieSift.Core.Planning;
using Xunit;

namespace MovieSift.Application.Tests.Batches;

/// <summary>Checks safe in-memory reset without discarding active or unresolved job ownership.</summary>
public sealed class BatchResetTests
{
    /// <summary>Allows the same source again with fresh identities while rejecting old approvals, reviews and callbacks.</summary>
    [Fact]
    public async Task CompletedBatchResetsAndSameSourceRequiresFreshApprovalAndReview()
    {
        var harness = new BatchTestHarness(1);
        var review = await harness.PrepareAsync();
        var originalItem = harness.Session.Snapshot.Items[0];
        var originalPlan = Assert.IsType<RemuxPlan>(originalItem.ApprovedPlan);
        var progress = new CollectedProgress();
        IProgress<double>? oldProgress = null;
        harness.OnRemux = request =>
        {
            oldProgress = request.Progress;
            return Task.CompletedTask;
        };
        var completed = await harness.Coordinator.RunAsync(new(review.Id, progress, TestContext.Current.CancellationToken));
        var originalResult = completed.Items[0].Result;
        var changes = 0;
        harness.Session.Changed += _ => changes++;

        var reset = harness.Coordinator.StartNewBatch(completed.Revision);
        Assert.True(reset.Applied);
        Assert.Null(reset.Failure);
        AssertEmptyReset(harness.Session.Snapshot, completed.Revision + 1);
        Assert.Equal(1, changes);
        Assert.Same(originalResult, completed.Items[0].Result);
        Assert.NotNull(originalResult!.PublishedPath);
        Assert.Single(harness.RemuxedPaths);
        Assert.Single(harness.PublishedPaths);
        Assert.Equal(1, harness.CleanupCalls);
        Assert.True(harness.Session.AddSources([originalItem.DiscoveredSource!]).Applied);
        var pending = harness.Session.Snapshot;
        var newItem = Assert.Single(pending.Items);
        Assert.NotEqual(originalItem.Id, newItem.Id);
        Assert.Null(newItem.Inspection);
        Assert.Null(newItem.ApprovedPlan);
        Assert.Null(newItem.Result);
        Assert.Equal(BatchItemState.PendingInspection, newItem.State);
        Assert.False(harness.Session.SetSelection(new(newItem.Id, originalPlan.RetainedTracks.Select(track => track.Id).ToImmutableArray())).Applied);
        Assert.False(harness.Session.SetSelection(new(originalItem.Id, originalPlan.RetainedTracks.Select(track => track.Id).ToImmutableArray())).Applied);
        Assert.False(harness.Session.InvalidatePlan(originalItem.Id).Applied);
        Assert.False(harness.Session.SetIncluded(originalItem.Id, false).Applied);
        Assert.Same(pending, harness.Session.Snapshot);
        var staleRun = await harness.Coordinator.RunAsync(new(review.Id, CancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCode.StalePlan, staleRun.Failure!.Code);
        Assert.Same(pending, harness.Session.Snapshot);
        Assert.Single(harness.RemuxedPaths);

        oldProgress!.Report(99);
        var staleProgress = progress.Values[^1];
        Assert.Equal(originalItem.Id, staleProgress.ItemId);
        Assert.Equal(originalPlan.Id, staleProgress.PlanId);
        Assert.Same(pending, harness.Session.Snapshot);
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        var inspected = harness.Session.Snapshot.Items[0];
        Assert.Equal(BatchItemState.NeedsReview, inspected.State);
        Assert.Null(inspected.ApprovedPlan);
        var nextDestination = Path.Combine(Path.GetTempPath(), "MovieSift batch second result.mkv");
        var nextPlan = await harness.SelectAndReviewAsync(inspected, nextDestination);
        Assert.NotEqual(originalPlan.Id, nextPlan.Id);
        var nextReview = (await harness.Coordinator.ReviewSelectionsAsync(new(harness.Session.Snapshot.Revision, [], TestContext.Current.CancellationToken))).Review!;
        Assert.NotEqual(review.Id, nextReview.Id);
        Assert.Equal(newItem.Id, Assert.Single(nextReview.Items).ItemId);
        var repeated = await harness.Coordinator.RunAsync(new(nextReview.Id, progress, TestContext.Current.CancellationToken));
        Assert.Equal(BatchPhase.Completed, repeated.Phase);
        Assert.Equal(nextDestination, repeated.Items[0].Result!.PublishedPath);
        Assert.Equal(2, harness.RemuxedPaths.Count);
        Assert.Equal(2, harness.PublishedPaths.Count);
        Assert.Equal(2, harness.CleanupCalls);
        Assert.Equal(nextPlan.Id, progress.Values[^1].PlanId);
        Assert.Equal(newItem.Id, progress.Values[^1].ItemId);
        Assert.Same(originalResult, completed.Items[0].Result);
    }

    /// <summary>Reclaims the entire bounded capacity and generates new row identities without performing I/O.</summary>
    [Fact]
    public void ResetReclaimsAllOneHundredSlots()
    {
        var harness = new BatchTestHarness(BatchSession.MaximumItems);
        var original = harness.Session.Snapshot;
        var identities = original.Items.Select(item => item.Id).ToHashSet();
        var sources = original.Items.Select(item => item.DiscoveredSource!).ToArray();

        Assert.True(harness.Coordinator.StartNewBatch(original.Revision).Applied);
        AssertEmptyReset(harness.Session.Snapshot, original.Revision + 1);
        Assert.True(harness.Session.AddSources(sources).Applied);
        var full = harness.Session.Snapshot;
        Assert.Equal(BatchSession.MaximumItems, full.Items.Length);
        Assert.All(full.Items, item => Assert.DoesNotContain(item.Id, identities));
        var overflow = harness.Session.AddSources([new SourceRevision(
            Path.Combine(Path.GetTempPath(), "batch overflow.mkv"), "overflow-identity", 100, 200)]);
        Assert.False(overflow.Applied);
        Assert.Equal(ErrorReason.BatchFileLimit, overflow.Failure!.Reason);
        Assert.Same(full, harness.Session.Snapshot);
        Assert.Empty(harness.InspectedPaths);
        Assert.Empty(harness.RemuxedPaths);
        Assert.Empty(harness.DestinationValidations);
        Assert.Empty(harness.Reservations);
        Assert.Equal(0, harness.CleanupCalls);
    }

    /// <summary>Clears a safe constructor rejection and advances revision even when starting from an empty batch.</summary>
    [Fact]
    public void EmptyResetClearsSafeErrorAndKeepsRevisionMonotonic()
    {
        var harness = new BatchTestHarness(BatchSession.MaximumItems + 1);
        var rejected = harness.Session.Snapshot;
        Assert.Empty(rejected.Items);
        Assert.Equal(ErrorReason.BatchFileLimit, rejected.Failure!.Reason);
        Assert.True(harness.Coordinator.StartNewBatch(rejected.Revision).Applied);
        AssertEmptyReset(harness.Session.Snapshot, rejected.Revision + 1);
        Assert.True(harness.Coordinator.StartNewBatch().Applied);
        AssertEmptyReset(harness.Session.Snapshot, rejected.Revision + 2);
        Assert.Empty(harness.InspectedPaths);
        Assert.Empty(harness.RemuxedPaths);
    }

    /// <summary>Rejects stale confirmation after an intervening edit without altering the edited queue.</summary>
    [Fact]
    public async Task StaleResetConsentPreservesTheExactCurrentSnapshot()
    {
        var harness = new BatchTestHarness();
        await harness.PrepareAsync();
        var consentRevision = harness.Session.Snapshot.Revision;
        Assert.True(harness.Session.InvalidatePlan(harness.Session.Snapshot.Items[0].Id).Applied);
        var edited = harness.Session.Snapshot;
        var changes = 0;
        harness.Session.Changed += _ => changes++;

        var reset = harness.Coordinator.StartNewBatch(consentRevision);

        Assert.False(reset.Applied);
        Assert.Equal(ErrorCode.StalePlan, reset.Failure!.Code);
        Assert.Same(edited, harness.Session.Snapshot);
        Assert.Equal(0, changes);
        Assert.Empty(harness.RemuxedPaths);
        Assert.True(harness.Coordinator.StartNewBatch(edited.Revision).Applied);
        AssertEmptyReset(harness.Session.Snapshot, edited.Revision + 1);
        Assert.Equal(1, changes);
    }

    /// <summary>Clears ordinary failed or cancelled outcomes only after their existing job cleanup has completed.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SafelyStoppedBatchCanBeExplicitlyReset(bool cancel)
    {
        var harness = new BatchTestHarness(1);
        var review = await harness.PrepareAsync();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        harness.ExitCodes[harness.Session.Snapshot.Items[0].SourcePath] = 2;

        if (cancel)
        {
            harness.OnRemux = _ => { cancellation.Cancel(); return Task.CompletedTask; };
        }

        var stopped = await harness.Coordinator.RunAsync(new(review.Id, CancellationToken: cancellation.Token));
        Assert.Equal(cancel ? BatchItemState.Cancelled : BatchItemState.Failed, stopped.Items[0].State);
        Assert.False(stopped.HasSafetyBlock);
        Assert.Equal(1, harness.CleanupCalls);
        var reset = harness.Coordinator.StartNewBatch(stopped.Revision);
        Assert.True(reset.Applied);
        AssertEmptyReset(harness.Session.Snapshot, stopped.Revision + 1);
        Assert.NotNull(stopped.Items[0].Result);
        Assert.Equal(1, harness.CleanupCalls);
    }

    /// <summary>Rejects reset throughout each active operation boundary and leaves the identical evidence snapshot intact.</summary>
    [Theory]
    [InlineData("inspection")]
    [InlineData("review")]
    [InlineData("remux")]
    [InlineData("verification")]
    [InlineData("cleanup")]
    public async Task ActiveOperationRejectsResetWithoutChangingState(string boundary)
    {
        var harness = new BatchTestHarness(1);
        var entered = BatchTestHarness.Signal();
        var release = BatchTestHarness.Signal();
        var coordinator = harness.Coordinator;
        Task operation;

        if (boundary == "inspection")
        {
            harness.OnInspect = async request =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(request.CancellationToken);
            };
            operation = coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        }
        else
        {
            var review = await harness.PrepareAsync();

            switch (boundary)
            {
                case "review":
                    var sourceAccess = new GatedReviewSourceAccess(harness, entered, release);
                    coordinator = new(harness.Session, new InspectionService(harness, harness),
                        new JobCoordinator(harness, harness, harness), sourceAccess);
                    break;
                case "remux":
                    harness.OnRemux = async request => { entered.TrySetResult(); await release.Task.WaitAsync(request.CancellationToken); };
                    break;
                case "verification":
                    harness.OnVerify = async request => { entered.TrySetResult(); await release.Task.WaitAsync(request.CancellationToken); };
                    break;
                case "cleanup":
                    harness.OnCleanup = async _ => { entered.TrySetResult(); await release.Task; };
                    break;
            }

            operation = boundary == "review"
                ? coordinator.ReviewSelectionsAsync(new(harness.Session.Snapshot.Revision, [], TestContext.Current.CancellationToken))
                : coordinator.RunAsync(new(review.Id, CancellationToken: TestContext.Current.CancellationToken));
        }

        try
        {
            await BatchTestHarness.Await(entered.Task);
            var active = harness.Session.Snapshot;
            var reset = coordinator.StartNewBatch(active.Revision);
            Assert.False(reset.Applied);
            Assert.Equal(ErrorCode.Busy, reset.Failure!.Code);
            Assert.Same(active, harness.Session.Snapshot);
        }
        finally
        {
            release.TrySetResult();
            await BatchTestHarness.Await(operation);
        }
    }

    /// <summary>Keeps the session's own busy guard effective even when a second dispatcher has a free gate.</summary>
    [Fact]
    public async Task SessionBusyGuardCannotBeBypassedByAnotherCoordinator()
    {
        var harness = new BatchTestHarness(1);
        var entered = BatchTestHarness.Signal();
        var release = BatchTestHarness.Signal();
        harness.OnInspect = async request => { entered.TrySetResult(); await release.Task.WaitAsync(request.CancellationToken); };
        var inspection = harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        var other = new BatchCoordinator(harness.Session, new InspectionService(harness, harness),
            new JobCoordinator(harness, harness, harness), harness);

        try
        {
            await BatchTestHarness.Await(entered.Task);
            var active = harness.Session.Snapshot;
            var reset = other.StartNewBatch(active.Revision);
            Assert.False(reset.Applied);
            Assert.Equal(ErrorCode.Busy, reset.Failure!.Code);
            Assert.Same(active, harness.Session.Snapshot);
        }
        finally
        {
            release.TrySetResult();
            await BatchTestHarness.Await(inspection);
        }
    }

    /// <summary>Holds the coordinator gate through terminal and reset notifications to reject recursive resets.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedNotificationCannotReenterReset(bool duringReset)
    {
        var harness = new BatchTestHarness(1);
        var review = await harness.PrepareAsync();
        BatchEditResult? nested = null;
        BatchSnapshot? observed = null;
        harness.Session.Changed += snapshot =>
        {
            var isObservedBoundary = duringReset
                ? snapshot.Phase == BatchPhase.Preparing && snapshot.Items.IsEmpty
                : snapshot.Phase == BatchPhase.Completed;

            if (!isObservedBoundary)
            {
                return;
            }

            observed = snapshot;
            nested = harness.Coordinator.StartNewBatch(snapshot.Revision);
            Assert.Same(snapshot, harness.Session.Snapshot);
        };
        await harness.Coordinator.RunAsync(new(review.Id, CancellationToken: TestContext.Current.CancellationToken));

        if (duringReset)
        {
            Assert.True(harness.Coordinator.StartNewBatch(harness.Session.Snapshot.Revision).Applied);
        }

        Assert.NotNull(nested);
        Assert.False(nested.Applied);
        Assert.Equal(ErrorCode.Busy, nested.Failure!.Code);
        Assert.Same(observed, harness.Session.Snapshot);
    }

    /// <summary>Preserves a pending warning candidate until explicit publish or discard has completed.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WarningReviewRejectsResetUntilCandidateIsResolved(bool publish)
    {
        var harness = new BatchTestHarness(1);
        var review = await harness.PrepareAsync();
        var first = harness.Session.Snapshot.Items[0];
        harness.ExitCodes[first.SourcePath] = 1;
        var warned = await harness.Coordinator.RunAsync(new(review.Id, CancellationToken: TestContext.Current.CancellationToken));
        var reset = harness.Coordinator.StartNewBatch(warned.Revision);
        Assert.False(reset.Applied);
        Assert.Equal(ErrorCode.Busy, reset.Failure!.Code);
        Assert.Same(warned, harness.Session.Snapshot);
        Assert.Equal(0, harness.CleanupCalls);
        Assert.Empty(harness.PublishedPaths);
        Assert.Single(harness.RemuxedPaths);
        var request = new BatchWarningRequest(first.Id, TestContext.Current.CancellationToken);
        var resolved = publish
            ? await harness.Coordinator.PublishWarningsAsync(request)
            : await harness.Coordinator.DiscardWarningsAsync(request);
        Assert.Equal(1, harness.CleanupCalls);
        Assert.True(harness.Coordinator.StartNewBatch(resolved.Revision).Applied);
        AssertEmptyReset(harness.Session.Snapshot, resolved.Revision + 1);
        Assert.Equal(1, harness.CleanupCalls);
        Assert.Single(harness.RemuxedPaths);
    }

    /// <summary>Refuses to erase uncertain ownership, publication, or cleanup while preserving the actual reason and evidence.</summary>
    [Theory]
    [InlineData("termination", ErrorCode.ProcessTerminationFailed)]
    [InlineData("publication", ErrorCode.PublicationUncertain)]
    [InlineData("cleanup", ErrorCode.OwnershipInvalid)]
    [InlineData("warning-cleanup", ErrorCode.OwnershipInvalid)]
    [InlineData("inspection", ErrorCode.ProcessTerminationFailed)]
    [InlineData("unexpected", ErrorCode.IoFailure)]
    public async Task UnsafeOutcomesRejectResetWithoutTouchingEvidence(string boundary, ErrorCode expectedCode)
    {
        var harness = new BatchTestHarness(1);

        if (boundary == "inspection")
        {
            harness.OnInspect = _ => throw new OperationFailureException(new(ErrorCode.ProcessTerminationFailed, "Owned probe is unresolved."));
            await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        }
        else
        {
            var review = await harness.PrepareAsync();

            switch (boundary)
            {
                case "termination":
                    harness.RemuxFailure = new(ErrorCode.ProcessTerminationFailed, "Owned worker is unresolved.");
                    break;
                case "publication":
                    harness.PublicationFailure = new(ErrorCode.PublicationUncertain, "Publication is unresolved.");
                    break;
                case "cleanup":
                case "warning-cleanup":
                    harness.CleanupFails = true;
                    break;
                case "unexpected":
                    harness.OnRemux = _ => throw new InvalidOperationException("Unexpected owner failure.");
                    break;
            }

            if (boundary == "warning-cleanup")
            {
                harness.ExitCodes[harness.Session.Snapshot.Items[0].SourcePath] = 1;
            }

            await harness.Coordinator.RunAsync(new(review.Id, CancellationToken: TestContext.Current.CancellationToken));

            if (boundary == "warning-cleanup")
            {
                await harness.Coordinator.DiscardWarningsAsync(new(harness.Session.Snapshot.Items[0].Id, TestContext.Current.CancellationToken));
            }
        }

        var unsafeSnapshot = harness.Session.Snapshot;
        var remuxCalls = harness.RemuxedPaths.Count;
        var cleanupCalls = harness.CleanupCalls;
        var publications = harness.PublishedPaths.ToArray();
        Assert.True(unsafeSnapshot.HasSafetyBlock);
        var rejected = harness.Coordinator.StartNewBatch(unsafeSnapshot.Revision);
        Assert.False(rejected.Applied);
        Assert.Equal(expectedCode, rejected.Failure!.Code);

        if (expectedCode != ErrorCode.OwnershipInvalid)
        {
            Assert.Same(unsafeSnapshot.Items[0].Failure ?? unsafeSnapshot.Failure, rejected.Failure);
        }

        Assert.Same(unsafeSnapshot, harness.Session.Snapshot);
        Assert.False(harness.Coordinator.StartNewBatch(unsafeSnapshot.Revision - 1).Applied);
        Assert.Same(unsafeSnapshot, harness.Session.Snapshot);
        Assert.Equal(remuxCalls, harness.RemuxedPaths.Count);
        Assert.Equal(cleanupCalls, harness.CleanupCalls);
        Assert.Equal(publications, harness.PublishedPaths);
    }

    /// <summary>Checks that a successful reset replaces every prior queue-specific field.</summary>
    private static void AssertEmptyReset(BatchSnapshot snapshot, long revision)
    {
        Assert.Equal(revision, snapshot.Revision);
        Assert.Equal(BatchPhase.Preparing, snapshot.Phase);
        Assert.Empty(snapshot.Items);
        Assert.Null(snapshot.Review);
        Assert.Null(snapshot.Failure);
        Assert.Equal(BatchPauseReason.None, snapshot.PauseReason);
        Assert.False(snapshot.PauseRequested);
        Assert.False(snapshot.HasSafetyBlock);
    }

    /// <summary>Collects original item and plan identities without scheduling mutable UI callbacks.</summary>
    private sealed class CollectedProgress : IProgress<BatchProgress>
    {
        public List<BatchProgress> Values { get; } = [];

        /// <summary>Records exactly the progress identity delivered by the existing job owner.</summary>
        public void Report(BatchProgress value) => Values.Add(value);
    }

    /// <summary>Exposes a deterministic in-flight read-only batch review boundary.</summary>
    private sealed class GatedReviewSourceAccess(
        ISourceAccess inner,
        TaskCompletionSource entered,
        TaskCompletionSource release) : ISourceAccess
    {
        /// <summary>Blocks review revision reads until the test explicitly releases them.</summary>
        public async Task<SourceRevision> ReadRevisionAsync(SourceRequest request)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(request.CancellationToken);
            return await inner.ReadRevisionAsync(request);
        }

        /// <summary>Implements the explicit preparation boundary without adding filesystem effects to this controlled port.</summary>
        public Task PrepareDestinationAsync(ValidateDestinationRequest request) => inner.PrepareDestinationAsync(request);

        /// <summary>Forwards destination validation without introducing another job lifecycle.</summary>
        public Task ValidateDestinationAsync(ValidateDestinationRequest request) => inner.ValidateDestinationAsync(request);
    }
}
