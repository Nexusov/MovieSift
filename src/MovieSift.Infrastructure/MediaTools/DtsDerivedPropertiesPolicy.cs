using System.Collections.Immutable;
using System.Globalization;
using MovieSift.Core.Media;

namespace MovieSift.Infrastructure.MediaTools;

/// <summary>Recognizes absent DTS headers only from matching, bounded source/candidate core-frame evidence.</summary>
internal static class DtsDerivedPropertiesPolicy
{
    internal const string DurationProperty = "default_duration";
    internal const string BitDepthProperty = "audio_bits_per_sample";
    internal const string EvidenceKey = "_moviesift_dts_core_evidence";
    private const string CodecIdentifier = "A_DTS";
    private const decimal NanosecondsPerSecond = 1_000_000_000;
    private const decimal RoundingToleranceNanoseconds = 1;

    /// <summary>Adds private computed evidence, never accepting a tool-provided substitute or rewriting media metadata.</summary>
    internal static async Task<MediaInspection> AnnotateAsync(MediaInspection inspection, CancellationToken cancellationToken)
    {
        var tracks = inspection.Tracks.Where(track => track.Kind == TrackKind.Audio &&
            track.Properties.GetValueOrDefault("codec_id") == CodecIdentifier).ToArray();

        if (tracks.Length == 0)
        {
            return inspection;
        }

        var numbers = tracks.Select(track => ParseNumber(track.Properties.GetValueOrDefault("number")))
            .Where(number => number > 0).ToHashSet();
        var evidence = await DtsCoreHeaderEvidence.ReadAsync(new(inspection.Source.FullPath, numbers, cancellationToken)).ConfigureAwait(false);
        return inspection with
        {
            Tracks = inspection.Tracks.Select(track =>
            {
                var properties = track.Properties.Remove(EvidenceKey);
                var number = ParseNumber(properties.GetValueOrDefault("number"));
                var eligible = track.Kind == TrackKind.Audio && properties.GetValueOrDefault("codec_id") == CodecIdentifier;

                if (!eligible || !evidence.TryGetValue(number, out var header))
                {
                    return track with { Properties = properties };
                }

                var compatible = ParseNumber(properties.GetValueOrDefault("audio_sampling_frequency")) == (ulong)header.SamplingFrequency &&
                    track.Channels == header.Channels && (!properties.TryGetValue(BitDepthProperty, out var bits) ||
                        ParseNumber(bits) == (ulong)header.BitsPerSample);

                if (!compatible)
                {
                    return track with { Properties = properties };
                }

                var value = FormattableString.Invariant($"{header.SamplingFrequency}:{header.SamplesPerFrame}:{header.BitsPerSample}:{header.Channels}:{header.FrameBytes}");
                return track with { Properties = properties.Add(EvidenceKey, value) };
            }).ToImmutableArray()
        };
    }

    /// <summary>Permits an added duration or bit depth only when both independently read core headers prove it.</summary>
    internal static bool PermitsAddition(TrackSemanticComparison comparison, string property)
    {
        var source = comparison.Expected;
        var candidate = comparison.Actual;
        var eligible = source.Kind == TrackKind.Audio && candidate.Kind == TrackKind.Audio &&
            source.Properties.GetValueOrDefault("codec_id") == CodecIdentifier &&
            candidate.Properties.GetValueOrDefault("codec_id") == CodecIdentifier;
        var proof = source.Properties.GetValueOrDefault(EvidenceKey);
        var consistentProof = proof is not null && proof == candidate.Properties.GetValueOrDefault(EvidenceKey);

        if (!eligible || !consistentProof || source.Properties.ContainsKey(property))
        {
            return false;
        }

        var values = proof!.Split(':');

        if (values.Length != 5 || !candidate.Properties.TryGetValue(property, out var actualText))
        {
            return false;
        }

        var rate = ParseNumber(values[0]);
        var samples = ParseNumber(values[1]);
        var bits = ParseNumber(values[2]);
        var actual = ParseNumber(actualText);

        if (rate == 0 || samples is not (512 or 1024 or 2048) || bits is not (16 or 20 or 24) || actual == 0)
        {
            return false;
        }

        return property switch
        {
            DurationProperty => Math.Abs(actual - samples * NanosecondsPerSecond / rate) <= RoundingToleranceNanoseconds,
            BitDepthProperty => actual == bits,
            _ => false
        };
    }

    /// <summary>Rejects missing, malformed and zero-valued numeric proof fields.</summary>
    private static ulong ParseNumber(string? value) => ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
}
