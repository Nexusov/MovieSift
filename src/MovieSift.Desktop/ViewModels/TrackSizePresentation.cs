using System.Globalization;
using MovieSift.Core.Media;
using MovieSift.Desktop.Localization;

namespace MovieSift.Desktop.ViewModels;

/// <summary>Labels unverified size statistics honestly without scanning media for presentation.</summary>
internal static class TrackSizePresentation
{
    private const double BytesPerMebibyte = 1_048_576;
    private const double BytesPerGibibyte = 1_073_741_824;
    private const double BytesPerKibibyte = 1_024;

    /// <summary>Distinguishes declared bytes, bitrate estimates and missing metadata in every track view.</summary>
    public static string Describe(MediaTrack track, LocalizationService localization)
    {
        if (track.PayloadSize is not { } size)
        {
            return localization["TrackSizeUnavailable"];
        }

        var bytes = size.Bytes;
        var (divisor, unit) = bytes switch
        {
            >= (long)BytesPerGibibyte => (BytesPerGibibyte, "GiB"),
            >= (long)BytesPerMebibyte => (BytesPerMebibyte, "MiB"),
            >= (long)BytesPerKibibyte => (BytesPerKibibyte, "KiB"),
            _ => (1d, "B")
        };
        var text = $"{(bytes / divisor).ToString("0.##", CultureInfo.GetCultureInfo(localization.Language))} {unit}";
        var key = size.Kind == TrackPayloadSizeKind.Reported ? "TrackSizeReported" : "TrackSizeEstimated";
        return localization.Format(new(key, [text]));
    }
}
