using System.Collections.Immutable;
using TrackTrim.Core.Media;
using TrackTrim.Core.Rules;
using Xunit;

namespace TrackTrim.Core.Tests;

/// <summary>Exercises per-file matching, conservative metadata review and immutable selection proposals without I/O.</summary>
public sealed class LanguageRuleMatcherTests
{
    private static readonly TrackLanguageRule Unchanged = new(LanguageRuleMode.Unchanged, []);
    private static readonly MediaInspection Inspection = Inspect([
        Track(new(7, TrackKind.Video, "und", null)),
        Track(new(23, TrackKind.Audio, "eng", "en")),
        Track(new(41, TrackKind.Audio, "ger", "de")),
        Track(new(12, TrackKind.Audio, "deu", "de")),
        Track(new(99, TrackKind.Subtitles, "rus", "ru")),
        Track(new(3, TrackKind.Subtitles, "eng", "en")),
        Track(new(88, TrackKind.Video, "und", "unsupported-video-tag"))]);

    /// <summary>Retains every alias match and every video while leaving untargeted subtitle choices intact.</summary>
    [Theory]
    [InlineData("de")]
    [InlineData("DE")]
    [InlineData("deu")]
    [InlineData("ger")]
    [InlineData("GER")]
    public void KeepsAllMatchingAudioTracksAndUntargetedChoices(string language)
    {
        ImmutableArray<int> current = [7, 23, 41, 12, 99, 88];
        var result = LanguageRuleMatcher.Preview(new(Inspection, current, new(Keep(language), Unchanged)));

        Assert.Empty(result.Issues);
        Assert.True(result.HasChanges);
        Assert.False(result.NoSubtitleMatches);
        Assert.Equal([7, 41, 12, 99, 88], result.RetainedTrackIds);
        Assert.Equal([7, 23, 41, 12, 99, 88], current);
    }

    /// <summary>Resolves each file's actual identifiers and order instead of reusing a previous episode's positions.</summary>
    [Fact]
    public void ReorderedInspectionUsesItsOwnTrackIdentifiers()
    {
        var reordered = Inspect([
            Inspection.Tracks[5] with { Id = 61 }, Inspection.Tracks[3] with { Id = 5 },
            Inspection.Tracks[0] with { Id = 33 }, Inspection.Tracks[1] with { Id = 47 },
            Inspection.Tracks[4] with { Id = 2 }, Inspection.Tracks[2] with { Id = 77 },
            Inspection.Tracks[6] with { Id = 9 }]);
        var rules = new LanguageRules(Keep("de"), Keep("ru"));
        var first = LanguageRuleMatcher.Preview(new(Inspection, Identifiers(Inspection), rules));
        var second = LanguageRuleMatcher.Preview(new(reordered, Identifiers(reordered), rules));

        Assert.Empty(first.Issues);
        Assert.Empty(second.Issues);
        Assert.Equal([7, 41, 12, 99, 88], first.RetainedTrackIds);
        Assert.Equal([5, 33, 2, 77, 9], second.RetainedTrackIds);
    }

    /// <summary>Uses exact comparison tags, with aliases and casing normalized but no base-language wildcard.</summary>
    [Theory]
    [InlineData("ger", "de-DE", "de-DE", true)]
    [InlineData("deu", "de-DE", "GER-de", true)]
    [InlineData("ger", "de-DE", "de", false)]
    [InlineData("ger", "de", "de-DE", false)]
    [InlineData("ger", null, "de", true)]
    [InlineData("srp", "sr-Cyrl-RS", "SRP-cyrl-rs", true)]
    [InlineData("srp", "sr-Cyrl-RS", "sr-Latn-RS", false)]
    [InlineData("srp", "sr-Cyrl-RS", "sr-Cyrl", false)]
    [InlineData("spa", "es-419", "SPA-419", true)]
    [InlineData("spa", "es-419", "es", false)]
    [InlineData("rus", "RU-ru", "RUS-RU", true)]
    [InlineData("eng", "en-Latn", "eng-latn", true)]
    public void MatchesOnlyExactSupportedTags(string legacy, string? ietf, string rule, bool matches)
    {
        var inspection = Inspect([Track(new(0, TrackKind.Video)), Track(new(1, TrackKind.Audio, legacy, ietf))]);
        var current = Identifiers(inspection);
        var result = LanguageRuleMatcher.Preview(new(inspection, current, new(Keep(rule), Unchanged)));

        Assert.False(result.HasChanges);
        Assert.Equal(current, result.RetainedTrackIds);

        if (matches)
        {
            Assert.Empty(result.Issues);
            return;
        }

        Assert.Equal(LanguageRuleIssueReason.NoAudioMatches, Assert.Single(result.Issues).Reason);
    }

