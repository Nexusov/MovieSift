using System.Collections.Immutable;
using TrackTrim.Core.Media;
using TrackTrim.Core.Rules;
using Xunit;

namespace TrackTrim.Core.Tests;

/// <summary>Checks inspection-free configuration validation and exact stored-content comparison.</summary>
public sealed class LanguageRuleConfigurationTests
{
    private static LanguageRules Configuration => new(
        new(LanguageRuleMode.Unchanged, ["fra", "deu"], new(false, "  Dormant [literal].*  ")),
        new(LanguageRuleMode.KeepMatching, ["eng", "en"], new(true, "  Captions  ")),
        KeepOnlyForcedSubtitles: true);

    /// <summary>Shares the matcher's category errors and priority without depending on inspected track metadata.</summary>
    [Theory]
    [InlineData("empty")]
    [InlineData("default")]
    [InlineData("language")]
    [InlineData("title")]
    [InlineData("mode")]
    [InlineData("missing")]
    public void ConfigurationErrorsMatchTheMatchersExistingPolicy(string invalid)
    {
        var audio = invalid switch
        {
            "empty" => new TrackLanguageRule(LanguageRuleMode.KeepMatching, []),
            "default" => new TrackLanguageRule(LanguageRuleMode.KeepMatching, default),
            "language" => new TrackLanguageRule(LanguageRuleMode.KeepMatching, ["qaa"]),
            "title" => new TrackLanguageRule(LanguageRuleMode.KeepMatching, [], new(true, " \t ")),
            "mode" => new TrackLanguageRule((LanguageRuleMode)int.MaxValue, ["en"]),
            _ => null!
        };
        var rules = new LanguageRules(audio, new(LanguageRuleMode.Unchanged, []));
        var inspection = CreateInspection();

        var validation = LanguageRuleConfiguration.Validate(rules);
        var matching = LanguageRuleMatcher.Preview(new(inspection, [0, 1, 2], rules));

        Assert.NotEmpty(validation);
        Assert.Equal(validation.ToArray(), matching.Issues.ToArray());
        Assert.All(validation, issue => Assert.Null(issue.TrackId));
        Assert.False(matching.HasChanges);
        Assert.Equal([0, 1, 2], matching.RetainedTrackIds);
    }

    /// <summary>Checks both active categories without inventing a media-dependent no-audio-match result.</summary>
    [Fact]
    public void ValidationDoesNotRequireOrFabricateAnInspection()
    {
        var rules = new LanguageRules(new(LanguageRuleMode.KeepMatching, ["ger-DE", "deu-DE"]),
            new(LanguageRuleMode.KeepMatching, ["rus"], new(true, " Captions ")), true);

        var issues = LanguageRuleConfiguration.Validate(rules);

        Assert.Empty(issues);
        Assert.Equal(["ger-DE", "deu-DE"], rules.Audio.Languages);
        Assert.Equal(" Captions ", rules.Subtitles.TitleFilter!.Text);
    }

    /// <summary>Leaves arbitrary inactive syntax uninterpreted while keeping missing active category objects invalid.</summary>
    [Fact]
    public void UnchangedSkipsInactiveValuesAndMissingRulesRemainExplicit()
    {
        var inactive = new TrackLanguageRule(LanguageRuleMode.Unchanged, [" invalid "], new(true, " "));

        Assert.Empty(LanguageRuleConfiguration.Validate(new(inactive, inactive, true)));
        var missing = LanguageRuleConfiguration.Validate(null);
        Assert.Equal(2, missing.Length);
        Assert.All(missing, issue => Assert.Equal(LanguageRuleIssueReason.InvalidRuleLanguage, issue.Reason));
        Assert.Equal([TrackKind.Audio, TrackKind.Subtitles], missing.Select(issue => issue.Category));
    }

