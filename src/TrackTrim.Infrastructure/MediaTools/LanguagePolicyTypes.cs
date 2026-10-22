namespace TrackTrim.Infrastructure.MediaTools;

internal sealed record LanguageComparison(string Expected, string Actual);
internal sealed record IetfLanguageComparison(string Legacy, string? ExpectedIetf, string? ActualIetf);
