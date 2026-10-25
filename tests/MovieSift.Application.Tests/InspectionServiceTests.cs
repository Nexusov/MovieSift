using MovieSift.Application.Inspection;
using MovieSift.Application.Ports;
using MovieSift.Core.Media;
using Xunit;

namespace MovieSift.Application.Tests;

/// <summary>Uses explicit synchronization to reproduce late inspection responses.</summary>
public sealed class InspectionServiceTests
{
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

        /// <summary>Pauses the first source while allowing the second request to finish.</summary>
        public Task<MediaInspection> InspectAsync(InspectRequest request)
        {
            if (request.SourcePath == "first.mkv")
            {
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
}
