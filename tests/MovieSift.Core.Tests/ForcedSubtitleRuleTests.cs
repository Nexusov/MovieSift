using System.Collections.Immutable;
using MovieSift.Core.Media;
using MovieSift.Core.Rules;
using Xunit;

namespace MovieSift.Core.Tests;

/// <summary>Checks subtitle-only forced filtering without hiding targeted metadata issues or altering other categories.</summary>
public sealed class ForcedSubtitleRuleTests
{
    private static readonly TrackLanguageRule Unchanged = new(LanguageRuleMode.Unchanged, []);
    private static readonly MediaInspection Inspection = Inspect([
        Track(new(7, TrackKind.Video, "und", null, "Required video")),
        Track(new(44, TrackKind.Subtitles, Title: "Same captions", Forced: true)),
        Track(new(23, TrackKind.Audio, Title: "Main")),
        Track(new(9, TrackKind.Subtitles, Title: "Same captions")),
        Track(new(62, TrackKind.Subtitles, Title: "Other captions", Forced: true)),
        Track(new(80, TrackKind.Subtitles, "rus", "ru", "Same captions", true)),
        Track(new(41, TrackKind.Audio, "rus", "ru", "Other", true)),
        Track(new(88, TrackKind.Video, "und", null, "Second required video"))]);

    /// <summary>Preserves language-only behavior by default, including non-forced tracks with otherwise identical metadata.</summary>
    [Fact]
    public void DefaultOffAndExplicitFalseRetainBothForcedAndNonForcedLanguageMatches()
    {
        var current = Identifiers(Inspection);
        var defaultRules = new LanguageRules(Unchanged, Keep("en"));
        var explicitRules = defaultRules with { KeepOnlyForcedSubtitles = false };

        var defaultResult = LanguageRuleMatcher.Preview(new(Inspection, current, defaultRules));
        var explicitResult = LanguageRuleMatcher.Preview(new(Inspection, current, explicitRules));

        Assert.False(defaultRules.KeepOnlyForcedSubtitles);
        Assert.Empty(defaultResult.Issues);
        Assert.Empty(explicitResult.Issues);
        Assert.Equal([7, 44, 23, 9, 62, 41, 88], defaultResult.RetainedTrackIds);
        Assert.Equal(defaultResult.RetainedTrackIds, explicitResult.RetainedTrackIds);
        Assert.False(defaultResult.NoSubtitleMatches);
    }

    /// <summary>Keeps every actual forced language match, including tracks not retained by the previous manual choice.</summary>
    [Fact]
    public void EnabledFilterKeepsAllMatchingForcedSubtitlesInInspectedOrder()
    {
        ImmutableArray<int> current = [7, 23, 9, 41, 88];
        var rules = new LanguageRules(Unchanged, Keep("eng"), KeepOnlyForcedSubtitles: true);

        var result = LanguageRuleMatcher.Preview(new(Inspection, current, rules));

        Assert.Empty(result.Issues);
        Assert.Equal([7, 44, 23, 62, 41, 88], result.RetainedTrackIds);
        Assert.True(result.HasChanges);
        Assert.False(result.NoSubtitleMatches);
        Assert.Equal([7, 23, 9, 41, 88], current);
        Assert.True(Inspection.Tracks[1].IsForced);
        Assert.False(Inspection.Tracks[3].IsForced);
    }

    /// <summary>Requires forced, exact regional language and literal title matches together while retaining every match.</summary>
    [Fact]
    public void ForcedLanguageAndTitleConstraintsAreCombinedWithAnd()
    {
        var inspection = Inspect([
            Track(new(7, TrackKind.Video)),
            Track(new(44, TrackKind.Subtitles, "eng", "en-US", "Main [CUT].*", true)),
            Track(new(9, TrackKind.Subtitles, "eng", "en-US", "Original Main [cut].*", true)),
            Track(new(62, TrackKind.Subtitles, "eng", "en-US", "Main cut anything", true)),
            Track(new(80, TrackKind.Subtitles, "eng", "en", "Main [cut].*", true)),
            Track(new(101, TrackKind.Subtitles, "eng", "en-US", "Main [cut].*")),
            Track(new(18, TrackKind.Subtitles, "rus", "ru", "Main [cut].*", true)),
            Track(new(23, TrackKind.Audio)), Track(new(88, TrackKind.Video))]);
        var subtitles = new TrackLanguageRule(LanguageRuleMode.KeepMatching, ["ENG-us"], new(true, " main [cut].* "));
        var rules = new LanguageRules(Unchanged, subtitles, KeepOnlyForcedSubtitles: true);

        var result = LanguageRuleMatcher.Preview(new(inspection, Identifiers(inspection), rules));

        Assert.Empty(result.Issues);
        Assert.Equal([7, 44, 9, 23, 88], result.RetainedTrackIds);
        Assert.True(result.HasChanges);
        Assert.False(result.NoSubtitleMatches);
    }

