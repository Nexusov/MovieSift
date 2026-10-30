using MovieSift.Application.Batches;
using MovieSift.Application.Jobs;
using MovieSift.Application.Tests.Batches;
using MovieSift.Core.Failures;
using MovieSift.Core.Planning;
using Xunit;

namespace MovieSift.Application.Tests;

/// <summary>Protects preparation ordering, cancellation and owned cleanup through the existing job lifecycle.</summary>
public sealed class JobInputPreparationTests
{
    /// <summary>The same reserved input is fully prepared before the backend can remux it.</summary>
    [Fact]
    public async Task PreparesReservedInputBeforeRemux()
    {
        var harness = new JobTestHarness { RequiresInputProjection = true };
        var prepared = false;
        harness.OnPrepareInput = _ => { prepared = true; return Task.CompletedTask; };
        harness.OnRemux = () => Assert.True(prepared);
        var result = await harness.Coordinator.RunAsync(new(harness.CreatePlan()));
        Assert.Equal(JobPhase.Completed, result.Phase);
        Assert.Equal(1, harness.PrepareInputCalls);
        Assert.Same(harness.PreparedInput, harness.RemuxInput);
        Assert.Equal(1, harness.CleanupCalls);
    }

    /// <summary>Missing reservation evidence and preparation failures cannot reach remux or publication.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedPreparationCleansWithoutStartingRemux(bool missingResource)
    {
        var harness = new JobTestHarness { RequiresInputProjection = true, OmitInputProjection = missingResource };
        harness.OnPrepareInput = _ => throw new OperationFailureException(new(ErrorCode.IoFailure, "controlled preparation failure"));
        var result = await harness.Coordinator.RunAsync(new(harness.CreatePlan()));
        Assert.Equal(JobPhase.Failed, result.Phase);
        Assert.Equal(missingResource ? ErrorCode.OwnershipInvalid : ErrorCode.IoFailure, result.Failure!.Code);
        Assert.Equal(0, harness.RemuxCalls);
        Assert.Equal(0, harness.PublishCalls);
        Assert.Equal(1, harness.CleanupCalls);
    }

    /// <summary>Uncertain preparation-worker termination preserves owned resources for explicit interruption handling.</summary>
    [Fact]
    public async Task UncertainPreparationTerminationDoesNotClean()
    {
        var harness = new JobTestHarness { RequiresInputProjection = true };
        harness.OnPrepareInput = _ => throw new OperationFailureException(new(ErrorCode.ProcessTerminationFailed));
        var result = await harness.Coordinator.RunAsync(new(harness.CreatePlan()));
        Assert.Equal(ErrorCode.ProcessTerminationFailed, result.Failure!.Code);
        Assert.Equal(0, harness.RemuxCalls);
        Assert.Equal(0, harness.CleanupCalls);
    }

    /// <summary>NoChange remains free of reservation and preparation even for a backend that requires projected input.</summary>
    [Fact]
    public async Task NoChangeDoesNotReserveOrPrepare()
    {
        var harness = new JobTestHarness { RequiresInputProjection = true };
        var plan = PlanBuilder.Build(new(harness.Inspection, [0, 1, 2], "output.mkv", false)).Plan!;
        var result = await harness.Coordinator.RunAsync(new(plan));
        Assert.Equal(JobPhase.NoChange, result.Phase);
        Assert.Equal(0, harness.ReserveCalls);
        Assert.Equal(0, harness.PrepareInputCalls);
        Assert.Equal(0, harness.RemuxCalls);
    }

    /// <summary>A source mutation during preparation invalidates the plan before the media worker can use the copy.</summary>
    [Fact]
    public async Task PreparationRechecksTheOriginalSource()
    {
        var harness = new JobTestHarness { RequiresInputProjection = true };
        harness.OnPrepareInput = _ => { harness.SourceChanges = true; return Task.CompletedTask; };
        var result = await harness.Coordinator.RunAsync(new(harness.CreatePlan()));
        Assert.Equal(ErrorCode.SourceChanged, result.Failure!.Code);
        Assert.Equal(0, harness.RemuxCalls);
        Assert.Equal(1, harness.CleanupCalls);
    }

    /// <summary>Cancellation inside preparation awaits cleanup and cannot start the next approved batch item.</summary>
    [Fact]
    public async Task BatchCancellationDuringPreparationLeavesNextItemPending()
    {
        var harness = new BatchTestHarness { RequiresInputProjection = true };
        var review = await harness.PrepareAsync();
        var entered = BatchTestHarness.Signal();
        var release = BatchTestHarness.Signal();
        harness.OnPrepareInput = async request =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(request.CancellationToken);
        };
        var run = harness.Coordinator.RunAsync(new(review.Id));
        await BatchTestHarness.Await(entered.Task);
        harness.Coordinator.Cancel();
        var result = await run;
        Assert.Equal(BatchItemState.Cancelled, result.Items[0].State);
        Assert.Equal(BatchItemState.Ready, result.Items[1].State);
        Assert.Empty(harness.RemuxedPaths);
        Assert.Empty(harness.PublishedPaths);
        Assert.Equal(1, harness.CleanupCalls);
    }
}
