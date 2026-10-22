using System.Globalization;
using System.Text.Json;
using TrackTrim.Application.Ports;

namespace TrackTrim.Infrastructure.MediaTools;

/// <summary>Builds explicit local MP4 stream-copy arguments from the frozen approved mapping.</summary>
internal static class FfmpegArguments
{
    /// <summary>Uses local-only input, disabled external references, explicit maps and dispositions, and real machine progress.</summary>
    internal static IReadOnlyList<string> BuildRemux(RemuxRequest request)
    {
        var arguments = new List<string>
        {
            "-nostdin", "-hide_banner", "-loglevel", "warning", "-n", "-protocol_whitelist", "file",
            "-enable_drefs", "0", "-use_absolute_path", "0", "-copyts", "-f", "mov", "-i", request.Plan.Inspection.Source.FullPath,
        };

        foreach (var track in request.Plan.RetainedTracks)
        {
            arguments.AddRange(["-map", "0:" + track.Id.ToString(CultureInfo.InvariantCulture)]);
        }

        arguments.AddRange(["-map_metadata", "0", "-map_chapters", "0", "-c", "copy"]);

        for (var index = 0; index < request.Plan.RetainedTracks.Length; index++)
        {
            var track = request.Plan.RetainedTracks[index];
            var flags = track.Properties.Where(property => property.Key.StartsWith("disposition:", StringComparison.Ordinal)
                && property.Value == "1").Select(property => property.Key["disposition:".Length..]).ToList();

            if (track.IsDefault)
            {
                flags.Insert(0, "default");
            }

            arguments.AddRange(["-map_metadata:s:" + index.ToString(CultureInfo.InvariantCulture),
                "0:s:" + track.Id.ToString(CultureInfo.InvariantCulture),
                "-disposition:" + index.ToString(CultureInfo.InvariantCulture), flags.Count == 0 ? "0" : string.Join('+', flags)]);

            var tags = JsonSerializer.Deserialize<Dictionary<string, string>>(track.Properties["tags"])!;

            if (tags.TryGetValue("name", out var title))
            {
                // The MOV demuxer exposes the track's udta/name as "name"; its muxer writes that atom from "title".
                arguments.AddRange(["-metadata:s:" + index.ToString(CultureInfo.InvariantCulture), "title=" + title]);
            }
        }

        // Keep demuxed presentation timestamps; do not shift negative AAC priming/B-frame decode timestamps.
        // Existing tx3g extradata may already contain btrt; suppress adding a duplicate codec child.
        arguments.AddRange(["-avoid_negative_ts", "disabled", "-write_btrt", "0", "-write_tmcd", "0",
            "-progress", "pipe:1", "-nostats", "-f", "mp4", request.CandidatePath]);
        return arguments;
    }

    /// <summary>Requests bounded structural JSON, never packet-sized JSON for an entire movie.</summary>
    internal static IReadOnlyList<string> BuildProbe(string path) =>
    [
        "-v", "error", "-f", "mov", "-protocol_whitelist", "file", "-enable_drefs", "0", "-use_absolute_path", "0",
        "-show_streams", "-show_format", "-show_chapters", "-show_data_hash", "sha256", "-of", "json", path,
    ];

    /// <summary>Streams bounded packet records for exact retained payload/timestamp and AAC skip/padding comparison.</summary>
    internal static IReadOnlyList<string> BuildPackets(string path) =>
    [
        "-v", "error", "-f", "mov", "-protocol_whitelist", "file", "-enable_drefs", "0", "-use_absolute_path", "0",
        "-show_packets", "-show_data_hash", "sha256", "-show_entries",
        "packet=stream_index,pts,dts,duration,size,flags,data_hash:packet_side_data=side_data_type,skip_samples,discard_padding,skip_reason,discard_reason",
        "-of", "compact=p=0", path,
    ];
}
