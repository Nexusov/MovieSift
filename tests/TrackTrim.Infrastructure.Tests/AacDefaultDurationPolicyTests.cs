using System.Collections.Immutable;
using TrackTrim.Core.Media;
using TrackTrim.Infrastructure.MediaTools;
using Xunit;

namespace TrackTrim.Infrastructure.Tests;

/// <summary>Constrains inferred AAC timing headers to a recognized unchanged 1024-sample AAC-LC configuration.</summary>
public sealed class AacDefaultDurationPolicyTests
{
    private const string CorrectDuration = "21333333";

    /// <summary>Accepts integer division rounding while rejecting wrong duration, sample rate, profile, and ASC flags.</summary>
    [Theory]
    [InlineData("1188", "48000", CorrectDuration, true)]
    [InlineData("118856e500", "48000", CorrectDuration, true)]
    [InlineData("118856e500", "48000", "21333334", true)]
    [InlineData("1208", "44100", "23219954", true)]
    [InlineData("118856e500", "48000", "21333335", false)]
    [InlineData("118856e500", "48000", "20000000", false)]
    [InlineData("118856e500", "44100", CorrectDuration, false)]
    [InlineData("118c", "48000", CorrectDuration, false)]
    [InlineData("118a", "48000", CorrectDuration, false)]
    [InlineData("1189", "48000", CorrectDuration, false)]
    [InlineData("1180", "48000", CorrectDuration, false)]
    [InlineData("2988", "48000", CorrectDuration, false)]
    [InlineData("1788", "48000", CorrectDuration, false)]
    [InlineData("118856e580", "48000", CorrectDuration, false)]
    [InlineData("1188deadbeef", "48000", CorrectDuration, false)]
    [InlineData("118", "48000", CorrectDuration, false)]
    [InlineData("not-hex", "48000", CorrectDuration, false)]
    [InlineData("1188", "48000", "18446744073709551616", false)]
    public void ChecksDerivedDurationAndKnownConfiguration(string configuration, string frequency, string duration, bool permitted)
    {
        var source = Track(new Configuration(configuration, frequency));
        var actual = source with { Properties = source.Properties.Add(AacDefaultDurationPolicy.PropertyName, duration) };
        Assert.Equal(permitted, AacDefaultDurationPolicy.PermitsAddition(new TrackSemanticComparison(source, actual)));
    }

    /// <summary>Retains strict comparisons for already-present duration and altered codec configuration.</summary>
    [Theory]
    [InlineData("codec_private_data", "1208")]
    [InlineData("audio_sampling_frequency", "44100")]
    [InlineData("codec_id", "A_OPUS")]
    public void RejectsChangedCodecConfiguration(string property, string value)
    {
        var source = Track(new Configuration("118856e500", "48000"));
        var actual = source with
        {
            Properties = source.Properties.SetItem(property, value).Add(AacDefaultDurationPolicy.PropertyName, CorrectDuration),
        };
        Assert.False(AacDefaultDurationPolicy.PermitsAddition(new TrackSemanticComparison(source, actual)));
    }

    /// <summary>Never treats an existing timing-header change or a non-audio track as a permitted addition.</summary>
    [Fact]
    public void RejectsExistingDurationChangesAndNonAudioTracks()
    {
        var source = Track(new Configuration("1188", "48000"));
        var actual = source with { Properties = source.Properties.Add(AacDefaultDurationPolicy.PropertyName, CorrectDuration) };
        Assert.False(AacDefaultDurationPolicy.PermitsAddition(new TrackSemanticComparison(actual,
            actual with { Properties = actual.Properties.SetItem(AacDefaultDurationPolicy.PropertyName, "21333334") })));
        Assert.False(AacDefaultDurationPolicy.PermitsAddition(new TrackSemanticComparison(
            source with { Kind = TrackKind.Video }, actual with { Kind = TrackKind.Video })));
    }

    /// <summary>Builds a minimal source track whose codec properties remain visible to the comparison policy.</summary>
    private static MediaTrack Track(Configuration configuration)
    {
        var properties = ImmutableDictionary<string, string>.Empty.Add("codec_id", "A_AAC")
            .Add("codec_private_data", configuration.Hex).Add("audio_sampling_frequency", configuration.Frequency);
        return new MediaTrack(1, 2, TrackKind.Audio, "AAC", "ger", null, "Original", 1, true, false, true, properties);
    }

    private sealed record Configuration(string Hex, string Frequency);
}
