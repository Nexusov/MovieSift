using TrackTrim.Application;
using TrackTrim.Application.Ports;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Media;

namespace TrackTrim.Infrastructure.MediaTools;

/// <summary>Selects an explicit adapter for inspection and the approved, frozen adapter for execution.</summary>
public sealed class ContainerMediaBackend(IMediaBackend matroska, IMediaBackend mp4) : IMediaBackend
{
    /// <summary>Uses a candidate suffix only for routing; each adapter validates the real container independently.</summary>
    public Task<MediaInspection> InspectAsync(InspectRequest request)
    {
        var container = MediaContainerPolicy.FromExtension(request.SourcePath);

        if (container is null)
        {
            throw new OperationFailureException(new(ErrorCode.UnsupportedContainer, "Choose an MKV or MP4 candidate."));
        }

        return Select(container.Value).InspectAsync(request);
    }

    /// <summary>Never changes adapters in response to a filename or tool failure after approval.</summary>
    public Task<ToolOutcome> RemuxAsync(RemuxRequest request) => Select(request.Plan.Inspection.Container).RemuxAsync(request);

    /// <summary>Verifies the candidate with the same format adapter captured by the approved inspection.</summary>
    public Task<VerificationOutcome> VerifyAsync(VerifyRequest request) => Select(request.Plan.Inspection.Container).VerifyAsync(request);

    /// <summary>Rejects unknown container identities instead of silently falling back to another backend.</summary>
    private IMediaBackend Select(MediaContainer container) => container switch
    {
        MediaContainer.Matroska => matroska,
        MediaContainer.Mp4 => mp4,
        _ => throw new OperationFailureException(new(ErrorCode.UnsupportedContainer)),
    };
}
