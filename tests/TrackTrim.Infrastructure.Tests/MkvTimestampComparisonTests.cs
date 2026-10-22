using System.Collections.Immutable;
using TrackTrim.Core.Media;
using TrackTrim.Infrastructure.MediaTools;
using Xunit;

namespace TrackTrim.Infrastructure.Tests;

/// <summary>Checks the separate synthetic terminal boundary without weakening actual timestamp or count preservation.</summary>
public sealed class MkvTimestampComparisonTests
{
    private const string AacDuration = "21333333";

    /// <summary>Permits the unknown-to-derived AAC end only when every real timestamp and frame count still matches.</summary>
    [Theory]
    [InlineData("0|21|42|42", "0|21.333333|42.666666|63.999999", true)]
    [InlineData("0|21|42|42", "0|21.333333|42.666666|85.333332", false)]
    [InlineData("0|21|42|58", "0|21.333333|42.666666|63.999999", false)]
    [InlineData("0|21|42|42", "0|46.333333|67.666666|88.999999", false)]
    [InlineData("0|21|42|42", "0|21.333333|63.999999", false)]
    [InlineData("0|21|42|42", "0|21.333333|42.666666|63.999999|85.333332", false)]
    [InlineData("0|21|42|42", "0|21.333333|44|65.333333", false)]
    [InlineData("0|21|42|42", "0|NaN|42.666666|63.999999", false)]
    public async Task ChecksUnknownEndWithoutLosingFrameEvidence(string sourceValues, string candidateValues, bool equivalent)
    {
        var sourceTrack = SourceTrack();
        var candidateTrack = sourceTrack with { Properties = sourceTrack.Properties.Add("default_duration", AacDuration) };
        Assert.Equal(equivalent, await CompareAsync(new TimestampCase(sourceValues, candidateValues, sourceTrack, candidateTrack)));
    }

    /// <summary>Retains strict known-end comparisons for existing AAC timing and non-AAC media.</summary>
    [Fact]
    public async Task RejectsUnprovenUnknownBoundaryExceptions()
    {
        var sourceTrack = SourceTrack();
        var withDuration = sourceTrack with { Properties = sourceTrack.Properties.Add("default_duration", AacDuration) };
        Assert.False(await CompareAsync(new TimestampCase("0|21|42|42", "0|21.333333|42.666666|63.999999",
            withDuration, withDuration)));
        Assert.False(await CompareAsync(new TimestampCase("0|21|42|42", "0|21.333333|42.666666|63.999999",
            sourceTrack with { Kind = TrackKind.Video }, withDuration with { Kind = TrackKind.Video })));
    }

    /// <summary>Creates trusted timestamp output files and invokes the production streaming comparator.</summary>
    private static async Task<bool> CompareAsync(TimestampCase request)
    {
        using var workspace = new TestWorkspace();
        var source = Path.Combine(workspace.Path, "source.timestamps.txt");
        var candidate = Path.Combine(workspace.Path, "candidate.timestamps.txt");
        await File.WriteAllTextAsync(source, "# timestamp format v2\n" + request.Source.Replace('|', '\n') + "\n",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(candidate, "# timestamp format v2\n" + request.Candidate.Replace('|', '\n') + "\n",
            TestContext.Current.CancellationToken);
        return await MkvTimestampComparison.EquivalentAsync(new TrackTimestampComparison(source, candidate,
            request.SourceTrack, request.CandidateTrack, TestContext.Current.CancellationToken));
    }

    /// <summary>Builds a recognized original AAC-LC track with no declared default duration.</summary>
    private static MediaTrack SourceTrack()
    {
        var properties = ImmutableDictionary<string, string>.Empty.Add("codec_id", "A_AAC")
            .Add("codec_private_data", "118856e500").Add("audio_sampling_frequency", "48000");
        return new MediaTrack(1, 2, TrackKind.Audio, "AAC", "ger", null, "Original", 1, true, false, true, properties);
    }

    private sealed record TimestampCase(string Source, string Candidate, MediaTrack SourceTrack, MediaTrack CandidateTrack);
}
