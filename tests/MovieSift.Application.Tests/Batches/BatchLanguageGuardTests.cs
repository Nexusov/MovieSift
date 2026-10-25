using MovieSift.Application;
using MovieSift.Application.Batches;
using MovieSift.Application.Inspection;
using MovieSift.Application.Jobs;
using MovieSift.Application.Ports;
using MovieSift.Core.Failures;
using MovieSift.Core.Media;
using Xunit;

namespace MovieSift.Application.Tests.Batches;

/// <summary>Checks rule-operation guards against the existing batch and single-job ownership boundaries.</summary>
public sealed class BatchLanguageGuardTests
{
    /// <summary>Rejects both rule operations at every active boundary without changing the owned snapshot.</summary>
    [Theory]
    [InlineData("inspection")]
    [InlineData("review")]
    [InlineData("remux")]
    [InlineData("verification")]
    [InlineData("cleanup")]
    public async Task ActiveOperationRejectsPreviewApplyAndManualSelection(string boundary)
    {
        var harness = new BatchTestHarness(1);
        var reviewed = await harness.PrepareAsync();
        var entered = BatchTestHarness.Signal();
        var release = BatchTestHarness.Signal();
        var coordinator = boundary == "review"
            ? new BatchCoordinator(harness.Session, new InspectionService(harness, harness), new JobCoordinator(harness, harness, harness),
                new GatedReviewSourceAccess(harness, entered, release)) : harness.Coordinator;
        var preview = coordinator.PreviewLanguageRules(BatchLanguageTestData.Unchanged).Preview!;
        var first = harness.Session.Snapshot.Items[0];
        Task operation;

        switch (boundary)
        {
            case "inspection":
                harness.OnInspect = async request => { entered.TrySetResult(); await release.Task.WaitAsync(request.CancellationToken); };
                operation = coordinator.InspectItemAsync(new(first.Id, TestContext.Current.CancellationToken));
                break;
            case "review":
                operation = coordinator.ReviewSelectionsAsync(new(harness.Session.Snapshot.Revision, [], TestContext.Current.CancellationToken));
                break;
            case "verification":
                harness.OnVerify = async request => { entered.TrySetResult(); await release.Task.WaitAsync(request.CancellationToken); };
                operation = coordinator.RunAsync(new(reviewed.Id, CancellationToken: TestContext.Current.CancellationToken));
                break;
            case "cleanup":
                harness.OnCleanup = async _ => { entered.TrySetResult(); await release.Task; };
                operation = coordinator.RunAsync(new(reviewed.Id, CancellationToken: TestContext.Current.CancellationToken));
                break;
            default:
                harness.OnRemux = async request => { entered.TrySetResult(); await release.Task.WaitAsync(request.CancellationToken); };
                operation = coordinator.RunAsync(new(reviewed.Id, CancellationToken: TestContext.Current.CancellationToken));
                break;
        }

        try
        {
            await BatchTestHarness.Await(entered.Task);
            var active = harness.Session.Snapshot;
            var rejectedPreview = coordinator.PreviewLanguageRules(BatchLanguageTestData.KeepRussianSubtitles);
            var rejectedApply = coordinator.ApplyLanguageRules(preview);
            var rejectedSelection = harness.Session.SetSelection(new(first.Id, first.CurrentRetainedTrackIds));
            Assert.Null(rejectedPreview.Preview);
            Assert.Equal(ErrorCode.Busy, rejectedPreview.Failure!.Code);
            Assert.False(rejectedApply.Applied);
            Assert.Equal(ErrorCode.Busy, rejectedApply.Failure!.Code);
            Assert.False(rejectedSelection.Applied);
            Assert.Equal(ErrorCode.Busy, rejectedSelection.Failure!.Code);
            Assert.Same(active, harness.Session.Snapshot);
        }
        finally
        {
            release.TrySetResult();
            await BatchTestHarness.Await(operation);
        }
    }

    /// <summary>Preserves the warning owner and candidate without approval, cleanup or another remux.</summary>
    [Fact]
    public async Task WarningReviewRejectsRulesWithoutTouchingCandidateOwnership()
    {
        var harness = new BatchTestHarness(1);
        var review = await harness.PrepareAsync();
        var preview = harness.Coordinator.PreviewLanguageRules(BatchLanguageTestData.Unchanged).Preview!;
        var item = harness.Session.Snapshot.Items[0];
        harness.ExitCodes[item.SourcePath] = 1;
        var warning = await harness.Coordinator.RunAsync(new(review.Id, CancellationToken: TestContext.Current.CancellationToken));

        try
        {
            var rejectedPreview = harness.Coordinator.PreviewLanguageRules(BatchLanguageTestData.KeepRussianSubtitles);
            var rejectedApply = harness.Coordinator.ApplyLanguageRules(preview);
            Assert.Null(rejectedPreview.Preview);
            Assert.Equal(ErrorCode.Busy, rejectedPreview.Failure!.Code);
            Assert.False(rejectedApply.Applied);
            Assert.Equal(ErrorCode.Busy, rejectedApply.Failure!.Code);
            Assert.Same(warning, harness.Session.Snapshot);
            Assert.Equal(BatchItemState.AwaitingWarningReview, warning.Items[0].State);
            Assert.Equal(0, harness.CleanupCalls);
            Assert.Single(harness.RemuxedPaths);
            Assert.Empty(harness.PublishedPaths);
        }
        finally
        {
            await harness.Coordinator.DiscardWarningsAsync(new(item.Id, TestContext.Current.CancellationToken));
        }
    }

