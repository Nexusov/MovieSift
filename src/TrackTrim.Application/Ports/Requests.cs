using TrackTrim.Core.Media;
using TrackTrim.Core.Planning;

namespace TrackTrim.Application.Ports;

public sealed record InspectRequest(string SourcePath, CancellationToken CancellationToken = default);
public sealed record SourceRequest(string Path, CancellationToken CancellationToken = default);
public sealed record RemuxRequest(RemuxPlan Plan, string CandidatePath, IProgress<double>? Progress, CancellationToken CancellationToken = default);
public sealed record VerifyRequest(RemuxPlan Plan, string CandidatePath, CancellationToken CancellationToken = default);
public sealed record ValidateDestinationRequest(SourceRevision Source, string DestinationPath, CancellationToken CancellationToken = default);
public sealed record ReserveOutputRequest(RemuxPlan Plan, CancellationToken CancellationToken = default);
public sealed record OutputRequest(OwnedOutput Output, CancellationToken CancellationToken = default);
public sealed record SaveLanguageRequest(string Language, CancellationToken CancellationToken = default);

public sealed record ToolOutcome(int ExitCode, string Diagnostics);
public sealed record VerificationOutcome(bool Passed, string Details);
public sealed record OwnedOutput(Guid JobId, string DirectoryPath, string CandidatePath, string DestinationPath, string OwnershipToken);
public sealed record InterruptedOutput(OwnedOutput? Output, string Stage, bool CanClean, string? Issue = null);
public sealed record CleanupIssue(string Path, string Detail);
public sealed record PublishedOutput(string Path, long Length);
