using System.Collections.Immutable;
using MovieSift.Application.Ports;
using MovieSift.Core.Failures;

namespace MovieSift.Application;

/// <summary>Carries a stable expected failure across an asynchronous I/O boundary.</summary>
public sealed class OperationFailureException(Failure failure, ImmutableArray<CleanupIssue> cleanupIssues = default,
    Exception? innerException = null) : Exception(failure.Detail, innerException)
{
    public Failure Failure { get; } = failure;
    public ImmutableArray<CleanupIssue> CleanupIssues { get; } = cleanupIssues.IsDefault ? [] : cleanupIssues;
}
