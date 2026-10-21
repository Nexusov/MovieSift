using TrackTrim.Core.Failures;

namespace TrackTrim.Core.Planning;

public sealed record FolderDestinationRequest(string OutputRoot, string RelativeSourcePath);
public sealed record FolderDestinationResult(string? DestinationPath, Failure? Failure);
