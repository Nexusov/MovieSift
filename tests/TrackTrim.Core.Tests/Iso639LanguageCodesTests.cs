using TrackTrim.Core.Languages;
using Xunit;

namespace TrackTrim.Core.Tests;

/// <summary>Checks the shared explicit registry without expanding the preservation verifier's alias policy.</summary>
public sealed class Iso639LanguageCodesTests
{
    /// <summary>Maps supported B/T and alpha-2 aliases case-insensitively to their explicit registry group.</summary>
    [Theory]
    [InlineData("GER", "deu")]
    [InlineData("deu", "deu")]
    [InlineData("de", "deu")]
    [InlineData("fre", "fra")]
    [InlineData("FR", "fra")]
    [InlineData("ru", "rus")]
    [InlineData("zxx", "zxx")]
    public void KnownPlainAliasesResolve(string value, string expected)
    {
        Assert.True(Iso639LanguageCodes.TryCanonicalPlainCode(value, out var canonical));
        Assert.Equal(expected, canonical);
    }

    /// <summary>Leaves suffix interpretation and unknown/private values outside the plain-code lookup.</summary>
    [Theory]
    [InlineData("de-DE")]
    [InlineData("qaa")]
    [InlineData("x-private")]
    [InlineData("zzz")]
    [InlineData(" de")]
    public void NonPlainOrUnregisteredValuesDoNotResolve(string value)
    {
        Assert.False(Iso639LanguageCodes.TryCanonicalPlainCode(value, out _));
    }
}