    /// <summary>Requires review of all targeted metadata, including tracks that would not match or were not previously kept.</summary>
    [Theory]
    [InlineData(null, "en", LanguageRuleIssueReason.UnknownLanguage)]
    [InlineData("", "en", LanguageRuleIssueReason.UnknownLanguage)]
    [InlineData("und", "en", LanguageRuleIssueReason.UnknownLanguage)]
    [InlineData("mul", null, LanguageRuleIssueReason.UnknownLanguage)]
    [InlineData("mis", null, LanguageRuleIssueReason.UnknownLanguage)]
    [InlineData("zxx", null, LanguageRuleIssueReason.UnknownLanguage)]
    [InlineData("qaa", "en", LanguageRuleIssueReason.UnknownLanguage)]
    [InlineData("zzz", null, LanguageRuleIssueReason.UnknownLanguage)]
    [InlineData("eng", "qaa", LanguageRuleIssueReason.UnknownLanguage)]
    [InlineData("eng", "x-private", LanguageRuleIssueReason.UnknownLanguage)]
    [InlineData("eng", "und", LanguageRuleIssueReason.UnknownLanguage)]
    [InlineData("eng", "", LanguageRuleIssueReason.UnknownLanguage)]
    [InlineData("eng", "en_US", LanguageRuleIssueReason.MalformedLanguage)]
    [InlineData("eng", "en--US", LanguageRuleIssueReason.MalformedLanguage)]
    [InlineData("eng", "en-u-ca-gregory", LanguageRuleIssueReason.MalformedLanguage)]
    [InlineData("eng", "en-GB-oed", LanguageRuleIssueReason.MalformedLanguage)]
    [InlineData("eng", "en-US-Latn", LanguageRuleIssueReason.MalformedLanguage)]
    [InlineData("eng", "en-x-private", LanguageRuleIssueReason.MalformedLanguage)]
    [InlineData("en-US", "en-US", LanguageRuleIssueReason.MalformedLanguage)]
    [InlineData("eng", "de", LanguageRuleIssueReason.ConflictingLanguage)]
    [InlineData("ger", "en-US", LanguageRuleIssueReason.ConflictingLanguage)]
    public void UncertainTargetMetadataKeepsTheEntireFileUnchanged(string? legacy, string? ietf, LanguageRuleIssueReason reason)
    {
        var inspection = Inspect([
            Track(new(0, TrackKind.Video)), Track(new(1, TrackKind.Audio, "eng", "en")),
            Track(new(2, TrackKind.Audio, legacy, ietf)), Track(new(3, TrackKind.Subtitles, "rus", "ru"))]);
        ImmutableArray<int> current = [3, 0, 1];
        var result = LanguageRuleMatcher.Preview(new(inspection, current, new(Keep("en"), Keep("de"))));
        var issue = Assert.Single(result.Issues);

        Assert.Equal(TrackKind.Audio, issue.Category);
        Assert.Equal(2, issue.TrackId);
        Assert.Equal(reason, issue.Reason);
        Assert.True(current == result.RetainedTrackIds);
        Assert.False(result.HasChanges);
        Assert.False(result.NoSubtitleMatches);
    }

    /// <summary>Returns the actual uncertain IETF value rather than blaming a known legacy field.</summary>
    [Fact]
    public void UnknownIetfIssueRetainsTheProblematicOriginalValue()
    {
        var inspection = Inspect([Track(new(0, TrackKind.Video)), Track(new(1, TrackKind.Audio, "eng", "qaa"))]);
        var result = LanguageRuleMatcher.Preview(new(inspection, Identifiers(inspection), new(Keep("en"), Unchanged)));

        Assert.Equal("qaa", Assert.Single(result.Issues).Value);
    }

    /// <summary>Skips invalid metadata and unused language tokens in every category whose mode is Unchanged.</summary>
    [Fact]
    public void UnchangedModeDoesNotInterpretUntargetedMetadata()
    {
        var inspection = Inspect([
            Track(new(0, TrackKind.Video, "und", "invalid")),
            Track(new(1, TrackKind.Audio, "qaa", "en")), Track(new(2, TrackKind.Subtitles, "rus", "ru"))]);
        var untouchedAudio = new TrackLanguageRule(LanguageRuleMode.Unchanged, ["not a language"]);
        var result = LanguageRuleMatcher.Preview(new(inspection, [0, 1], new(untouchedAudio, Keep("ru"))));

        Assert.Empty(result.Issues);
        Assert.Equal([0, 1, 2], result.RetainedTrackIds);
        Assert.True(result.HasChanges);
    }

