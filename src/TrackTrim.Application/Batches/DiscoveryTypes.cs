using System.Collections.Immutable;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Media;

namespace TrackTrim.Application.Batches;

public enum BatchInputKind { ExplicitFiles, TopLevelFolder, RecursiveFolder }

public static class BatchDiscoveryLimits
{
    public const int MaximumFiles = 100;
    public const int MaximumExaminedEntries = 10000;
    public const int MaximumDirectoryDepth = 32;
}

public sealed record BatchInputDiscoveryRequest(BatchInputKind Kind, IReadOnlyList<string> Paths,
    CancellationToken CancellationToken = default, string? OutputRoot = null);

public sealed record FolderSourceOrigin(string RootPath, string RelativePath);

public sealed record BatchDiscoveryIssue(string Path, Failure Failure);

public sealed record BatchInputDiscoveryResult(ImmutableArray<SourceRevision> Sources,
    ImmutableArray<BatchDiscoveryIssue> Issues, Failure? Rejection = null,
    ImmutableDictionary<string, FolderSourceOrigin>? FolderOrigins = null);
