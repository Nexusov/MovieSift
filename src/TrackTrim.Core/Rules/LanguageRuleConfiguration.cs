using System.Collections.Immutable;
using System.Globalization;
using TrackTrim.Core.Media;

namespace TrackTrim.Core.Rules;

/// <summary>Validates reusable active settings and compares their stored content independently of media inspection.</summary>
public static class LanguageRuleConfiguration
{
    /// <summary>Uses the same active category policy as matching without fabricating media or inspecting inactive values.</summary>
    public static ImmutableArray<LanguageRuleIssue> Validate(LanguageRules? rules)
    {
        var audio = ValidateCategory(new(TrackKind.Audio, rules?.Audio));
        var subtitles = ValidateCategory(new(TrackKind.Subtitles, rules?.Subtitles));
        return [.. audio.Issues, .. subtitles.Issues];
    }

    /// <summary>Compares ordered raw values while treating an absent title filter as disabled with empty text.</summary>
    public static bool ContentEquals(LanguageRules? first, LanguageRules? second)
    {
        if (ReferenceEquals(first, second))
        {
            return true;
        }

        if (first is null || second is null)
        {
            return false;
        }

        var sameAudio = CategoryContentEquals(new(first.Audio, second.Audio));
        var sameSubtitles = CategoryContentEquals(new(first.Subtitles, second.Subtitles));
        var sameContent = sameAudio && sameSubtitles && first.KeepOnlyForcedSubtitles == second.KeepOnlyForcedSubtitles;
        return sameContent;
    }

    /// <summary>Returns comparison-only language aliases and trimmed title text after validating one active category.</summary>
    internal static ValidatedLanguageRule ValidateCategory(LanguageRuleConfigurationRequest request)
    {
        var rule = request.Rule;

        if (rule is null)
        {
            return new([], null, [new(request.Category, null, LanguageRuleIssueReason.InvalidRuleLanguage)]);
        }

        if (rule.Mode == LanguageRuleMode.Unchanged)
        {
            return new([], null, []);
        }

        if (rule.Mode != LanguageRuleMode.KeepMatching)
        {
            return new([], null, [new(request.Category, null, LanguageRuleIssueReason.InvalidRuleLanguage,
                ((int)rule.Mode).ToString(CultureInfo.InvariantCulture))]);
        }

        var hasTitleFilter = rule.TitleFilter is { Enabled: true };
        var titleSearch = hasTitleFilter ? rule.TitleFilter?.Text?.Trim() : null;

        if (hasTitleFilter && string.IsNullOrEmpty(titleSearch))
        {
            return new([], null, [new(request.Category, null, LanguageRuleIssueReason.EmptyTitleFilter, rule.TitleFilter?.Text)]);
        }

        if (rule.Languages.IsDefaultOrEmpty)
        {
            return new([], titleSearch, [new(request.Category, null, LanguageRuleIssueReason.EmptyLanguageSelection)]);
        }

        var languages = ImmutableArray.CreateBuilder<string>(rule.Languages.Length);
        var issues = ImmutableArray.CreateBuilder<LanguageRuleIssue>();

        foreach (var value in rule.Languages)
        {
            var parsed = LanguageRuleTagParser.ParseTag(value);

            if (parsed.Issue is not null)
            {
                issues.Add(new(request.Category, null, LanguageRuleIssueReason.InvalidRuleLanguage, value));
                continue;
            }

            languages.Add(parsed.Tag!);
        }

        return new(languages.ToImmutable(), titleSearch, issues.ToImmutable());
    }

    /// <summary>Keeps inactive values and alias ordering significant rather than comparing normalized matching behavior.</summary>
    private static bool CategoryContentEquals(LanguageRuleContentComparison request)
    {
        var first = request.First;
        var second = request.Second;

        if (ReferenceEquals(first, second))
        {
            return true;
        }

        if (first is null || second is null)
        {
            return false;
        }

        if (first.Languages.IsDefault != second.Languages.IsDefault)
        {
            return false;
        }

        var sameLanguages = first.Languages.IsDefault || first.Languages.SequenceEqual(second.Languages, StringComparer.Ordinal);
        var firstTitle = first.TitleFilter is { } firstFilter ? firstFilter.Text : string.Empty;
        var secondTitle = second.TitleFilter is { } secondFilter ? secondFilter.Text : string.Empty;
        var sameTitleText = string.Equals(firstTitle, secondTitle, StringComparison.Ordinal);
        var sameTitleEnabled = (first.TitleFilter?.Enabled ?? false) == (second.TitleFilter?.Enabled ?? false);
        var sameContent = first.Mode == second.Mode && sameLanguages && sameTitleText && sameTitleEnabled;
        return sameContent;
    }
}
