using TrackTrim.Core.Languages;

namespace TrackTrim.Core.Rules;

/// <summary>Parses the explicit primary[-Script][-REGION] matching subset without rewriting inspected values.</summary>
internal static class LanguageRuleTagParser
{
    private const int ShortLanguageCodeLength = 2;
    private const int LongLanguageCodeLength = 3;
    private const int ScriptLength = 4;
    private const int AlphabeticRegionLength = 2;
    private const int NumericRegionLength = 3;
    private const int MaximumSubtags = 3;

    /// <summary>Rejects missing, special, private-use and unregistered plain legacy codes.</summary>
    internal static ParsedLanguageTag ParsePlain(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new(null, null, LanguageRuleIssueReason.UnknownLanguage, value);
        }

        var hasPlainSyntax = value.Length is ShortLanguageCodeLength or LongLanguageCodeLength && IsAsciiLetters(value);

        if (!hasPlainSyntax)
        {
            return new(null, null, LanguageRuleIssueReason.MalformedLanguage, value);
        }

        var known = Iso639LanguageCodes.TryCanonicalPlainCode(value, out var canonical);
        var cannotSelectLanguage = canonical is "und" or "mul" or "mis" or "zxx";

        if (!known || cannotSelectLanguage)
        {
            return new(null, null, LanguageRuleIssueReason.UnknownLanguage, value);
        }

        return new(canonical, canonical, null);
    }

    /// <summary>Accepts only an optional script followed by an optional alphabetic or numeric region.</summary>
    internal static ParsedLanguageTag ParseTag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new(null, null, LanguageRuleIssueReason.UnknownLanguage, value);
        }

        var subtags = value.Split('-');

        if (subtags[0].Equals("x", StringComparison.OrdinalIgnoreCase))
        {
            return new(null, null, LanguageRuleIssueReason.UnknownLanguage, value);
        }

        var primary = ParsePlain(subtags[0]);

        if (primary.Issue is not null)
        {
            return primary with { Value = value };
        }

        if (subtags.Length > MaximumSubtags)
        {
            return new(null, null, LanguageRuleIssueReason.MalformedLanguage, value);
        }

        var nextSubtag = 1;

        if (nextSubtag < subtags.Length && IsScript(subtags[nextSubtag]))
        {
            nextSubtag++;
        }

        if (nextSubtag < subtags.Length && IsRegion(subtags[nextSubtag]))
        {
            nextSubtag++;
        }

        if (nextSubtag != subtags.Length)
        {
            return new(null, null, LanguageRuleIssueReason.MalformedLanguage, value);
        }

        var separator = value.IndexOf('-');
        var canonical = separator < 0 ? primary.Primary : primary.Primary + value[separator..].ToLowerInvariant();
        return new(primary.Primary, canonical, null);
    }

    /// <summary>Recognizes the supported script subtag shape without claiming registry validation.</summary>
    private static bool IsScript(string value) => value.Length == ScriptLength && IsAsciiLetters(value);

    /// <summary>Recognizes the supported region shapes while preserving their exact identity.</summary>
    private static bool IsRegion(string value) =>
        value.Length == AlphabeticRegionLength && IsAsciiLetters(value) ||
        value.Length == NumericRegionLength && value.All(char.IsAsciiDigit);

    /// <summary>Excludes Unicode lookalikes and separators from the narrow tag grammar.</summary>
    private static bool IsAsciiLetters(string value) => value.All(char.IsAsciiLetter);
}
