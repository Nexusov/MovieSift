using System.Collections.Immutable;
using TrackTrim.Core.Media;

namespace TrackTrim.Core.Rules;

/// <summary>Proposes independent language selections while preserving the whole file on any review issue.</summary>
public static class LanguageRuleMatcher
{
    /// <summary>Matches exact language tags with optional literal title and subtitle-forced constraints.</summary>
    public static LanguageSelectionProposal Preview(LanguageSelectionRequest request)
    {
        if (!HasCoherentCurrentSelection(request))
        {
            return new(request.CurrentRetainedTrackIds,
                [new(TrackKind.Video, null, LanguageRuleIssueReason.InvalidCurrentSelection)], false, false);
        }

        var retainedIdentifiers = request.CurrentRetainedTrackIds.ToHashSet();
        var issues = ImmutableArray.CreateBuilder<LanguageRuleIssue>();
        SelectCategory(new(TrackKind.Audio, request.Rules?.Audio, request.Inspection.Tracks, retainedIdentifiers, issues));
        var noSubtitleMatches = SelectCategory(new(TrackKind.Subtitles, request.Rules?.Subtitles,
            request.Inspection.Tracks, retainedIdentifiers, issues, request.Rules?.KeepOnlyForcedSubtitles ?? false));

        if (issues.Count != 0)
        {
            return new(request.CurrentRetainedTrackIds, issues.ToImmutable(), false, false);
        }

        var hasChanges = retainedIdentifiers.Count != request.CurrentRetainedTrackIds.Length ||
            request.CurrentRetainedTrackIds.Any(identifier => !retainedIdentifiers.Contains(identifier));
        var proposed = hasChanges
            ? request.Inspection.Tracks.Where(track => retainedIdentifiers.Contains(track.Id)).Select(track => track.Id).ToImmutableArray()
            : request.CurrentRetainedTrackIds;
        return new(proposed, [], hasChanges, noSubtitleMatches);
    }

    /// <summary>Rejects ambiguous IDs and video removal before interpreting any language metadata.</summary>
    private static bool HasCoherentCurrentSelection(LanguageSelectionRequest request)
    {
        if (request.CurrentRetainedTrackIds.IsDefault || request.Inspection is null || request.Inspection.Tracks.IsDefault)
        {
            return false;
        }

        var tracks = request.Inspection.Tracks;
        var availableIdentifiers = tracks.Select(track => track.Id).ToHashSet();
        var retainedIdentifiers = request.CurrentRetainedTrackIds.ToHashSet();
        var hasAmbiguousIdentifiers = availableIdentifiers.Count != tracks.Length || tracks.Any(track => track.Id < 0) ||
            retainedIdentifiers.Count != request.CurrentRetainedTrackIds.Length;

        if (hasAmbiguousIdentifiers || !retainedIdentifiers.IsSubsetOf(availableIdentifiers))
        {
            return false;
        }

        return tracks.All(track => track.Kind != TrackKind.Video || retainedIdentifiers.Contains(track.Id));
    }

    /// <summary>Replaces only one targeted category after every language in that category is trustworthy.</summary>
    private static bool SelectCategory(CategoryLanguageSelection request)
    {
        var configuration = LanguageRuleConfiguration.ValidateCategory(new(request.Category, request.Rule));

        if (!configuration.Issues.IsEmpty)
        {
            request.Issues.AddRange(configuration.Issues);
            return false;
        }

        if (request.Rule!.Mode == LanguageRuleMode.Unchanged)
        {
            return false;
        }

        var previousIssueCount = request.Issues.Count;
        var requestedLanguages = configuration.CanonicalLanguages.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var titleSearch = configuration.TitleSearch;
        var tracks = request.Tracks.Where(track => track.Kind == request.Category).ToImmutableArray();
        var matchingIdentifiers = new HashSet<int>();

        foreach (var track in tracks)
        {
            var language = ReadTrackLanguage(track);

            if (language.Issue is { } reason)
            {
                request.Issues.Add(new(request.Category, track.Id, reason, language.Value));
                continue;
            }

            var languageMatches = requestedLanguages.Contains(language.Tag!);
            var titleMatches = titleSearch is null || !string.IsNullOrWhiteSpace(track.Title) &&
                track.Title.Contains(titleSearch, StringComparison.OrdinalIgnoreCase);
            var forcedMatches = !request.KeepOnlyForcedSubtitles || track.IsForced;
            var matches = languageMatches && titleMatches && forcedMatches;

            if (matches)
            {
                matchingIdentifiers.Add(track.Id);
            }
        }

        if (request.Issues.Count != previousIssueCount)
        {
            return false;
        }

        if (request.Category == TrackKind.Audio && !tracks.IsEmpty && matchingIdentifiers.Count == 0)
        {
            request.Issues.Add(new(request.Category, null, LanguageRuleIssueReason.NoAudioMatches));
            return false;
        }

        foreach (var track in tracks)
        {
            request.RetainedIdentifiers.Remove(track.Id);
        }

        request.RetainedIdentifiers.UnionWith(matchingIdentifiers);
        return request.Category == TrackKind.Subtitles && matchingIdentifiers.Count == 0;
    }

    /// <summary>Requires a known legacy code before accepting an authoritative, primary-consistent IETF tag.</summary>
    internal static ParsedLanguageTag ReadTrackLanguage(MediaTrack track)
    {
        var legacy = LanguageRuleTagParser.ParsePlain(track.Language);

        if (legacy.Issue is not null || track.LanguageIetf is null)
        {
            return legacy;
        }

        var ietf = LanguageRuleTagParser.ParseTag(track.LanguageIetf);

        if (ietf.Issue is not null)
        {
            return ietf;
        }

        return legacy.Primary == ietf.Primary ? ietf : new(null, null, LanguageRuleIssueReason.ConflictingLanguage,
            track.Language + " / " + track.LanguageIetf);
    }
}
