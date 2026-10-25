using MovieSift.Core.Media;

namespace MovieSift.Application.Ports;

public interface IMediaBackend
{
    Task<MediaInspection> InspectAsync(InspectRequest request);
    Task<ToolOutcome> RemuxAsync(RemuxRequest request);
    Task<VerificationOutcome> VerifyAsync(VerifyRequest request);
}