    /// <summary>Uses the inspected forced flag rather than inferring it from title wording or missing title text.</summary>
    [Theory]
    [InlineData("Forced subtitles", false, false)]
    [InlineData("Not forced", true, true)]
    [InlineData("Ordinary captions", true, true)]
    [InlineData("", true, true)]
    public void MisleadingOrBlankTitlesNeverReplaceActualForcedFlag(string title, bool forced, bool retained)
    {
        var inspection = Inspect([Track(new(7, TrackKind.Video)),
            Track(new(44, TrackKind.Subtitles, Title: title, Forced: forced))]);
        var rules = new LanguageRules(Unchanged, Keep("en"), KeepOnlyForcedSubtitles: true);

        var result = LanguageRuleMatcher.Preview(new(inspection, [7, 44], rules));
        ImmutableArray<int> expected = retained ? [7, 44] : [7];

        Assert.Empty(result.Issues);
        Assert.Equal(expected, result.RetainedTrackIds);
        Assert.Equal(!retained, result.NoSubtitleMatches);
        Assert.Equal(!retained, result.HasChanges);
    }

    /// <summary>Reports no forced matches explicitly and permits subtitle removal without changing audio or video choices.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NoForcedSubtitleMatchesUsesExistingNoMatchPolicy(bool hasSubtitles)
    {
        var inspection = Inspection with
        {
            Tracks = Inspection.Tracks.Where(track => hasSubtitles || track.Kind != TrackKind.Subtitles)
                .Select(track => track.Kind == TrackKind.Subtitles ? track with { IsForced = false } : track)
                .ToImmutableArray()
        };
        var current = Identifiers(inspection);
        var rules = new LanguageRules(Unchanged, Keep("en"), KeepOnlyForcedSubtitles: true);

        var result = LanguageRuleMatcher.Preview(new(inspection, current, rules));

        Assert.Empty(result.Issues);
        Assert.Equal([7, 23, 41, 88], result.RetainedTrackIds);
        Assert.Equal(hasSubtitles, result.HasChanges);
        Assert.True(result.NoSubtitleMatches);
    }

    /// <summary>Ignores the forced flag and unused invalid configuration when subtitle mode is Unchanged.</summary>
    [Fact]
    public void UnchangedSubtitlesPreserveKeptNonForcedTracksAndCurrentSelectionIdentity()
    {
        var inspection = Inspection with
        {
            Tracks = Inspection.Tracks.SetItem(3, Inspection.Tracks[3] with { Language = "und", LanguageIetf = "invalid" })
        };
        ImmutableArray<int> current = [41, 7, 9, 23, 88];
        var subtitles = new TrackLanguageRule(LanguageRuleMode.Unchanged, ["invalid"], new(true, " "));
        var rules = new LanguageRules(Unchanged, subtitles, KeepOnlyForcedSubtitles: true);

        var result = LanguageRuleMatcher.Preview(new(inspection, current, rules));

        Assert.Empty(result.Issues);
        Assert.True(current == result.RetainedTrackIds);
        Assert.False(result.HasChanges);
        Assert.False(result.NoSubtitleMatches);
        Assert.False(inspection.Tracks[3].IsForced);
    }

    /// <summary>Applies ordinary language and title criteria to audio without requiring its forced flag.</summary>
    [Fact]
    public void ForcedSubtitleSettingDoesNotFilterNonForcedAudioOrAnyVideo()
    {
        var audio = new TrackLanguageRule(LanguageRuleMode.KeepMatching, ["en"], new(true, "Main"));
        var rules = new LanguageRules(audio, Keep("en"), KeepOnlyForcedSubtitles: true);

        var result = LanguageRuleMatcher.Preview(new(Inspection, Identifiers(Inspection), rules));

        Assert.Empty(result.Issues);
        Assert.Equal([7, 44, 23, 62, 88], result.RetainedTrackIds);
        Assert.False(Inspection.Tracks[2].IsForced);
        Assert.Equal(TrackKind.Audio, Inspection.Tracks[2].Kind);
        Assert.All(Inspection.Tracks.Where(track => track.Kind == TrackKind.Video),
            track => Assert.Contains(track.Id, result.RetainedTrackIds));
    }