    /// <summary>Never applies a valid audio change when a targeted subtitle requires manual review.</summary>
    [Fact]
    public void SubtitleIssueAlsoRollsBackTheAudioProposal()
    {
        var inspection = Inspection with
        {
            Tracks = Inspection.Tracks.SetItem(4, Inspection.Tracks[4] with { LanguageIetf = "en" })
        };
        var current = Identifiers(inspection);
        var result = LanguageRuleMatcher.Preview(new(inspection, current, new(Keep("de"), Keep("ru"))));

        Assert.Equal(TrackKind.Subtitles, Assert.Single(result.Issues).Category);
        Assert.True(current == result.RetainedTrackIds);
        Assert.False(result.HasChanges);
    }

    /// <summary>Rejects an empty target language list without interpreting it as remove-all.</summary>
    [Fact]
    public void EmptyOrDefaultTargetLanguagesRequireReview()
    {
        var result = LanguageRuleMatcher.Preview(new(Inspection, Identifiers(Inspection),
            new(new(LanguageRuleMode.KeepMatching, []), new(LanguageRuleMode.KeepMatching, default))));

        Assert.Equal(2, result.Issues.Length);
        Assert.All(result.Issues, issue => Assert.Equal(LanguageRuleIssueReason.EmptyLanguageSelection, issue.Reason));
        Assert.False(result.HasChanges);
    }

    /// <summary>Refuses invalid, unknown, private or unsupported requested tags instead of silently ignoring them.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("und")]
    [InlineData("mul")]
    [InlineData("mis")]
    [InlineData("zxx")]
    [InlineData("qaa")]
    [InlineData("x-private")]
    [InlineData("en_US")]
    [InlineData("en-x-private")]
    [InlineData("en-u-ca-gregory")]
    [InlineData(" de")]
    public void InvalidRequestedLanguageCannotPartiallyApply(string language)
    {
        var result = LanguageRuleMatcher.Preview(new(Inspection, Identifiers(Inspection),
            new(new(LanguageRuleMode.KeepMatching, ["de", language]), Keep("ru"))));

        Assert.Equal(LanguageRuleIssueReason.InvalidRuleLanguage, Assert.Single(result.Issues).Reason);
        Assert.Equal(language, result.Issues[0].Value);
        Assert.Equal(Identifiers(Inspection), result.RetainedTrackIds);
        Assert.False(result.HasChanges);
    }

    /// <summary>Leaves already silent input valid while permitting a no-match subtitle result.</summary>
    [Fact]
    public void AlreadySilentSourceIsValidAndSubtitleNoMatchIsExplicit()
    {
        var silent = Inspect([Track(new(0, TrackKind.Video)), Track(new(1, TrackKind.Subtitles, "eng", "en"))]);
        var result = LanguageRuleMatcher.Preview(new(silent, [0, 1], new(Keep("de"), Keep("ru"))));

        Assert.Empty(result.Issues);
        Assert.Equal([0], result.RetainedTrackIds);
        Assert.True(result.HasChanges);
        Assert.True(result.NoSubtitleMatches);
    }

    /// <summary>Reports the targeted category's lack of tracks without inventing a selection change.</summary>
    [Fact]
    public void SubtitleFreeSourceHasNoMatchesAndNoChanges()
    {
        var inspection = Inspect([Track(new(0, TrackKind.Video))]);
        var result = LanguageRuleMatcher.Preview(new(inspection, [0], new(Keep("de"), Keep("ru"))));

        Assert.Empty(result.Issues);
        Assert.False(result.HasChanges);
        Assert.True(result.NoSubtitleMatches);
    }

    /// <summary>Retains existing audio and subtitles when no audio matches rather than proposing an accidental silent file.</summary>
    [Fact]
    public void NoAudioMatchesPreventsEveryCategoryChange()
    {
        var current = Identifiers(Inspection);
        var result = LanguageRuleMatcher.Preview(new(Inspection, current, new(Keep("ru"), Keep("de"))));

        Assert.Equal(LanguageRuleIssueReason.NoAudioMatches, Assert.Single(result.Issues).Reason);
        Assert.True(current == result.RetainedTrackIds);
        Assert.False(result.HasChanges);
        Assert.False(result.NoSubtitleMatches);
    }

