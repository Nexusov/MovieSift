using TrackTrim.Core.Rules;

namespace TrackTrim.Core.Media;

/// <summary>Describes a selection group, never an assertion of shared encoded content.</summary>
public sealed record TrackGroupingKey(
    TrackKind Kind,
    string? CanonicalLanguage,
    string Title,
    string Codec,
    int? AudioChannels,
    bool IsForced,
    int SemanticDispositions);

public sealed record TrackGroupingDescription(TrackGroupingKey Key, LanguageRuleIssueReason? LanguageIssue);
