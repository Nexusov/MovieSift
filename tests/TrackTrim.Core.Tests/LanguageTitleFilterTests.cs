using System.Collections.Immutable;
using System.Globalization;
using TrackTrim.Core.Media;
using TrackTrim.Core.Rules;
using Xunit;

namespace TrackTrim.Core.Tests;

/// <summary>Checks the optional literal title condition without relaxing independent language validation.</summary>
public sealed class LanguageTitleFilterTests
{
    private static readonly TrackLanguageRule Unchanged = new(LanguageRuleMode.Unchanged, []);
    private static readonly MediaInspection Inspection = Inspect([
        Track(new(7, TrackKind.Video, "und", null, "Required video")),
        Track(new(3, TrackKind.Audio, "eng", "en", "English commentary [2.0]")),
        Track(new(15, TrackKind.Audio, "eng", "en", "ENGLISH COMMENTARY [5.1]")),
        Track(new(8, TrackKind.Audio, "rus", "ru", "Russian commentary")),
        Track(new(21, TrackKind.Audio, "eng", "en", "English main")),
        Track(new(44, TrackKind.Subtitles, "eng", "en", "English SDH subtitles")),
        Track(new(9, TrackKind.Subtitles, "rus", "ru", "Русские субтитры")),
        Track(new(80, TrackKind.Video, "und", null, ""))]);

    /// <summary>Keeps every language-and-title match while retaining video and untargeted current choices.</summary>
    [Fact]
    public void AudioUsesLiteralAndAndKeepsEveryMatchInSourceOrder()
    {
        ImmutableArray<int> current = [7, 3, 15, 8, 21, 9, 80];
        var result = LanguageRuleMatcher.Preview(new(Inspection, current, new(Keep(new("en", "commentary")), Unchanged)));

        Assert.Empty(result.Issues);
        Assert.Equal([7, 3, 15, 9, 80], result.RetainedTrackIds);
        Assert.True(result.HasChanges);
        Assert.False(result.NoSubtitleMatches);
        Assert.Equal([7, 3, 15, 8, 21, 9, 80], current);
    }

    /// <summary>Applies the same all-match semantics to subtitles without touching any audio choice.</summary>
    [Fact]
    public void SubtitleConditionKeepsAllMatchingTracks()
    {
        var inspection = Inspection with
        {
            Tracks = Inspection.Tracks.Insert(6, Track(new(52, TrackKind.Subtitles, "eng", "en", "SDH [HI]")))
        };
        var result = LanguageRuleMatcher.Preview(new(inspection, Identifiers(inspection), new(Unchanged, Keep(new("eng", "sdh")))));

        Assert.Empty(result.Issues);
        Assert.Equal([7, 3, 15, 8, 21, 44, 52, 80], result.RetainedTrackIds);
        Assert.False(result.NoSubtitleMatches);
    }

    /// <summary>Treats punctuation as literal text and normalizes neither the title nor Unicode composition.</summary>
    [Theory]
    [InlineData("[SDH]", "English [sdh] subtitles", true)]
    [InlineData("[SDH]", "English S subtitles", false)]
    [InlineData("a.*", "English a.* dub", true)]
    [InlineData("a.*", "English arbitrary dub", false)]
    [InlineData("^main$", "^MAIN$", true)]
    [InlineData("^main$", "main", false)]
    [InlineData("главный", "ГЛАВНЫЙ РУССКИЙ", true)]
    [InlineData("  commentary  ", "  Commentary [5.1]  ", true)]
    [InlineData("e\u0301", "\u00e9", false)]
    public void TitleMatchingIsLiteralOrdinalIgnoreCase(string search, string title, bool matches)
    {
        var inspection = Inspect([Track(new(0, TrackKind.Video)), Track(new(1, TrackKind.Audio, Title: title)),
            Track(new(2, TrackKind.Audio, Title: "unrelated"))]);
        var result = LanguageRuleMatcher.Preview(new(inspection, Identifiers(inspection), new(Keep(new("en", search)), Unchanged)));

        if (matches)
        {
            Assert.Empty(result.Issues);
            Assert.Equal([0, 1], result.RetainedTrackIds);
            return;
        }

        Assert.Equal(LanguageRuleIssueReason.NoAudioMatches, Assert.Single(result.Issues).Reason);
        Assert.Equal([0, 1, 2], result.RetainedTrackIds);
        Assert.False(result.HasChanges);
    }

