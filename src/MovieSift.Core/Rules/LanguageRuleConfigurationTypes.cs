using System.Collections.Immutable;
using MovieSift.Core.Media;

namespace MovieSift.Core.Rules;

internal sealed record LanguageRuleConfigurationRequest(TrackKind Category, TrackLanguageRule? Rule);
internal sealed record ValidatedLanguageRule(
    ImmutableArray<string> CanonicalLanguages,
    string? TitleSearch,
    ImmutableArray<LanguageRuleIssue> Issues);
internal sealed record LanguageRuleContentComparison(TrackLanguageRule? First, TrackLanguageRule? Second);