    /// <summary>Rejects duplicate, absent and missing-video IDs without repairing an invalid caller selection.</summary>
    [Theory]
    [InlineData(new[] { 7, 88, 23, 23 })]
    [InlineData(new[] { 7, 88, 999 })]
    [InlineData(new[] { 7, 23, 41, 12, 99, 3 })]
    [InlineData(new[] { 7, 88, -1 })]
    public void IncoherentCurrentSelectionRequiresReview(int[] identifiers)
    {
        var current = identifiers.ToImmutableArray();
        var result = LanguageRuleMatcher.Preview(new(Inspection, current, new(Keep("de"), Keep("ru"))));

        Assert.Equal(LanguageRuleIssueReason.InvalidCurrentSelection, Assert.Single(result.Issues).Reason);
        Assert.True(current == result.RetainedTrackIds);
        Assert.False(result.HasChanges);
    }

    /// <summary>Rejects uninitialized selections or duplicate inspected IDs before any dictionary lookup can be ambiguous.</summary>
    [Fact]
    public void DefaultSelectionAndAmbiguousInspectionAreRejected()
    {
        var rules = new LanguageRules(Unchanged, Unchanged);
        var missing = LanguageRuleMatcher.Preview(new(Inspection, default, rules));
        var duplicated = Inspection with { Tracks = Inspection.Tracks.Add(Inspection.Tracks[0]) };
        var ambiguous = LanguageRuleMatcher.Preview(new(duplicated, Identifiers(Inspection), rules));

        Assert.Equal(LanguageRuleIssueReason.InvalidCurrentSelection, Assert.Single(missing.Issues).Reason);
        Assert.Equal(LanguageRuleIssueReason.InvalidCurrentSelection, Assert.Single(ambiguous.Issues).Reason);
    }

    /// <summary>Leaves raw metadata, input arrays and duplicate alias rule entries unchanged.</summary>
    [Fact]
    public void PreviewIsImmutableAndSelectionOrderingDoesNotInventChanges()
    {
        var inspection = Inspect([Track(new(0, TrackKind.Video)), Track(new(1, TrackKind.Audio, "GER", "de-DE"))]);
        ImmutableArray<int> current = [1, 0];
        ImmutableArray<string> languages = ["GER-de", "deu-DE", "de-DE"];
        var rules = new LanguageRules(new(LanguageRuleMode.KeepMatching, languages), Unchanged);
        var result = LanguageRuleMatcher.Preview(new(inspection, current, rules));

        Assert.Empty(result.Issues);
        Assert.False(result.HasChanges);
        Assert.True(current == result.RetainedTrackIds);
        Assert.True(languages == rules.Audio.Languages);
        Assert.Equal("GER", inspection.Tracks[1].Language);
        Assert.Equal("de-DE", inspection.Tracks[1].LanguageIetf);
    }

    /// <summary>Treats missing rule objects and invalid enum values as review issues at the public boundary.</summary>
    [Fact]
    public void InvalidRuleObjectsCannotThrowOrChangeSelections()
    {
        var current = Identifiers(Inspection);
        var absent = LanguageRuleMatcher.Preview(new(Inspection, current, null!));
        var invalidMode = LanguageRuleMatcher.Preview(new(Inspection, current,
            new(new((LanguageRuleMode)int.MaxValue, ["de"]), Unchanged)));

        Assert.All(absent.Issues, issue => Assert.Equal(LanguageRuleIssueReason.InvalidRuleLanguage, issue.Reason));
        Assert.Equal(2, absent.Issues.Length);
        Assert.Equal(LanguageRuleIssueReason.InvalidRuleLanguage, Assert.Single(invalidMode.Issues).Reason);
        Assert.False(absent.HasChanges);
        Assert.False(invalidMode.HasChanges);
    }

    /// <summary>Creates one explicit language choice without UI parsing or persistence.</summary>
    private static TrackLanguageRule Keep(string language) => new(LanguageRuleMode.KeepMatching, [language]);

    /// <summary>Builds a pure immutable inspection for the supplied source-local identities.</summary>
    private static MediaInspection Inspect(ImmutableArray<MediaTrack> tracks) => new(
        new("source.mkv", "identity", 100, 200), new("mkvmerge", "102.0", 20), tracks, [], "Title", false, false);

    /// <summary>Snapshots each actual inspected ID rather than assuming contiguous indices.</summary>
    private static ImmutableArray<int> Identifiers(MediaInspection inspection) => inspection.Tracks.Select(track => track.Id).ToImmutableArray();

    /// <summary>Creates original raw metadata including deliberately missing values used by failure cases.</summary>
    private static MediaTrack Track(TrackSpecification specification) => new(
        specification.Id, (ulong)specification.Id + 1, specification.Kind, "codec", specification.Legacy!,
        specification.Ietf, "Original title", null, false, false, true, ImmutableDictionary<string, string>.Empty);

    private sealed record TrackSpecification(int Id, TrackKind Kind, string? Legacy = "eng", string? Ietf = "en");
}
