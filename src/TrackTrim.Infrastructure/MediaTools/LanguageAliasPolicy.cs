using TrackTrim.Core.Languages;

namespace TrackTrim.Infrastructure.MediaTools;

/// <summary>Permits explicit ISO language aliases while retaining exact regional/script/private distinctions.</summary>
internal static class LanguageAliasPolicy
{
    /// <summary>Compares known primary-code aliases and preserves every suffix or unknown value exactly.</summary>
    internal static bool Equivalent(LanguageComparison request)
    {
        return Canonicalize(request.Expected) == Canonicalize(request.Actual);
    }

    /// <summary>Allows a new IETF field only for a registered equivalent plain legacy language code.</summary>
    internal static bool EquivalentPlainCodes(LanguageComparison request)
    {
        var expectedKnown = Iso639LanguageCodes.TryCanonicalPlainCode(request.Expected, out var expected);
        var actualKnown = Iso639LanguageCodes.TryCanonicalPlainCode(request.Actual, out var actual);
        return expectedKnown && actualKnown && expected == actual;
    }

    /// <summary>Preserves existing IETF distinctions and validates a tool-generated equivalent base-code addition.</summary>
    internal static bool MatchesIetf(IetfLanguageComparison request)
    {

        if (request.ExpectedIetf is not null)
        {
            return request.ActualIetf is not null && Equivalent(new LanguageComparison(request.ExpectedIetf, request.ActualIetf));
        }

        if (request.ActualIetf is null)
        {
            return true;
        }

        return EquivalentPlainCodes(new LanguageComparison(request.Legacy, request.ActualIetf));
    }

    /// <summary>Canonicalizes only registered primary-code aliases in a comparison copy.</summary>
    internal static string Canonicalize(string value)
    {
        var separator = value.IndexOf('-');
        var primary = separator < 0 ? value : value[..separator];

        if (!Iso639LanguageCodes.TryCanonicalPlainCode(primary, out var canonical))
        {
            return value;
        }

        return separator < 0 ? canonical : canonical + value[separator..];
    }
}
