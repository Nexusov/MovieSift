using MovieSift.Application.Inspection;
using MovieSift.Application.Ports;
using MovieSift.Core.Media;
using Xunit;

namespace MovieSift.Application.Tests;

/// <summary>Uses explicit synchronization to reproduce late inspection responses.</summary>
public sealed class InspectionServiceTests
{
    /// <summary>A retained backend callback cannot report another stage after a successful request returned.</summary>
    [Fact]
    public async Task CompletedInspectionCannotReportLateStages()
    {
        var harness = new JobTestHarness();
        var backend = new DelayedBackend(harness.Inspection);
        var service = new InspectionService(backend, harness);
        var observer = new StageObserver();
        var operation = service.InspectAsync(new("first.mkv", Progress: observer));
        await backend.Started.Task;
        backend.Completion.SetResult(harness.Inspection);
        var result = await operation;
        Assert.Same(harness.Inspection, result.Inspection);
        Assert.Contains(InspectionStage.RecheckingSource, observer.Stages);
        var before = observer.Stages.Count;
        backend.FirstProgress!.Report(InspectionStage.ReadingMetadata);
        Assert.Equal(before, observer.Stages.Count);
    }

    /// <summary>Rejects late backend stages from a request superseded by another inspection.</summary>
    [Fact]
    public async Task SupersededInspectionCannotReportStagesToItsObserver()
    {
        var harness = new JobTestHarness();
        var backend = new DelayedBackend(harness.Inspection);
        var service = new InspectionService(backend, harness);
        var observer = new StageObserver();
        var firstTask = service.InspectAsync(new("first.mkv", Progress: observer));
        await backend.Started.Task;
        Assert.Contains(InspectionStage.CheckingSource, observer.Stages);
        await service.InspectAsync(new("second.mkv"));
        var before = observer.Stages.Count;
        backend.FirstProgress!.Report(InspectionStage.ReadingMetadata);
        backend.Completion.SetResult(harness.Inspection);
        Assert.True((await firstTask).IsStale);
        Assert.Equal(before, observer.Stages.Count);
    }

    /// <summary>Rejects cancellation-late stages without inventing progress for a stopped inspection.</summary>
    [Fact]
    public async Task CancelledInspectionCannotReportLateStages()
    {
        var harness = new JobTestHarness();
        var backend = new DelayedBackend(harness.Inspection);
        var service = new InspectionService(backend, harness);
        var observer = new StageObserver();
        using var cancellation = new CancellationTokenSource();
        var operation = service.InspectAsync(new("first.mkv", cancellation.Token, observer));
        await backend.Started.Task;
        await cancellation.CancelAsync();
        var before = observer.Stages.Count;
        backend.FirstProgress!.Report(InspectionStage.ReadingMetadata);
        backend.Completion.SetCanceled(cancellation.Token);
        await operation;
        Assert.Equal(before, observer.Stages.Count);
    }

    /// <summary>A late failed inspection cannot replace the current source or its error state.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewerInspectionSupersedesLateSuccessAndIoFailure(bool failLate)
    {
        var harness = new JobTestHarness();
        var backend = new DelayedBackend(harness.Inspection);
        var service = new InspectionService(backend, harness);
        var firstTask = service.InspectAsync(new("first.mkv"));
        await backend.Started.Task;
        var secondResult = await service.InspectAsync(new("second.mkv"));

        if (failLate)
        {
            backend.Completion.SetException(new IOException("late failure"));
        }

        if (!failLate)
        {
            backend.Completion.SetResult(harness.Inspection);
        }

        var firstResult = await firstTask;
        Assert.True(firstResult.IsStale);
        Assert.False(secondResult.IsStale);
        Assert.Same(harness.Inspection, secondResult.Inspection);
    }

    /// <summary>Completes one request only when the test explicitly releases it.</summary>
    private sealed class DelayedBackend(MediaInspection inspection) : IMediaBackend
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<MediaInspection> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IProgress<InspectionStage>? FirstProgress { get; private set; }

        /// <summary>Pauses the first source while allowing the second request to finish.</summary>
        public Task<MediaInspection> InspectAsync(InspectRequest request)
        {
            if (request.SourcePath == "first.mkv")
            {
                FirstProgress = request.Progress;
                Started.SetResult();
                return Completion.Task;
            }

            return Task.FromResult(inspection);
        }

        /// <summary>Does not implement remuxing in an inspection-only test double.</summary>
        public Task<ToolOutcome> RemuxAsync(RemuxRequest request) => throw new NotSupportedException();

        /// <summary>Does not implement verification in an inspection-only test double.</summary>
        public Task<VerificationOutcome> VerifyAsync(VerifyRequest request) => throw new NotSupportedException();
    }

    /// <summary>Records synchronous stage notifications without a captured dispatcher.</summary>
    private sealed class StageObserver : IProgress<InspectionStage>
    {
        public List<InspectionStage> Stages { get; } = [];

        /// <summary>Retains the exact reported stage sequence for lifecycle assertions.</summary>
        public void Report(InspectionStage value) => Stages.Add(value);
    }
}
