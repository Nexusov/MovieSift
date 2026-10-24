using TrackTrim.Core.Media;
using TrackTrim.Desktop.Localization;

namespace TrackTrim.Desktop.ViewModels;

/// <summary>Shares effective metadata formatting between per-file and aggregate track details.</summary>
internal static class TrackPresentation
{
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
