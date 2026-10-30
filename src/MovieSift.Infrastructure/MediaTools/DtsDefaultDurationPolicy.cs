using System.Collections.Immutable;
using System.Globalization;
using MovieSift.Core.Media;

namespace MovieSift.Infrastructure.MediaTools;

/// <summary>Recognizes only a missing DTS duration derived from independently read matching core-frame headers.</summary>
internal static class DtsDefaultDurationPolicy
{
    internal const string PropertyName = "default_duration";
    internal const string EvidenceKey = "_moviesift_dts_core_evidence";
    private const string CodecIdentifier = "A_DTS";
    private const decimal NanosecondsPerSecond = 1_000_000_000;

    /// <summary>Captures private computed evidence without accepting a tool-provided substitute or changing media metadata.</summary>
    internal static async Task<MediaInspection> AnnotateAsync(MediaInspection inspection, CancellationToken cancellationToken)
    {
        var targets = inspection.Tracks.Where(IsDts).ToArray();
        var numbers = targets.Select(track => ParseNumber(track.Properties.GetValueOrDefault("number")))
            .Where(number => number > 0).ToHashSet();
        var evidence = numbers.Count == 0 ? ImmutableDictionary<ulong, DtsCoreFrameHeader>.Empty :
            await DtsCoreHeaderEvidence.ReadAsync(new(inspection.Source.FullPath, numbers, cancellationToken)).ConfigureAwait(false);
        return inspection with
        {
            Tracks = inspection.Tracks.Select(track =>
            {
                var properties = track.Properties.Remove(EvidenceKey);
                var number = ParseNumber(properties.GetValueOrDefault("number"));
                var eligible = IsDts(track) && evidence.TryGetValue(number, out _);

                if (!eligible)
                {
                    return track with { Properties = properties };
                }

                var header = evidence[number];
                var compatible = ParseNumber(properties.GetValueOrDefault("audio_sampling_frequency")) == (ulong)header.SamplingFrequency &&
                    track.Channels == header.Channels;

                if (!compatible)
                {
                    return track with { Properties = properties };
                }

                var value = FormattableString.Invariant($"{header.SamplingFrequency}:{header.SamplesPerFrame}:{header.BitsPerSample}:{header.Channels}:{header.FrameBytes}");
                return track with { Properties = properties.Add(EvidenceKey, value) };
            }).ToImmutableArray()
        };
    }

    /// <summary>Allows only the exact rounded codec duration when the source did not declare one and both files prove the same core header.</summary>
    internal static bool PermitsAddition(TrackSemanticComparison comparison)
    {
        var source = comparison.Expected;
        var candidate = comparison.Actual;
        var eligible = IsDts(source) && IsDts(candidate) && !source.Properties.ContainsKey(PropertyName) &&
            source.Properties.GetValueOrDefault(EvidenceKey) == candidate.Properties.GetValueOrDefault(EvidenceKey);

        if (!eligible || !TryReadHeader(source, out var header))
        {
            return false;
        }

        return ParseNumber(candidate.Properties.GetValueOrDefault(PropertyName)) == RoundedDuration(header);
    }

    /// <summary>Reads only validated private evidence; absent or malformed fields cannot authorize a duration addition.</summary>
    internal static bool TryReadHeader(MediaTrack track, out DtsCoreFrameHeader header)
    {
        header = new(0, 0, 0, 0, 0);
        var fields = track.Properties.GetValueOrDefault(EvidenceKey)?.Split(':');

        if (fields is not { Length: 5 })
        {
            return false;
        }

        var values = fields.Select(ParseNumber).ToArray();
        var supported = values[0] is > 0 and <= 192000 && values[1] is 512 or 1024 or 2048 &&
            values[2] is 16 or 20 or 24 && values[3] is > 0 and <= 9 && values[4] is >= 96 and <= 16384;

        if (!supported)
        {
            return false;
        }

        header = new((int)values[0], (int)values[1], (int)values[2], (int)values[3], (int)values[4]);
        return true;
    }

    /// <summary>Identifies DTS only through the inspected codec identifier and category.</summary>
    internal static bool IsDts(MediaTrack track) => track.Kind == TrackKind.Audio &&
        track.Properties.GetValueOrDefault("codec_id") == CodecIdentifier;

    /// <summary>Uses the pinned packetizer's integer-nanosecond codec duration without modifying timestamps or bit-depth metadata.</summary>
    internal static ulong RoundedDuration(DtsCoreFrameHeader header) => (ulong)decimal.Round(
        header.SamplesPerFrame * NanosecondsPerSecond / header.SamplingFrequency, 0, MidpointRounding.AwayFromZero);

    /// <summary>Rejects missing, malformed and zero-valued numeric fields.</summary>
    private static ulong ParseNumber(string? value) => ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
}