    /// <summary>Compares independently allocated immutable arrays and records by content rather than record reference equality.</summary>
    [Fact]
    public void DeepCopyOfStoredConfigurationIsEqual()
    {
        var original = Configuration;
        var copy = original with
        {
            Audio = original.Audio with
            {
                Languages = original.Audio.Languages.ToArray().ToImmutableArray(),
                TitleFilter = original.Audio.TitleFilter! with { }
            },
            Subtitles = original.Subtitles with
            {
                Languages = original.Subtitles.Languages.ToArray().ToImmutableArray(),
                TitleFilter = original.Subtitles.TitleFilter! with { }
            }
        };

        Assert.NotSame(original.Audio, copy.Audio);
        Assert.False(original.Audio.Languages == copy.Audio.Languages);
        Assert.True(LanguageRuleConfiguration.ContentEquals(original, copy));
        Assert.True(LanguageRuleConfiguration.ContentEquals(copy, original));
    }

    /// <summary>Keeps ordered aliases, inactive values, exact title text, modes and forced preference significant.</summary>
    [Theory]
    [InlineData("audio-order")]
    [InlineData("audio-alias")]
    [InlineData("audio-mode")]
    [InlineData("inactive-title")]
    [InlineData("subtitle-order")]
    [InlineData("title-case")]
    [InlineData("title-enabled")]
    [InlineData("forced")]
    public void StoredValueChangesAreNotHiddenByMatchingEquivalence(string change)
    {
        var original = Configuration;
        var edited = change switch
        {
            "audio-order" => original with { Audio = original.Audio with { Languages = ["deu", "fra"] } },
            "audio-alias" => original with { Audio = original.Audio with { Languages = ["fre", "deu"] } },
            "audio-mode" => original with { Audio = original.Audio with { Mode = LanguageRuleMode.KeepMatching } },
            "inactive-title" => original with { Audio = original.Audio with { TitleFilter = new(false, "Different") } },
            "subtitle-order" => original with { Subtitles = original.Subtitles with { Languages = ["en", "eng"] } },
            "title-case" => original with { Subtitles = original.Subtitles with { TitleFilter = new(true, "  captions  ") } },
            "title-enabled" => original with { Subtitles = original.Subtitles with { TitleFilter = new(false, "  Captions  ") } },
            _ => original with { KeepOnlyForcedSubtitles = false }
        };

        Assert.False(LanguageRuleConfiguration.ContentEquals(original, edited));
        Assert.False(LanguageRuleConfiguration.ContentEquals(edited, original));
    }

    /// <summary>Normalizes only the absent disabled-empty title representation while retaining other nullable distinctions.</summary>
    [Fact]
    public void AbsentTitleFilterEqualsDisabledEmptyAndOtherShapesRemainDistinct()
    {
        var original = new LanguageRules(new(LanguageRuleMode.Unchanged, []), new(LanguageRuleMode.Unchanged, []));
        var explicitEmpty = original with { Audio = original.Audio with { TitleFilter = new(false, "") } };
        var inactiveText = original with { Audio = original.Audio with { TitleFilter = new(false, " ") } };
        var missingArray = original with { Audio = original.Audio with { Languages = default } };

        Assert.True(LanguageRuleConfiguration.ContentEquals(original, explicitEmpty));
        Assert.False(LanguageRuleConfiguration.ContentEquals(original, inactiveText));
        Assert.False(LanguageRuleConfiguration.ContentEquals(original, missingArray));
        Assert.True(LanguageRuleConfiguration.ContentEquals(null, null));
        Assert.False(LanguageRuleConfiguration.ContentEquals(original, null));
        Assert.False(LanguageRuleConfiguration.ContentEquals(null, original));
    }

    /// <summary>Builds a known ordinary inspection solely to compare shared validation with the existing matcher boundary.</summary>
    private static MediaInspection CreateInspection() => new(
        new("synthetic.mkv", "identity", 100, 200), new("mkvmerge", "102.0", 20),
        [Track(new(0, TrackKind.Video)), Track(new(1, TrackKind.Audio)), Track(new(2, TrackKind.Subtitles))],
        [], "Title", false, false);

    /// <summary>Creates known language metadata so the validation parity cases contain no media uncertainty.</summary>
    private static MediaTrack Track(TrackSpecification specification) => new(specification.Id, (ulong)specification.Id + 1,
        specification.Kind, "codec", "eng", "en", "Original", null, false, false, true,
        ImmutableDictionary<string, string>.Empty);

    private sealed record TrackSpecification(int Id, TrackKind Kind);
}