    /// <summary>Validates unkept non-forced subtitles even when both the title and forced constraints would reject them.</summary>
    [Theory]
    [InlineData("und", null, LanguageRuleIssueReason.UnknownLanguage)]
    [InlineData("eng", "en_US", LanguageRuleIssueReason.MalformedLanguage)]
    [InlineData("eng", "ru", LanguageRuleIssueReason.ConflictingLanguage)]
    public void NonForcedSubtitleCannotHideUncertainLanguageMetadata(
        string legacy, string? ietf, LanguageRuleIssueReason reason)
    {
        var inspection = Inspection with
        {
            Tracks = Inspection.Tracks.SetItem(3, Inspection.Tracks[3] with
            {
                Language = legacy,
                LanguageIetf = ietf,
                Title = "Different title"
            })
        };
        ImmutableArray<int> current = [88, 41, 7, 23, 80];
        var subtitles = new TrackLanguageRule(LanguageRuleMode.KeepMatching, ["en"], new(true, "Same"));
        var rules = new LanguageRules(Keep("en"), subtitles, KeepOnlyForcedSubtitles: true);

        var result = LanguageRuleMatcher.Preview(new(inspection, current, rules));
        var issue = Assert.Single(result.Issues);

        Assert.Equal(TrackKind.Subtitles, issue.Category);
        Assert.Equal(9, issue.TrackId);
        Assert.Equal(reason, issue.Reason);
        Assert.True(current == result.RetainedTrackIds);
        Assert.False(result.HasChanges);
        Assert.False(result.NoSubtitleMatches);
    }

    /// <summary>Evaluates reordered episodes independently and preserves each file's retained source order.</summary>
    [Fact]
    public void ReorderedInspectionUsesItsOwnForcedSubtitleIdentifiers()
    {
        var reordered = Inspect([
            Inspection.Tracks[3] with { Id = 5 }, Inspection.Tracks[6] with { Id = 72 },
            Inspection.Tracks[4] with { Id = 40 }, Inspection.Tracks[0] with { Id = 2 },
            Inspection.Tracks[1] with { Id = 90 }, Inspection.Tracks[5] with { Id = 11 },
            Inspection.Tracks[2] with { Id = 13 }, Inspection.Tracks[7] with { Id = 54 }]);
        var rules = new LanguageRules(Unchanged, Keep("en"), KeepOnlyForcedSubtitles: true);

        var first = LanguageRuleMatcher.Preview(new(Inspection, Identifiers(Inspection), rules));
        var second = LanguageRuleMatcher.Preview(new(reordered, Identifiers(reordered), rules));

        Assert.Empty(first.Issues);
        Assert.Empty(second.Issues);
        Assert.Equal([7, 44, 23, 62, 41, 88], first.RetainedTrackIds);
        Assert.Equal([72, 40, 2, 90, 13, 54], second.RetainedTrackIds);
    }

    /// <summary>Preserves the exact current selection array and raw inspection metadata when forced choices already match.</summary>
    [Fact]
    public void MatchingForcedSelectionIsAnImmutableNoOp()
    {
        ImmutableArray<int> current = [88, 62, 23, 7, 44, 41];
        var tracks = Inspection.Tracks;
        var rules = new LanguageRules(Unchanged, Keep("eng"), KeepOnlyForcedSubtitles: true);

        var result = LanguageRuleMatcher.Preview(new(Inspection, current, rules));

        Assert.Empty(result.Issues);
        Assert.True(current == result.RetainedTrackIds);
        Assert.False(result.HasChanges);
        Assert.False(result.NoSubtitleMatches);
        Assert.True(tracks == Inspection.Tracks);
        Assert.Equal("Same captions", Inspection.Tracks[1].Title);
        Assert.True(Inspection.Tracks[1].IsForced);
        Assert.False(Inspection.Tracks[3].IsForced);
        Assert.Equal("eng", Assert.Single(rules.Subtitles.Languages));
    }

    /// <summary>Creates one ordinary targeted language category without a title constraint.</summary>
    private static TrackLanguageRule Keep(string language) => new(LanguageRuleMode.KeepMatching, [language]);

    /// <summary>Builds a pure immutable inspection with supplied file-local metadata and no external dependencies.</summary>
    private static MediaInspection Inspect(ImmutableArray<MediaTrack> tracks) => new(
        new("source.mkv", "identity", 100, 200), new("mkvmerge", "102.0", 20), tracks, [], "Title", false, false);

    /// <summary>Captures actual inspected track IDs rather than assuming positional identities.</summary>
    private static ImmutableArray<int> Identifiers(MediaInspection inspection) => inspection.Tracks.Select(track => track.Id).ToImmutableArray();

    /// <summary>Creates raw track metadata with an explicit forced flag independent of its title.</summary>
    private static MediaTrack Track(TrackSpecification specification) => new(
        specification.Id, (ulong)specification.Id + 1, specification.Kind, "codec", specification.Legacy,
        specification.Ietf, specification.Title, null, false, specification.Forced, true,
        ImmutableDictionary<string, string>.Empty);

    private sealed record TrackSpecification(
        int Id,
        TrackKind Kind,
        string Legacy = "eng",
        string? Ietf = "en",
        string Title = "Captions",
        bool Forced = false);
}