    /// <summary>Retains the actual unsafe failure and all original evidence while refusing preview and application.</summary>
    [Theory]
    [InlineData("termination", ErrorCode.ProcessTerminationFailed)]
    [InlineData("publication", ErrorCode.PublicationUncertain)]
    [InlineData("cleanup", ErrorCode.OwnershipInvalid)]
    [InlineData("unexpected", ErrorCode.IoFailure)]
    [InlineData("inspection", ErrorCode.ProcessTerminationFailed)]
    public async Task UnsafeOutcomesRejectRulesWithOriginalFailure(string boundary, ErrorCode expectedCode)
    {
        var harness = new BatchTestHarness(1);
        var review = await harness.PrepareAsync();
        var preview = harness.Coordinator.PreviewLanguageRules(BatchLanguageTestData.Unchanged).Preview!;

        switch (boundary)
        {
            case "termination":
                harness.RemuxFailure = new(ErrorCode.ProcessTerminationFailed, "Unresolved original worker.");
                break;
            case "publication":
                harness.PublicationFailure = new(ErrorCode.PublicationUncertain, "Uncertain original publication.");
                break;
            case "cleanup":
                harness.CleanupFails = true;
                break;
            case "unexpected":
                harness.OnRemux = _ => throw new InvalidOperationException("Unexpected original worker failure.");
                break;
            case "inspection":
                harness.OnInspect = _ => throw new OperationFailureException(new(ErrorCode.ProcessTerminationFailed, "Unresolved original probe."));
                break;
        }

        if (boundary == "inspection")
        {
            await harness.Coordinator.InspectItemAsync(new(harness.Session.Snapshot.Items[0].Id, TestContext.Current.CancellationToken));
        }

        if (boundary != "inspection")
        {
            await harness.Coordinator.RunAsync(new(review.Id, CancellationToken: TestContext.Current.CancellationToken));
        }

        var unsafeSnapshot = harness.Session.Snapshot;
        var remuxCalls = harness.RemuxedPaths.Count;
        var cleanupCalls = harness.CleanupCalls;
        var publications = harness.PublishedPaths.ToArray();
        Assert.True(unsafeSnapshot.HasSafetyBlock);
        var rejectedPreview = harness.Coordinator.PreviewLanguageRules(BatchLanguageTestData.KeepRussianSubtitles);
        var rejectedApply = harness.Coordinator.ApplyLanguageRules(preview);
        Assert.Null(rejectedPreview.Preview);
        Assert.Equal(expectedCode, rejectedPreview.Failure!.Code);
        Assert.False(rejectedApply.Applied);
        Assert.Equal(expectedCode, rejectedApply.Failure!.Code);
        Assert.Same(unsafeSnapshot, harness.Session.Snapshot);
        Assert.Equal(remuxCalls, harness.RemuxedPaths.Count);
        Assert.Equal(cleanupCalls, harness.CleanupCalls);
        Assert.Equal(publications, harness.PublishedPaths);

        if (expectedCode != ErrorCode.OwnershipInvalid)
        {
            Assert.Same(unsafeSnapshot.Items[0].Failure ?? unsafeSnapshot.Failure, rejectedApply.Failure);
            Assert.Same(rejectedPreview.Failure, rejectedApply.Failure);
        }
    }

    /// <summary>Applies the session busy guard even when a different coordinator has its own free gate.</summary>
    [Fact]
    public async Task SeparateCoordinatorCannotPreviewAnActiveSession()
    {
        var harness = new BatchTestHarness(1);
        var entered = BatchTestHarness.Signal();
        var release = BatchTestHarness.Signal();
        harness.OnInspect = async request => { entered.TrySetResult(); await release.Task.WaitAsync(request.CancellationToken); };
        var operation = harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        var other = new BatchCoordinator(harness.Session, new InspectionService(harness, harness),
            new JobCoordinator(harness, harness, harness), harness);

        try
        {
            await BatchTestHarness.Await(entered.Task);
            var active = harness.Session.Snapshot;
            var result = other.PreviewLanguageRules(BatchLanguageTestData.Unchanged);
            Assert.Null(result.Preview);
            Assert.Equal(ErrorCode.Busy, result.Failure!.Code);
            Assert.Same(active, harness.Session.Snapshot);
        }
        finally
        {
            release.TrySetResult();
            await BatchTestHarness.Await(operation);
        }
    }

    /// <summary>Exposes read-only review synchronization without modifying the real job coordinator.</summary>
    private sealed class GatedReviewSourceAccess(
        ISourceAccess inner,
        TaskCompletionSource entered,
        TaskCompletionSource release) : ISourceAccess
    {
        /// <summary>Holds the review before any media work is owned.</summary>
        public async Task<SourceRevision> ReadRevisionAsync(SourceRequest request)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(request.CancellationToken);
            return await inner.ReadRevisionAsync(request);
        }

        /// <summary>Implements the explicit preparation boundary without adding filesystem effects to this controlled port.</summary>
        public Task PrepareDestinationAsync(ValidateDestinationRequest request) => inner.PrepareDestinationAsync(request);

        /// <summary>Forwards existing destination checks after release.</summary>
        public Task ValidateDestinationAsync(ValidateDestinationRequest request) => inner.ValidateDestinationAsync(request);
    }
}
