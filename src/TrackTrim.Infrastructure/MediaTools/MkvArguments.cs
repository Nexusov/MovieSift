using System.Globalization;
using TrackTrim.Application.Ports;
using TrackTrim.Core.Media;

namespace TrackTrim.Infrastructure.MediaTools;

/// <summary>Builds explicit retention arguments for one Matroska input without shell interpretation.</summary>
internal static class MkvArguments
{
    private static readonly (string Property, string Option)[] AdditionalFlags =
    [
        ("flag_hearing_impaired", "--hearing-impaired-flag"),
        ("flag_visual_impaired", "--visual-impaired-flag"),
        ("flag_text_descriptions", "--text-descriptions-flag"),
        ("flag_original", "--original-flag"),
        ("flag_commentary", "--commentary-flag"),
    ];

    /// <summary>Preserves approved order, effective flags, attachments, and existing statistics tags.</summary>
    internal static IReadOnlyList<string> BuildRemux(RemuxRequest request)
    {
        var retainedTracks = request.Plan.RetainedTracks;
        // GUI protocol keys are invariant; tool UI locale identifiers differ between Windows and Linux.
        var arguments = new List<string>
        {
            "--gui-mode", "--output-charset", "UTF-8",
            "--normalize-language-ietf", "off", "--disable-track-statistics-tags",
            "--output", request.CandidatePath,
            "--track-order", string.Join(',', retainedTracks.Select(track => $"0:{track.Id.ToString(CultureInfo.InvariantCulture)}")),
        };
        AddRetention(new RetentionRequest(arguments, retainedTracks, TrackKind.Video, "--video-tracks", "--no-video"));
        AddRetention(new RetentionRequest(arguments, retainedTracks, TrackKind.Audio, "--audio-tracks", "--no-audio"));
        AddRetention(new RetentionRequest(arguments, retainedTracks, TrackKind.Subtitles, "--subtitle-tracks", "--no-subtitles"));
        arguments.Add("--no-buttons");

        if (!request.Plan.Inspection.Attachments.IsEmpty)
        {
            arguments.Add("--attachments");
            arguments.Add(string.Join(',', request.Plan.Inspection.Attachments.Select(attachment =>
                attachment.Id.ToString(CultureInfo.InvariantCulture))));
        }

        foreach (var track in retainedTracks)
        {
            var identifier = track.Id.ToString(CultureInfo.InvariantCulture);
            arguments.AddRange(["--default-track-flag", $"{identifier}:{BooleanValue(track.IsDefault)}",
                "--forced-display-flag", $"{identifier}:{BooleanValue(track.IsForced)}",
                "--track-enabled-flag", $"{identifier}:{BooleanValue(track.IsEnabled)}"]);

            foreach (var (property, option) in AdditionalFlags)
            {

                if (track.Properties.TryGetValue(property, out var value))
                {
                    arguments.Add(option);
                    arguments.Add($"{identifier}:{BooleanValue(bool.Parse(value))}");
                }
            }
        }

        arguments.Add(request.Plan.Inspection.Source.FullPath);
        return arguments;
    }

    /// <summary>Adds explicit keep IDs or the documented remove-all option for an empty selection.</summary>
    private static void AddRetention(RetentionRequest request)
    {
        var identifiers = request.Tracks.Where(track => track.Kind == request.Kind)
            .Select(track => track.Id.ToString(CultureInfo.InvariantCulture)).ToArray();

        if (identifiers.Length == 0)
        {
            request.Arguments.Add(request.RemoveAllOption);
            return;
        }

        request.Arguments.Add(request.KeepOption);
        request.Arguments.Add(string.Join(',', identifiers));
    }

    /// <summary>Serializes flags independently of desktop culture.</summary>
    private static string BooleanValue(bool value)
    {
        return value ? "1" : "0";
    }

    private sealed record RetentionRequest(List<string> Arguments, IEnumerable<MediaTrack> Tracks,
        TrackKind Kind, string KeepOption, string RemoveAllOption);
}
