using MovieSift.Core.Failures;

namespace MovieSift.Application;

/// <summary>Carries a stable expected failure across an asynchronous I/O boundary.</summary>
public sealed class OperationFailureException(Failure failure) : Exception(failure.Detail)
{
    public Failure Failure { get; } = failure;
}