    /// <summary>Preserves Latin and Cyrillic ordinal casing semantics under a Turkish current culture.</summary>
    [Theory]
    [InlineData("FILE Commentary", "file")]
    [InlineData("Русская ГЛАВНАЯ", "главная")]
    public void TitleComparisonDoesNotUseCurrentCulture(string title, string search)
    {
        var originalCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var inspection = Inspect([Track(new(0, TrackKind.Video)), Track(new(1, TrackKind.Audio, Title: title))]);
            var result = LanguageRuleMatcher.Preview(new(inspection, [0, 1], new(Keep(new("en", search)), Unchanged)));

            Assert.Empty(result.Issues);
            Assert.False(result.HasChanges);
            Assert.Equal([0, 1], result.RetainedTrackIds);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    /// <summary>Missing and blank subtitle titles are nonmatches rather than unknown-language issues.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void BlankTrackTitlesDoNotMatch(string? title)
    {
        var inspection = Inspect([Track(new(0, TrackKind.Video)), Track(new(1, TrackKind.Subtitles, Title: title))]);
        var result = LanguageRuleMatcher.Preview(new(inspection, [0, 1], new(Unchanged, Keep(new("en", "captions")))));

        Assert.Empty(result.Issues);
        Assert.Equal([0], result.RetainedTrackIds);
        Assert.True(result.NoSubtitleMatches);
    }

    /// <summary>Empty enabled search text requires review and prevents every category's proposed edit.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public void EnabledEmptyTitleFilterKeepsTheWholeFileUnchanged(string? text)
    {
        var current = Identifiers(Inspection);
        var audio = new TrackLanguageRule(LanguageRuleMode.KeepMatching, ["en"], new(true, text!));
        var result = LanguageRuleMatcher.Preview(new(Inspection, current, new(audio, Keep(new("eng", "SDH")))));
        var issue = Assert.Single(result.Issues);

        Assert.Equal(LanguageRuleIssueReason.EmptyTitleFilter, issue.Reason);
        Assert.Equal(TrackKind.Audio, issue.Category);
        Assert.Null(issue.TrackId);
        Assert.Equal(text, issue.Value);
        Assert.True(current == result.RetainedTrackIds);
        Assert.False(result.HasChanges);
        Assert.False(result.NoSubtitleMatches);
    }

    /// <summary>Disabled filters retain the language-only behavior even when their unused text is empty or unmatched.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("will not match any title")]
    public void DisabledTitleFilterDoesNotRestrictLanguageMatches(string text)
    {
        var audio = new TrackLanguageRule(LanguageRuleMode.KeepMatching, ["en"], new(false, text));
        var result = LanguageRuleMatcher.Preview(new(Inspection, Identifiers(Inspection), new(audio, Unchanged)));

        Assert.Empty(result.Issues);
        Assert.Equal([7, 3, 15, 21, 44, 9, 80], result.RetainedTrackIds);
    }

    /// <summary>Unchanged mode ignores title configuration and uncertain metadata in its untargeted category.</summary>
    [Fact]
    public void UnchangedCategoryIgnoresEnabledEmptyTitleFilter()
    {
        var inspection = Inspection with
        {
            Tracks = Inspection.Tracks.SetItem(1, Inspection.Tracks[1] with { Language = "und", LanguageIetf = null })
        };
        var unchangedAudio = new TrackLanguageRule(LanguageRuleMode.Unchanged, ["invalid"], new(true, " "));
        var result = LanguageRuleMatcher.Preview(new(inspection, Identifiers(inspection), new(unchangedAudio, Keep(new("en", "SDH")))));

        Assert.Empty(result.Issues);
        Assert.Equal([7, 3, 15, 8, 21, 44, 80], result.RetainedTrackIds);
    }

    /// <summary>Reads every targeted language even when a title mismatch would otherwise hide uncertain metadata.</summary>
    [Theory]
    [InlineData("und", null, LanguageRuleIssueReason.UnknownLanguage)]
    [InlineData("eng", "ru", LanguageRuleIssueReason.ConflictingLanguage)]
    [InlineData("eng", "en_US", LanguageRuleIssueReason.MalformedLanguage)]
    public void TitleMismatchCannotHideTargetLanguageIssues(string legacy, string? ietf, LanguageRuleIssueReason reason)
    {
        var inspection = Inspection with
        {
            Tracks = Inspection.Tracks.SetItem(3, Inspection.Tracks[3] with
            {
                Language = legacy,
                LanguageIetf = ietf,
                Title = "does not contain the requested phrase"
            })
        };
        var current = Identifiers(inspection);
        var rules = new LanguageRules(Keep(new("en", "commentary")), Keep(new("en", "SDH")));
        var result = LanguageRuleMatcher.Preview(new(inspection, current, rules));
        var issue = Assert.Single(result.Issues);

        Assert.Equal(8, issue.TrackId);
        Assert.Equal(reason, issue.Reason);
        Assert.True(current == result.RetainedTrackIds);
        Assert.False(result.HasChanges);
    }

    /// <summary>Protects an existing soundtrack when language matches but the added title condition matches no audio.</summary>
    [Fact]
    public void NoCombinedAudioMatchesRequiresWholeFileManualReview()
    {
        var current = Identifiers(Inspection);
        var result = LanguageRuleMatcher.Preview(new(Inspection, current,
            new(Keep(new("en", "missing phrase")), Keep(new("en", "SDH")))));

        Assert.Equal(LanguageRuleIssueReason.NoAudioMatches, Assert.Single(result.Issues).Reason);
        Assert.True(current == result.RetainedTrackIds);
        Assert.False(result.HasChanges);
    }

    /// <summary>Keeps silent input valid and explicitly reports a subtitle no-match after applying the title condition.</summary>
    [Fact]
    public void SilentInputAndNoSubtitleMatchesRemainValid()
    {
        var inspection = Inspect([Track(new(0, TrackKind.Video)), Track(new(1, TrackKind.Subtitles, Title: "English captions"))]);
        var result = LanguageRuleMatcher.Preview(new(inspection, [0, 1],
            new(Keep(new("en", "main")), Keep(new("en", "forced")))));

        Assert.Empty(result.Issues);
        Assert.Equal([0], result.RetainedTrackIds);
        Assert.True(result.HasChanges);
        Assert.True(result.NoSubtitleMatches);
    }

    /// <summary>Uses each inspection's title and local IDs independently after track reordering.</summary>
    [Fact]
    public void ReorderedFileKeepsItsOwnMatchingIdentifiers()
    {
        var reordered = Inspect([
            Inspection.Tracks[6] with { Id = 5 }, Inspection.Tracks[2] with { Id = 72 },
            Inspection.Tracks[0] with { Id = 40 }, Inspection.Tracks[3] with { Id = 2 },
            Inspection.Tracks[1] with { Id = 90 }, Inspection.Tracks[5] with { Id = 11 },
            Inspection.Tracks[4] with { Id = 13 }, Inspection.Tracks[7] with { Id = 54 }]);
        var rules = new LanguageRules(Keep(new("en", "commentary")), Unchanged);
        var first = LanguageRuleMatcher.Preview(new(Inspection, Identifiers(Inspection), rules));
        var second = LanguageRuleMatcher.Preview(new(reordered, Identifiers(reordered), rules));

        Assert.Empty(first.Issues);
        Assert.Empty(second.Issues);
        Assert.Equal([7, 3, 15, 44, 9, 80], first.RetainedTrackIds);
        Assert.Equal([5, 72, 40, 90, 11, 54], second.RetainedTrackIds);
    }

    /// <summary>Trims only the comparison search while preserving configuration, raw titles and no-op selection identity.</summary>
    [Fact]
    public void PreviewDoesNotRewriteRawTitleOrFilterText()
    {
        const string originalTitle = "  Commentary [5.1]  ";
        const string originalSearch = "  COMMENTARY  ";
        var inspection = Inspect([Track(new(0, TrackKind.Video)), Track(new(1, TrackKind.Audio, Title: originalTitle))]);
        ImmutableArray<int> current = [1, 0];
        var audio = Keep(new("en", originalSearch));
        var result = LanguageRuleMatcher.Preview(new(inspection, current, new(audio, Unchanged)));

        Assert.Empty(result.Issues);
        Assert.False(result.HasChanges);
        Assert.True(current == result.RetainedTrackIds);
        Assert.Equal(originalTitle, inspection.Tracks[1].Title);
        Assert.Equal(originalSearch, audio.TitleFilter!.Text);
    }

    /// <summary>Creates one enabled literal title condition alongside a language selection.</summary>
    private static TrackLanguageRule Keep(TitleRuleRequest request) => new(
        LanguageRuleMode.KeepMatching, [request.Language], new(true, request.Text));

    /// <summary>Builds an immutable inspection without media tools or filesystem access.</summary>
    private static MediaInspection Inspect(ImmutableArray<MediaTrack> tracks) => new(
        new("source.mkv", "identity", 100, 200), new("mkvmerge", "102.0", 20), tracks, [], "Title", false, false);

    /// <summary>Captures actual inspected identifiers instead of assuming contiguous positions.</summary>
    private static ImmutableArray<int> Identifiers(MediaInspection inspection) => inspection.Tracks.Select(track => track.Id).ToImmutableArray();

    /// <summary>Preserves supplied raw metadata, including missing titles in explicit failure cases.</summary>
    private static MediaTrack Track(TrackSpecification specification) => new(
        specification.Id, (ulong)specification.Id + 1, specification.Kind, "codec", specification.Legacy,
        specification.Ietf, specification.Title!, null, false, false, true, ImmutableDictionary<string, string>.Empty);

    private sealed record TrackSpecification(int Id, TrackKind Kind, string Legacy = "eng", string? Ietf = "en", string? Title = "Track");
    private sealed record TitleRuleRequest(string Language, string Text);
}
