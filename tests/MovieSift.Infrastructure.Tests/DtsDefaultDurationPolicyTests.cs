using System.Collections.Immutable;
using MovieSift.Core.Media;
using MovieSift.Infrastructure.MediaTools;
using Xunit;

namespace MovieSift.Infrastructure.Tests;

/// <summary>Restricts an added duration to independently corroborated DTS core evidence without relaxing other properties.</summary>
public sealed class DtsDefaultDurationPolicyTests
{
    /// <summary>Accepts only the exact proven missing duration, not arbitrary DTS defaults or changed source defaults.</summary>
    [Theory]
    [InlineData("10666667", true)]
    [InlineData("10666666", false)]
    [InlineData("21333333", false)]
    [InlineData("unknown", false)]
    public void RequiresExactDuration(string duration, bool permitted)
    {
        var source = Track();
        var candidate = source with { Properties = source.Properties.Add("default_duration", duration) };
        Assert.Equal(permitted, DtsDefaultDurationPolicy.PermitsAddition(new(source, candidate)));
        Assert.False(DtsDefaultDurationPolicy.PermitsAddition(new(candidate, candidate)));
    }

    /// <summary>Missing or conflicting computed evidence cannot authorize a metadata exception.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("48000:1024:24:6:96")]
    [InlineData("48000:512:24:6")]
    [InlineData("0:512:24:6:96")]
    public void RequiresMatchingEvidence(string? evidence)
    {
        var source = Track();
        var properties = source.Properties.Remove(DtsDefaultDurationPolicy.EvidenceKey).Add("default_duration", "10666667");

        if (evidence is not null)
        {
            properties = properties.Add(DtsDefaultDurationPolicy.EvidenceKey, evidence);
        }

        Assert.False(DtsDefaultDurationPolicy.PermitsAddition(new(source, source with { Properties = properties })));
    }

    /// <summary>Non-DTS categories cannot use DTS evidence, even if a tool supplies a matching-looking private field.</summary>
    [Fact]
    public void ExcludesOtherCodecsAndCategories()
    {
        var source = Track();
        var candidate = source with { Properties = source.Properties.Add("default_duration", "10666667") };
        Assert.False(DtsDefaultDurationPolicy.PermitsAddition(new(source with { Kind = TrackKind.Video }, candidate)));
        Assert.False(DtsDefaultDurationPolicy.PermitsAddition(new(source,
            candidate with { Properties = candidate.Properties.SetItem("codec_id", "A_AAC") })));
    }

    /// <summary>Tool-provided private-looking fields are removed even on categories not requiring a physical header read.</summary>
    [Fact]
    public async Task RemovesUntrustedComputedEvidenceFromOtherTracks()
    {
        var source = Track() with { Kind = TrackKind.Video };
        var inspection = new MediaInspection(new("not opened", "test", 0, 0), new("MKVToolNix", "102.0", 20),
            [source], [], "", false, false);
        var annotated = await DtsDefaultDurationPolicy.AnnotateAsync(inspection, TestContext.Current.CancellationToken);
        Assert.False(Assert.Single(annotated.Tracks).Properties.ContainsKey(DtsDefaultDurationPolicy.EvidenceKey));
    }

    /// <summary>Builds inspected values only; it does not manufacture encoded media or backend authority.</summary>
    private static MediaTrack Track() => new(6, 123, TrackKind.Audio, "DTS", "eng", null, "Original", 6,
        false, false, true, ImmutableDictionary<string, string>.Empty.Add("codec_id", "A_DTS")
            .Add(DtsDefaultDurationPolicy.EvidenceKey, "48000:512:24:6:96"));
}
