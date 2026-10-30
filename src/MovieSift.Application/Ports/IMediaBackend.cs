using MovieSift.Core.Media;
using MovieSift.Core.Planning;

namespace MovieSift.Application.Ports;

public interface IMediaBackend
{
    /// <summary>Declares additional owned storage before reservation; ordinary backends need none.</summary>
    MediaWorkRequirements GetWorkRequirements(RemuxPlan plan) => new();
    /// <summary>Prepares only a journal-owned input resource while the existing coordinator owns cancellation.</summary>
    Task PrepareInputAsync(PrepareInputRequest request) => Task.CompletedTask;
    Task<MediaInspection> InspectAsync(InspectRequest request);
    Task<ToolOutcome> RemuxAsync(RemuxRequest request);
    Task<VerificationOutcome> VerifyAsync(VerifyRequest request);
}
