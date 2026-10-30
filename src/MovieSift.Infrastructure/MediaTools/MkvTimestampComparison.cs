using System.Globalization;
using MovieSift.Core.Media;

namespace MovieSift.Infrastructure.MediaTools;

/// <summary>Compares every actual frame timestamp and the extractor's separate synthetic end boundary.</summary>
internal static class MkvTimestampComparison
{
    private const string RequiredHeader = "# timestamp format v2";
    private const double TimestampToleranceMilliseconds = 1;
    private const double NanosecondsPerMillisecond = 1_000_000;

    /// <summary>Retains frame-count and timestamp checks while recognizing only a proven unknown codec end boundary.</summary>
    internal static async Task<bool> EquivalentAsync(TrackTimestampComparison request)
    {
        using var sourceReader = File.OpenText(request.SourcePath);
        using var candidateReader = File.OpenText(request.CandidatePath);
        var sourceHeader = await sourceReader.ReadLineAsync(request.CancellationToken).ConfigureAwait(false);
        var candidateHeader = await candidateReader.ReadLineAsync(request.CancellationToken).ConfigureAwait(false);

        if (sourceHeader != RequiredHeader || candidateHeader != RequiredHeader)
        {
            return false;
        }

        var sourceLine = await sourceReader.ReadLineAsync(request.CancellationToken).ConfigureAwait(false);
        var candidateLine = await candidateReader.ReadLineAsync(request.CancellationToken).ConfigureAwait(false);
        double? lastSourceFrame = null;
        double? lastCandidateFrame = null;

        while (true)
        {

            if (sourceLine is null || candidateLine is null)
            {
                return sourceLine is null && candidateLine is null && lastSourceFrame is null;
            }

            var sourceTimestamp = ParseTimestamp(sourceLine);
            var candidateTimestamp = ParseTimestamp(candidateLine);

            if (sourceTimestamp is not { } sourceValue || candidateTimestamp is not { } candidateValue)
            {
                return false;
            }

            var nextSourceLine = await sourceReader.ReadLineAsync(request.CancellationToken).ConfigureAwait(false);
            var nextCandidateLine = await candidateReader.ReadLineAsync(request.CancellationToken).ConfigureAwait(false);

            if (nextSourceLine is null || nextCandidateLine is null)
            {
                var completeFiles = nextSourceLine is null && nextCandidateLine is null && lastSourceFrame is not null;

                if (!completeFiles)
                {
                    return false;
                }

                return WithinTimestampTolerance(new TimestampValues(sourceValue, candidateValue))
                    || IsUnknownCodecEnd(new EndBoundary(request, sourceValue, candidateValue,
                        lastSourceFrame!.Value, lastCandidateFrame!.Value));
            }

            if (!WithinTimestampTolerance(new TimestampValues(sourceValue, candidateValue)))
            {
                return false;
            }

            lastSourceFrame = sourceValue;
            lastCandidateFrame = candidateValue;
            sourceLine = nextSourceLine;
            candidateLine = nextCandidateLine;
        }
    }

    /// <summary>Recognizes the extractor's final unknown-duration sentinel only after independently proven AAC or DTS header addition.</summary>
    private static bool IsUnknownCodecEnd(EndBoundary request)
    {
        // Pinned102 src/extract/tracks.cpp close_timestamp_files writes one end boundary after all frames.
        // An absent SimpleBlock duration yields an exact duplicate of the last frame timestamp.
        var unknownSourceEnd = request.SourceEnd == request.LastSourceFrame;

        var comparison = new TrackSemanticComparison(request.Comparison.SourceTrack, request.Comparison.CandidateTrack);
        var provenAddition = AacDefaultDurationPolicy.PermitsAddition(comparison) || DtsDefaultDurationPolicy.PermitsAddition(comparison);

        if (!unknownSourceEnd || !provenAddition)
        {
            return false;
        }

        var duration = ulong.Parse(request.Comparison.CandidateTrack.Properties[AacDefaultDurationPolicy.PropertyName],
            CultureInfo.InvariantCulture) / NanosecondsPerMillisecond;
        return WithinTimestampTolerance(new TimestampValues(request.LastCandidateFrame + duration, request.CandidateEnd));
    }

    /// <summary>Parses a finite invariant timestamp emitted by the pinned extractor.</summary>
    private static double? ParseTimestamp(string text)
    {
        var validTimestamp = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && double.IsFinite(value);
        return validTimestamp ? value : null;
    }

    /// <summary>Applies only the established one-millisecond Matroska tick-rounding allowance.</summary>
    private static bool WithinTimestampTolerance(TimestampValues request)
    {
        return Math.Abs(request.Expected - request.Actual) <= TimestampToleranceMilliseconds;
    }

    private sealed record TimestampValues(double Expected, double Actual);
    private sealed record EndBoundary(TrackTimestampComparison Comparison, double SourceEnd, double CandidateEnd,
        double LastSourceFrame, double LastCandidateFrame);
}

internal sealed record TrackTimestampComparison(string SourcePath, string CandidatePath,
    MediaTrack SourceTrack, MediaTrack CandidateTrack, CancellationToken CancellationToken);
