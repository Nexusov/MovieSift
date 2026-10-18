namespace TrackTrim.Infrastructure.Processes;

internal sealed record ProcessRequest(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    int MaximumOutputCharacters,
    TimeSpan? Timeout,
    Action<string>? StandardOutputLine,
    CancellationToken CancellationToken,
    IReadOnlyDictionary<string, string>? Environment = null);

internal sealed record ProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool OutputTruncated);

/// <summary>Signals that safe process termination could not be established.</summary>
public sealed class ProcessTerminationException(string message, Exception innerException)
    : Exception(message, innerException);
