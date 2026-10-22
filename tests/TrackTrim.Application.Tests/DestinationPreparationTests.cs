using TrackTrim.Application.Jobs;
using TrackTrim.Application.Ports;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Media;
using TrackTrim.Core.Planning;
using Xunit;

namespace TrackTrim.Application.Tests;

/// <summary>Controls the preparation boundary without pretending to create or verify real media.</summary>
public sealed class DestinationPreparationTests
{
    /// <summary>A preparation failure cannot reserve candidate ownership or launch a worker.</summary>
    [Fact]
    public async Task FailedPreparationStopsBeforeReservationAndRemux()
    {
        var harness = new JobTestHarness();
        var access = new PreparationAccess(harness) { PreparationFailure = new(ErrorCode.IoFailure, "controlled preparation failure") };
        var result = await new JobCoordinator(harness, access, harness).RunAsync(new(NestedPlan(harness)));
        Assert.Equal(JobPhase.Failed, result.Phase);
        Assert.Equal(access.PreparationFailure, result.Failure);
        Assert.Equal(1, access.PreparationCalls);
        Assert.Equal(0, harness.ReserveCalls);
        Assert.Equal(0, harness.RemuxCalls);
        Assert.Equal(0, harness.CleanupCalls);
    }

    /// <summary>Rechecks a destination after preparation even if aggregate review observed it absent.</summary>
    [Fact]
    public async Task CollisionAppearingDuringPreparationDoesNotLaunchWorker()
    {
        var harness = new JobTestHarness();
        var access = new PreparationAccess(harness) { CollisionAfterPreparation = true };
        var result = await new JobCoordinator(harness, access, harness).RunAsync(new(NestedPlan(harness)));
        Assert.Equal(ErrorCode.DestinationExists, result.Failure?.Code);
        Assert.Equal(2, access.Validations.Count);
        Assert.Equal(0, harness.ReserveCalls);
        Assert.Equal(0, harness.RemuxCalls);
    }

    /// <summary>Does not create directories for no-change plans.</summary>
    [Fact]
    public async Task NoChangeDoesNotPrepareAnyDirectory()
    {
        var harness = new JobTestHarness();
        var access = new PreparationAccess(harness);
        var root = Path.Combine(Path.GetTempPath(), "controlled-output-root");
        var plan = PlanBuilder.Build(new(harness.Inspection, [0, 1, 2],
            Path.Combine(root, "missing season", "episode.trimmed.mkv"), false, root)).Plan!;
        var result = await new JobCoordinator(harness, access, harness).RunAsync(new(plan));
        Assert.Equal(JobPhase.NoChange, result.Phase);
        Assert.Equal(0, access.PreparationCalls);
        Assert.Empty(access.Validations);
        Assert.Equal(0, harness.ReserveCalls);
    }

    /// <summary>Rejects a changed original before creating folders or claiming any candidate ownership.</summary>
    [Fact]
    public async Task ChangedSourceDoesNotPrepareOutputDirectories()
    {
        var harness = new JobTestHarness();
        var access = new PreparationAccess(harness);
        var plan = NestedPlan(harness);
        harness.SourceChanges = true;

        var result = await new JobCoordinator(harness, access, harness).RunAsync(new(plan));

        Assert.Equal(ErrorCode.SourceChanged, result.Failure?.Code);
        Assert.Equal(0, access.PreparationCalls);
        Assert.Empty(access.Validations);
        Assert.Equal(0, harness.RemuxCalls);
        Assert.Equal(0, harness.ReserveCalls);
    }

    /// <summary>Warning publication reuses the original root without preparing or remuxing a second time.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task RootSurvivesValidationPreparationAndPublication(int exitCode)
    {
        var harness = new JobTestHarness { ExitCode = exitCode };
        var access = new PreparationAccess(harness);
        var root = Path.Combine(Path.GetTempPath(), "controlled-output-root");
        var plan = harness.CreatePlan() with { DestinationPath = Path.Combine(root, "Season 1", "episode.mkv"), OutputDirectoryRoot = root };
        var coordinator = new JobCoordinator(harness, access, harness);
        var result = await coordinator.RunAsync(new(plan));

        if (exitCode == 1)
        {
            Assert.Equal(JobPhase.AwaitingWarningReview, result.Phase);
            result = await coordinator.PublishWarningsAsync(new(result.JobId));
        }

        Assert.Equal(JobPhase.Completed, result.Phase);
        Assert.Equal(1, access.PreparationCalls);
        Assert.Equal(1, harness.RemuxCalls);
        Assert.Equal(3, access.Validations.Count);
        Assert.All(access.Validations, request => Assert.Equal(root, request.OutputDirectoryRoot));
        Assert.Equal(0, access.Validations[^1].AdditionalAllocationBytes);
        Assert.Equal(plan.Inspection.Source.Length, access.Validations[0].AdditionalAllocationBytes);
    }

    /// <summary>Creates only a controlled plan shape; the real-file tests verify directory safety separately.</summary>
    private static RemuxPlan NestedPlan(JobTestHarness harness)
    {
        var root = Path.Combine(Path.GetTempPath(), "controlled-output-root");
        return harness.CreatePlan() with { DestinationPath = Path.Combine(root, "Season 1", "episode.mkv"), OutputDirectoryRoot = root };
    }

    /// <summary>Observes coordinator sequencing while retaining the existing controlled lifecycle harness.</summary>
    private sealed class PreparationAccess(JobTestHarness harness) : ISourceAccess
    {
        public int PreparationCalls { get; private set; }
        public Failure? PreparationFailure { get; init; }
        public bool CollisionAfterPreparation { get; init; }
        public List<ValidateDestinationRequest> Validations { get; } = [];

        /// <summary>Retains the harness's source and candidate revision checks.</summary>
        public Task<SourceRevision> ReadRevisionAsync(SourceRequest request) => harness.ReadRevisionAsync(request);

        /// <summary>Introduces a deterministic collision only after the preparation boundary.</summary>
        public Task ValidateDestinationAsync(ValidateDestinationRequest request)
        {
            Validations.Add(request);

            if (CollisionAfterPreparation && PreparationCalls != 0)
            {
                throw new OperationFailureException(new(ErrorCode.DestinationExists));
            }

            return harness.ValidateDestinationAsync(request);
        }

        /// <summary>Fails before ownership exists or acknowledges one safe preparation.</summary>
        public Task PrepareDestinationAsync(ValidateDestinationRequest request)
        {
            PreparationCalls++;
            Assert.Equal(0, harness.ReserveCalls);

            if (PreparationFailure is not null)
            {
                throw new OperationFailureException(PreparationFailure);
            }

            return Task.CompletedTask;
        }
    }
}
