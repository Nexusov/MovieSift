using TrackTrim.Core.Media;
using TrackTrim.Desktop.Localization;

namespace TrackTrim.Desktop.ViewModels;

/// <summary>Shares effective metadata formatting between per-file and aggregate track details.</summary>
internal static class TrackPresentation
{
    /// <summary>Summarizes actual group flags without choosing one member's default or enabled value.</summary>
    internal static string DescribeGroupFlags(IEnumerable<MediaTrack> tracks, LocalizationService localization)
    {
        var members = tracks.ToArray();
        var flags = localization.Format(new("AggregateFlags", [
            DescribeBoolean(members.Select(track => track.IsForced), localization),
            DescribeBoolean(members.Select(track => track.IsDefault), localization),
            DescribeBoolean(members.Select(track => track.IsEnabled), localization)]));
        var roles = members.SelectMany(track => track.Dispositions.IsDefaultOrEmpty ? [] : track.Dispositions)
            .Distinct().OrderBy(disposition => disposition)
            .Select(disposition => localization[$"Disposition{disposition}"]);
        return string.Join(", ", new[] { flags }.Concat(roles));
    }

    /// <summary>Distinguishes unanimous effective flags from values that vary across concrete members.</summary>
    private static string DescribeBoolean(IEnumerable<bool> values, LocalizationService localization)
    {
        var distinct = values.Distinct().ToArray();
        return distinct.Length == 1 ? localization[distinct[0] ? "Yes" : "No"] : localization["Varies"];
    }

    /// <summary>Displays flags already resolved by the backend without changing their semantics.</summary>
    internal static string DescribeFlags(MediaTrack track, LocalizationService localization)
    {
        var flags = new List<string>();

        if (track.IsDefault)
        {
            flags.Add(localization["Default"]);
        }

        if (track.IsForced)
        {
            flags.Add(localization["Forced"]);
        }

        flags.Add(localization[track.IsEnabled ? "Enabled" : "Disabled"]);

        if (!track.Dispositions.IsDefaultOrEmpty)
        {
            flags.AddRange(track.Dispositions.Select(disposition => localization[$"Disposition{disposition}"]));
        }

        return string.Join(", ", flags);
    }
}
