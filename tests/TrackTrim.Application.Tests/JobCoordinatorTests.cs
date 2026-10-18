using TrackTrim.Application.Jobs;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Planning;
using Xunit;

namespace TrackTrim.Application.Tests;

/// <summary>Verifies publication, warnings, cancellation and cleanup as separate evidence.</summary>
public sealed class JobCoordinatorTests
{
    /// <summary>Publishes only after structural verification completes.</summary>
    [Fact]
    public async Task CleanVerifiedOutputIsPublished()
    {
        var harness = new JobTestHarness();
        var result = await harness.Coordinator.RunAsync(new(harness.CreatePlan()));
        Assert.Equal(JobPhase.Completed, result.Phase);
        Assert.Equal("output.mkv", result.PublishedPath);
        Assert.Equal(80, result.OutputLength);
        Assert.Equal(1, harness.VerifyCalls);
        Assert.Equal(1, harness.PublishCalls);
    }

    /// <summary>Does not reserve storage or start a worker for a no-change plan.</summary>
    [Fact]
    public async Task NoChangeNeverLaunchesRemux()
    {
        var harness = new JobTestHarness();
        var plan = PlanBuilder.Build(new(harness.Inspection, [0, 1, 2], "output.mkv", false)).Plan!;
        var result = await harness.Coordinator.RunAsync(new(plan));
        Assert.Equal(JobPhase.NoChange, result.Phase);
        Assert.Equal(0, harness.RemuxCalls);
        Assert.Equal(0, harness.ReserveCalls);
    }

    /// <summary>Retains a warning candidate until explicit approval and re-verifies without remuxing.</summary>
    [Fact]
    public async Task WarningCandidateRequiresExplicitPublication()
    {
        var harness = new JobTestHarness { ExitCode = 1 };
        var coordinator = harness.Coordinator;
        var result = await coordinator.RunAsync(new(harness.CreatePlan()));
        Assert.Equal(JobPhase.AwaitingWarningReview, result.Phase);
        Assert.Equal(0, harness.PublishCalls);
        Assert.Equal(0, harness.CleanupCalls);
        var approved = await coordinator.PublishWarningsAsync(new(result.JobId));
        Assert.Equal(JobPhase.Completed, approved.Phase);
        Assert.Equal(1, harness.RemuxCalls);
        Assert.Equal(2, harness.VerifyCalls);
        Assert.Equal(1, approved.Tool?.ExitCode);
    }

    /// <summary>Deletes a discarded owned candidate without publication.</summary>
    [Fact]
    public async Task WarningCandidateCanBeDiscarded()
    {
        var harness = new JobTestHarness { ExitCode = 1 };
        var coordinator = harness.Coordinator;
        var result = await coordinator.RunAsync(new(harness.CreatePlan()));
        var discarded = await coordinator.DiscardWarningsAsync(new(result.JobId));
        Assert.Equal(JobPhase.Cancelled, discarded.Phase);
        Assert.Equal(0, harness.PublishCalls);
        Assert.Equal(1, harness.CleanupCalls);
    }

    /// <summary>Rejects a warning candidate when re-verification fails during review.</summary>
    [Fact]
    public async Task WarningApprovalCannotOverrideVerificationFailure()
    {
        var harness = new JobTestHarness { ExitCode = 1 };
        var coordinator = harness.Coordinator;
        var result = await coordinator.RunAsync(new(harness.CreatePlan()));
        harness.VerificationPasses = false;
        var approved = await coordinator.PublishWarningsAsync(new(result.JobId));
        Assert.Equal(ErrorCode.VerificationFailed, approved.Failure?.Code);
        Assert.Equal(0, harness.PublishCalls);
    }

    /// <summary>Rechecks the source after a warning review delay.</summary>
    [Fact]
    public async Task ChangedSourceInvalidatesWarningApproval()
    {
        var harness = new JobTestHarness { ExitCode = 1 };
        var coordinator = harness.Coordinator;
        var result = await coordinator.RunAsync(new(harness.CreatePlan()));
        harness.SourceChanges = true;
        var approved = await coordinator.PublishWarningsAsync(new(result.JobId));
        Assert.Equal(ErrorCode.SourceChanged, approved.Failure?.Code);
        Assert.Equal(0, harness.PublishCalls);
    }

    /// <summary>Detects candidate changes during required verification before any publication.</summary>
    [Fact]
    public async Task CandidateChangeDuringVerificationCannotPublish()
    {
        var harness = new JobTestHarness();
        harness.OnVerify = () => harness.CandidateChanges = true;
        var result = await harness.Coordinator.RunAsync(new(harness.CreatePlan()));
        Assert.Equal(ErrorCode.VerificationFailed, result.Failure?.Code);
        Assert.Equal(0, harness.PublishCalls);
    }

