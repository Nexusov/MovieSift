using System.Collections.Immutable;
using TrackTrim.Core.Media;
using TrackTrim.Core.Rules;
using Xunit;

namespace TrackTrim.Core.Tests;

/// <summary>Checks conservative metadata grouping independently from media content and local identifiers.</summary>
public sealed class TrackGroupingPolicyTests
{
    /// <summary>Normalizes exact alias identity and whitespace only, ignoring IDs and default/enabled details.</summary>
    [Fact]
    public void ExactAliasesGroupWithoutDependingOnLocalIdsOrDefaultFlags()
    {
        var first = Track() with { Title = " Main dub ", Language = "ger", LanguageIetf = "de-DE" };
        var second = first with
        {
            Id = 77,
            Uid = 991,
            Language = "deu",
            LanguageIetf = "DE-de",
            Title = "Main dub",
            IsDefault = false,
            IsEnabled = false
        };

        Assert.Equal(TrackGroupingPolicy.Describe(first), TrackGroupingPolicy.Describe(second));
        Assert.Equal("deu-de", TrackGroupingPolicy.Describe(first).Key.CanonicalLanguage);
    }

    /// <summary>Keeps meaningful title text, exact tags, codecs, channels, forced and semantic roles distinct.</summary>
    [Fact]
    public void DifferentMetadataDoesNotBecomeOneSemanticGroup()
    {
        var original = Track();
        MediaTrack[] distinct = [original with { Title = "main" }, original with { Title = "Main (studio)" },
            original with { LanguageIetf = "en-US" }, original with { Codec = "other" },
            original with { Channels = 6 }, original with { IsForced = true },
            original with { Dispositions = [TrackDisposition.Commentary] },
            original with { Dispositions = [TrackDisposition.HearingImpaired] }];
        var key = TrackGroupingPolicy.Describe(original).Key;

        Assert.All(distinct, track => Assert.NotEqual(key, TrackGroupingPolicy.Describe(track).Key));
        Assert.NotEqual(TrackGroupingPolicy.Describe(original with { Language = "srp", LanguageIetf = "sr-Cyrl" }).Key,
            TrackGroupingPolicy.Describe(original with { Language = "srp", LanguageIetf = "sr-Latn" }).Key);
    }

    /// <summary>Uses semantic disposition membership rather than array order or duplicate flags.</summary>
    [Fact]
    public void SemanticDispositionSetHasDeterministicEquality()
    {
        var first = Track() with { Dispositions = [TrackDisposition.Original, TrackDisposition.Commentary] };
        var second = first with { Dispositions = [TrackDisposition.Commentary, TrackDisposition.Original, TrackDisposition.Original] };
        Assert.Equal(TrackGroupingPolicy.Describe(first), TrackGroupingPolicy.Describe(second));
    }

    /// <summary>Exposes unknown/conflicting language rather than assigning it a known matching identity.</summary>
    [Theory]
    [InlineData("und", null, LanguageRuleIssueReason.UnknownLanguage)]
    [InlineData("eng", "ru", LanguageRuleIssueReason.ConflictingLanguage)]
    [InlineData("eng", "en-private-invalid-extra", LanguageRuleIssueReason.MalformedLanguage)]
    public void UnreliableLanguageIsExplicit(string language, string? tag, LanguageRuleIssueReason reason)
    {
        var result = TrackGroupingPolicy.Describe(Track() with { Language = language, LanguageIetf = tag });
        Assert.Null(result.Key.CanonicalLanguage);
        Assert.Equal(reason, result.LanguageIssue);
    }

    /// <summary>Provides an ordinary track with no filesystem or backend effects.</summary>
    private static MediaTrack Track() => new(1, 2, TrackKind.Audio, "AAC", "eng", "en", "Main", 2,
        true, false, true, ImmutableDictionary<string, string>.Empty);
}
