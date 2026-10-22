using TrackTrim.Application;
using TrackTrim.Application.Ports;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Media;
using TrackTrim.Core.Planning;
using TrackTrim.Infrastructure.MediaTools;
using Xunit;

namespace TrackTrim.Infrastructure.Tests;

/// <summary>Separates candidate routing from frozen execution identity through controlled media ports.</summary>
public sealed class ContainerMediaBackendTests
{
    /// <summary>A rejected format probe never falls through to the other container adapter.</summary>
    [Theory]
    [InlineData("source.mkv", MediaContainer.Matroska)]
    [InlineData("source.MP4", MediaContainer.Mp4)]
    public async Task InspectionRoutesOneCandidateAndDoesNotFallbackAfterRejection(string path, MediaContainer expectedContainer)
    {
        var matroska = new RecordingBackend(MediaContainer.Matroska);
        var mp4 = new RecordingBackend(MediaContainer.Mp4);
        var selected = expectedContainer == MediaContainer.Matroska ? matroska : mp4;
        selected.RejectInspection = true;
        var backend = new ContainerMediaBackend(matroska, mp4);
        var failure = await Assert.ThrowsAsync<OperationFailureException>(() => backend.InspectAsync(new(path)));

        Assert.Equal(ErrorCode.UnsupportedContainer, failure.Failure.Code);
        Assert.Equal(expectedContainer == MediaContainer.Matroska ? 1 : 0, matroska.Inspections);
        Assert.Equal(expectedContainer == MediaContainer.Mp4 ? 1 : 0, mp4.Inspections);
        Assert.Equal(0, matroska.Remuxes + mp4.Remuxes);
    }

    /// <summary>Neither the source suffix nor the owned candidate suffix can replace approved backend identity.</summary>
    [Theory]
    [InlineData(MediaContainer.Matroska)]
    [InlineData(MediaContainer.Mp4)]
    public async Task ExecutionAndVerificationUseTheFrozenContainer(MediaContainer container)
    {
        var matroska = new RecordingBackend(MediaContainer.Matroska);
        var mp4 = new RecordingBackend(MediaContainer.Mp4);
        var backend = new ContainerMediaBackend(matroska, mp4);
        var inspection = Inspection(container) with
        {
            Source = new("misleading-source.mov", "source", 1, 1)
        };
        var plan = new RemuxPlan(Guid.NewGuid(), inspection, [], "output" + MediaContainerPolicy.Extension(container), false);
        var result = await backend.RemuxAsync(new(plan, "candidate.unrecognized", null));
        var verification = await backend.VerifyAsync(new(plan, "candidate.unrecognized"));

        Assert.Equal(ToolDisposition.Success, result.EffectiveDisposition);
        Assert.True(verification.Passed);
        Assert.Equal(container == MediaContainer.Matroska ? 1 : 0, matroska.Remuxes);
        Assert.Equal(container == MediaContainer.Mp4 ? 1 : 0, mp4.Remuxes);
        Assert.Equal(container == MediaContainer.Matroska ? 1 : 0, matroska.Verifications);
        Assert.Equal(container == MediaContainer.Mp4 ? 1 : 0, mp4.Verifications);
        Assert.Equal(0, matroska.Inspections + mp4.Inspections);
    }

    /// <summary>An unknown frozen value cannot silently select a default backend.</summary>
    [Fact]
    public async Task UnknownContainerAndUnsupportedCandidateNeverInvokeEitherAdapter()
    {
        var matroska = new RecordingBackend(MediaContainer.Matroska);
        var mp4 = new RecordingBackend(MediaContainer.Mp4);
        var backend = new ContainerMediaBackend(matroska, mp4);
        var inspection = Inspection((MediaContainer)int.MaxValue);
        var plan = new RemuxPlan(Guid.NewGuid(), inspection, [], "output.mp4", false);
        var inspectFailure = await Assert.ThrowsAsync<OperationFailureException>(() => backend.InspectAsync(new("source.mov")));
        var remuxFailure = await Assert.ThrowsAsync<OperationFailureException>(() => backend.RemuxAsync(new(plan, "candidate.mp4", null)));
        var verifyFailure = await Assert.ThrowsAsync<OperationFailureException>(() => backend.VerifyAsync(new(plan, "candidate.mp4")));

        Assert.Equal(ErrorCode.UnsupportedContainer, inspectFailure.Failure.Code);
        Assert.Equal(ErrorCode.UnsupportedContainer, remuxFailure.Failure.Code);
        Assert.Equal(ErrorCode.UnsupportedContainer, verifyFailure.Failure.Code);
        Assert.Equal(0, matroska.Inspections + mp4.Inspections + matroska.Remuxes + mp4.Remuxes + matroska.Verifications + mp4.Verifications);
    }

    /// <summary>Creates a pure immutable inspection without asserting anything about actual media content.</summary>
    private static MediaInspection Inspection(MediaContainer container) => new(
        new("source", "source", 1, 1), new("controlled backend", "1", 1), [], [], "", false, false, container);

    /// <summary>Records only dispatch decisions while preserving the shared normalized outcome contract.</summary>
    private sealed class RecordingBackend(MediaContainer container) : IMediaBackend
    {
        public int Inspections { get; private set; }
        public int Remuxes { get; private set; }
        public int Verifications { get; private set; }
        public bool RejectInspection { get; set; }

        /// <summary>Rejects a controlled capability probe without pretending to validate a container.</summary>
        public Task<MediaInspection> InspectAsync(InspectRequest request)
        {
            Inspections++;

            if (RejectInspection)
            {
                throw new OperationFailureException(new(ErrorCode.UnsupportedContainer));
            }

            return Task.FromResult(Inspection(container));
        }

        /// <summary>Counts a dispatched job without creating a process or output.</summary>
        public Task<ToolOutcome> RemuxAsync(RemuxRequest request)
        {
            Remuxes++;
            return Task.FromResult(new ToolOutcome(0, "controlled success", ToolDisposition.Success));
        }

        /// <summary>Counts a verification request without using filesystem or media evidence.</summary>
        public Task<VerificationOutcome> VerifyAsync(VerifyRequest request)
        {
            Verifications++;
            return Task.FromResult(new VerificationOutcome(true, "controlled dispatch only"));
        }
    }
}
