using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TrackTrim.Application.Ports;
using TrackTrim.Core.Media;
using TrackTrim.Infrastructure.Processes;

namespace TrackTrim.Infrastructure.MediaTools;

/// <summary>Recognizes only two pinned MOV demuxer compatibility diagnostics backed by admitted title/artwork facts.</summary>
internal static class FfmpegDiagnostics
{
    private static readonly Regex InputPrefix = new(@"^\[in#0 @ (?:0x)?[0-9a-fA-F]+\] ", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex RepeatedMessage = new(@"^Last message repeated ([0-9]+) times$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>Preserves raw diagnostics while requiring a zero exit and exact known, structurally justified messages; all others fail closed.</summary>
    internal static ToolDisposition Classify(ProcessResult result, MediaInspection? inspection)
    {
        if (result.ExitCode != 0 || result.StandardErrorTruncated || result.ProtocolLineTruncated)
        {
            return ToolDisposition.Failure;
        }

        if (string.IsNullOrWhiteSpace(result.StandardError))
        {
            return ToolDisposition.Success;
        }

        if (inspection is null)
        {
            return ToolDisposition.Failure;
        }

        var namedTracks = inspection.Tracks.Count(HasName);
        var hasArtwork = inspection.Tracks.Any(track => track.Properties["attached_pic"] == "1");
        var previousWasRawName = false;

        foreach (var rawLine in result.StandardError.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            var repeat = RepeatedMessage.Match(line);

            if (previousWasRawName && repeat.Success && int.TryParse(repeat.Groups[1].Value, CultureInfo.InvariantCulture, out var repetitions)
                && repetitions > 0 && repetitions < namedTracks)
            {
                previousWasRawName = false;
                continue;
            }

            previousWasRawName = false;

            if (!InputPrefix.IsMatch(line))
            {
                return ToolDisposition.Failure;
            }

            var message = InputPrefix.Replace(line, string.Empty, 1);

            if (namedTracks > 0 && message == "UDTA parsing failed retrying raw")
            {
                previousWasRawName = true;
                continue;
            }

            // MOV cover art is synthetic (no mdhd); every real media track's positive timescale was checked by Mp4BoxReader.
            if (hasArtwork && message == "stream 0, timescale not set")
            {
                continue;
            }

            return ToolDisposition.Failure;
        }

        return ToolDisposition.Success;
    }

    /// <summary>Uses frozen, validated actual name metadata rather than a display fallback as the warning justification.</summary>
    private static bool HasName(MediaTrack track)
    {
        using var document = JsonDocument.Parse(track.Properties["tags"]);
        return document.RootElement.TryGetProperty("name", out _);
    }
}
