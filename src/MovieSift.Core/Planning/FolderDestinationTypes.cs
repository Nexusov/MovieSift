using MovieSift.Core.Failures;

namespace MovieSift.Core.Planning;

public sealed record FolderDestinationRequest(string OutputRoot, string RelativeSourcePath);
public sealed record FolderDestinationResult(string? DestinationPath, Failure? Failure);
