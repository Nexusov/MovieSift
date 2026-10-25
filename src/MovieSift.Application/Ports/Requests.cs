using MovieSift.Core.Media;
using MovieSift.Core.Planning;

namespace MovieSift.Application.Ports;

public sealed record InspectRequest(string SourcePath, CancellationToken CancellationToken = default);
public sealed record SourceRequest(string Path, CancellationToken CancellationToken = default);
public sealed record RemuxRequest(RemuxPlan Plan, string CandidatePath, IProgress<double>? Progress, CancellationToken CancellationToken = default);
public sealed record VerifyRequest(RemuxPlan Plan, string CandidatePath, CancellationToken CancellationToken = default);
public sealed record ValidateDestinationRequest(SourceRevision Source, string DestinationPath, long AdditionalAllocationBytes,
    CancellationToken CancellationToken = default, string? OutputDirectoryRoot = null,
    MediaContainer Container = MediaContainer.Matroska);
public sealed record ReserveOutputRequest(RemuxPlan Plan, CancellationToken CancellationToken = default);
public sealed record OutputRequest(OwnedOutput Output, CancellationToken CancellationToken = default);
public sealed record SaveLanguageRequest(string Language, CancellationToken CancellationToken = default);

public enum ToolDisposition { Success, WarningReview, Failure }

public sealed record ToolOutcome(int ExitCode, string Diagnostics, ToolDisposition? Disposition = null)
{
    // Legacy callers describe MKVToolNix; production backends supply their own normalized disposition.
    public ToolDisposition EffectiveDisposition => Disposition ?? ExitCode switch
    {
        0 => ToolDisposition.Success,
        1 => ToolDisposition.WarningReview,
        _ => ToolDisposition.Failure
    };
}
public sealed record VerificationOutcome(bool Passed, string Details);
public sealed record OwnedOutput(Guid JobId, string DirectoryPath, string CandidatePath, string DestinationPath, string OwnershipToken,
    MediaContainer Container = MediaContainer.Matroska);
public sealed record InterruptedOutput(OwnedOutput? Output, string Stage, bool CanClean, string? Issue = null);
public sealed record CleanupIssue(string Path, string Detail);
public sealed record PublishedOutput(string Path, long Length);
