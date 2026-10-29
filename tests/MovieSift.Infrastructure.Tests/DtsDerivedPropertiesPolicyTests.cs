using System.Collections.Immutable;
using MovieSift.Core.Media;
using MovieSift.Infrastructure.MediaTools;
using Xunit;

namespace MovieSift.Infrastructure.Tests;

/// <summary>Limits absent DTS header additions to independently established matching core-frame evidence.</summary>
public sealed class DtsDerivedPropertiesPolicyTests
{
    /// <summary>Accepts only the proven integer-nanosecond duration and source core bit depth.</summary>
    [Theory]
    [InlineData("default_duration", "10666666", true)]
    [InlineData("default_duration", "10666667", true)]
    [InlineData("default_duration", "10666668", false)]
    [InlineData("default_duration", "20000000", false)]
    [InlineData("audio_bits_per_sample", "24", true)]
    [InlineData("audio_bits_per_sample", "16", false)]
    [InlineData("audio_bits_per_sample", "0", false)]
    [InlineData("codec_private_length", "0", false)]
    public void RequiresProvenAddedProperty(string property, string value, bool permitted)
    {
        var source = Track();
        var actual = source with { Properties = source.Properties.Add(property, value) };
        Assert.Equal(permitted, DtsDerivedPropertiesPolicy.PermitsAddition(new(source, actual), property));
    }

    /// <summary>Does not authorize existing metadata changes, missing/different proofs or another codec/category.</summary>
    [Fact]
    public void RejectsExistingHeadersAndUnprovenComparisons()
    {
        const string property = "default_duration";
        var source = Track();
        var actual = source with { Properties = source.Properties.Add(property, "10666667") };
        var cases = new[]
        {
            new TrackSemanticComparison(actual, actual),
            new(source with { Properties = source.Properties.Remove(DtsDerivedPropertiesPolicy.EvidenceKey) }, actual),
            new(source, actual with { Properties = actual.Properties.SetItem(DtsDerivedPropertiesPolicy.EvidenceKey, "48000:1024:24:6:2013") }),
            new(source with { Kind = TrackKind.Video }, actual),
            new(source with { Properties = source.Properties.SetItem("codec_id", "A_AC3") }, actual)
        };
        Assert.All(cases, comparison => Assert.False(DtsDerivedPropertiesPolicy.PermitsAddition(comparison, property)));
    }

    /// <summary>Builds controlled private evidence; production obtains it exclusively from bounded file reads.</summary>
    private static MediaTrack Track() => new(6, 123, TrackKind.Audio, "DTS", "eng", null, "Original", 6,
        false, false, true, ImmutableDictionary<string, string>.Empty.Add("codec_id", "A_DTS")
            .Add("audio_sampling_frequency", "48000").Add(DtsDerivedPropertiesPolicy.EvidenceKey, "48000:512:24:6:2013"));
}