    /// <summary>Refuses clean and warning-bearing candidates that fail required checks.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task FailedVerificationCannotPublish(int exitCode)
    {
        var harness = new JobTestHarness { ExitCode = exitCode, VerificationPasses = false };
        var result = await harness.Coordinator.RunAsync(new(harness.CreatePlan()));
        Assert.Equal(ErrorCode.VerificationFailed, result.Failure?.Code);
        Assert.Null(result.PublishedPath);
        Assert.Equal(0, harness.PublishCalls);
    }

    /// <summary>Rejects a source changed during worker execution.</summary>
    [Fact]
    public async Task SourceChangeBeforePublicationInvalidatesOutput()
    {
        var harness = new JobTestHarness();
        harness.OnRemux = () => harness.SourceChanges = true;
        var result = await harness.Coordinator.RunAsync(new(harness.CreatePlan()));
        Assert.Equal(ErrorCode.SourceChanged, result.Failure?.Code);
        Assert.Equal(0, harness.PublishCalls);
        Assert.Equal(1, harness.CleanupCalls);
    }

    /// <summary>Reports a destination appearing at the operation itself as unpublished failure.</summary>
    [Fact]
    public async Task LateDestinationCollisionNeverCompletesJob()
    {
        var harness = new JobTestHarness { LateCollision = true };
        var result = await harness.Coordinator.RunAsync(new(harness.CreatePlan()));
        Assert.Equal(ErrorCode.DestinationExists, result.Failure?.Code);
        Assert.Null(result.PublishedPath);
        Assert.Equal(JobPhase.Failed, result.Phase);
    }

    /// <summary>Preserves successful publication when cleanup fails or cancellation arrives too late.</summary>
    [Fact]
    public async Task LateCancelAndCleanupFailureDoNotErasePublishedOutput()
    {
        using var cancellation = new CancellationTokenSource();
        var harness = new JobTestHarness { FailCleanup = true, OnPublish = cancellation.Cancel };
        var result = await harness.Coordinator.RunAsync(new(harness.CreatePlan(), CancellationToken: cancellation.Token));
        Assert.Equal(JobPhase.Completed, result.Phase);
        Assert.Equal("output.mkv", result.PublishedPath);
        Assert.True(result.CancellationRequested);
        Assert.Null(result.Failure);
        Assert.Single(result.CleanupIssues);
    }

    /// <summary>Stops before verification and publication when cancellation is observed.</summary>
    [Fact]
    public async Task CancellationDuringWorkerNeverPublishes()
    {
        using var cancellation = new CancellationTokenSource();
        var harness = new JobTestHarness { OnRemux = cancellation.Cancel };
        var result = await harness.Coordinator.RunAsync(new(harness.CreatePlan(), CancellationToken: cancellation.Token));
        Assert.Equal(JobPhase.Cancelled, result.Phase);
        Assert.True(result.CancellationRequested);
        Assert.Equal(0, harness.PublishCalls);
        Assert.Equal(1, harness.CleanupCalls);
    }

    /// <summary>Returns a cancellation outcome before storage reservation or a worker launch.</summary>
    [Fact]
    public async Task PendingCancellationNeverReservesOrLaunchesWorker()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var harness = new JobTestHarness();
        var result = await harness.Coordinator.RunAsync(new(harness.CreatePlan(), CancellationToken: cancellation.Token));
        Assert.Equal(JobPhase.Cancelled, result.Phase);
        Assert.Equal(0, harness.ReserveCalls);
        Assert.Equal(0, harness.RemuxCalls);
    }

    /// <summary>Retains a potentially active candidate when process termination cannot be established.</summary>
    [Fact]
    public async Task UnconfirmedTerminationDoesNotDeleteCandidate()
    {
        var harness = new JobTestHarness { TerminationFails = true };
        var result = await harness.Coordinator.RunAsync(new(harness.CreatePlan()));
        Assert.Equal(ErrorCode.ProcessTerminationFailed, result.Failure?.Code);
        Assert.Equal(0, harness.CleanupCalls);
    }

    /// <summary>Keeps the original failure when cleanup also fails.</summary>
    [Fact]
    public async Task CleanupFailureDoesNotReplaceToolFailure()
    {
        var harness = new JobTestHarness { ExitCode = 2, FailCleanup = true };
        var result = await harness.Coordinator.RunAsync(new(harness.CreatePlan()));
        Assert.Equal(ErrorCode.ToolFailed, result.Failure?.Code);
        Assert.Single(result.CleanupIssues);
    }
}
