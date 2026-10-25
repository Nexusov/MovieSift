using System.Globalization;
using MovieSift.Core.Media;
using MovieSift.Core.Planning;
using MovieSift.Desktop.Localization;

namespace MovieSift.Desktop.ViewModels;

/// <summary>Formats immutable file-local retention evidence for either workflow.</summary>
internal static class PlanPresentation
{
    /// <summary>Lists the retained and removed identities without matching tracks across files.</summary>
    public static string DescribeTracks(RemuxPlan? plan, LocalizationService localization)
    {
        if (plan is null)
        {
            return string.Empty;
        }

        var retained = plan.RetainedTracks.Select(track => track.Id).ToHashSet();
        return string.Join(Environment.NewLine, plan.Inspection.Tracks.Select(track =>
        {
            var action = localization[retained.Contains(track.Id) ? "Keep" : "Remove"];
            var title = string.IsNullOrWhiteSpace(track.Title) ? localization["Untitled"] : track.Title;
            return $"{action}: {localization[track.Kind.ToString()]} {track.Id} — {title} ({track.Codec}, {track.LanguageIetf ?? track.Language})";
        }));
    }

    /// <summary>Describes the resulting default flags, including explicitly confirmed silence.</summary>
    public static string DescribeDefaultAudio(RemuxPlan? plan, LocalizationService localization)
    {
        if (plan is null)
        {
            return string.Empty;
        }

        if (!plan.RetainedTracks.Any(track => track.Kind == TrackKind.Audio))
        {
            return localization["PlanNoAudio"];
        }

        if (!plan.HasDefaultAudio)
        {
            return localization["PlanNoDefaultAudio"];
        }

        var identifiers = plan.RetainedTracks.Where(track => track.Kind == TrackKind.Audio && track.IsDefault)
            .Select(track => track.Id.ToString(CultureInfo.InvariantCulture));
        return localization.Format(new("PlanDefaultAudioRetained", [string.Join(", ", identifiers)]));
    }
}
