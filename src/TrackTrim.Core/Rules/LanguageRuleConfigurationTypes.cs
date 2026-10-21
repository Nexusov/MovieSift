using System.Collections.Immutable;
using TrackTrim.Core.Media;

namespace TrackTrim.Core.Rules;

internal sealed record LanguageRuleConfigurationRequest(TrackKind Category, TrackLanguageRule? Rule);
internal sealed record ValidatedLanguageRule(
    ImmutableArray<string> CanonicalLanguages,
    string? TitleSearch,
    ImmutableArray<LanguageRuleIssue> Issues);
internal sealed record LanguageRuleContentComparison(TrackLanguageRule? First, TrackLanguageRule? Second);
