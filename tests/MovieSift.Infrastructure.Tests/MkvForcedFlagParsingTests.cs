using System.Text.Json.Nodes;
using MovieSift.Application;
using MovieSift.Core.Failures;
using MovieSift.Core.Media;
using MovieSift.Infrastructure.MediaTools;
using Xunit;

namespace MovieSift.Infrastructure.Tests;

/// <summary>Separates the Matroska forced-flag default from malformed probe values used by subtitle selection.</summary>
public sealed class MkvForcedFlagParsingTests
{
    private const string Identification = """
        {"identification_format_version":20,"attachments":[],"chapters":[],"global_tags":[],"track_tags":[],
        "container":{"recognized":true,"supported":true,"type":"Matroska","properties":{}},
        "tracks":[{"id":4,"type":"subtitles","codec":"SubRip/SRT","properties":{"uid":123,"language":"eng"}}],
        "errors":[],"warnings":[]}
        """;

    /// <summary>Uses effective false for an omitted subtitle flag while retaining its raw absence.</summary>
    [Fact]
    public void OmittedForcedSubtitleFlagHasEffectiveFalseAndNoInventedRawProperty()
    {
        var track = Assert.Single(Parse(Identification).Tracks);
        Assert.Equal(TrackKind.Subtitles, track.Kind);
        Assert.False(track.IsForced);
        Assert.DoesNotContain("forced_track", track.Properties.Keys);
    }

    /// <summary>Rejects non-boolean forced values instead of treating them as true, false, or absent.</summary>
    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("{}")]
    public void MalformedForcedSubtitleFlagRejectsIdentification(string value)
    {
        var root = JsonNode.Parse(Identification)!;
        root["tracks"]![0]!["properties"]!["forced_track"] = JsonNode.Parse(value);
        var failure = Assert.Throws<OperationFailureException>(() => Parse(root.ToJsonString())).Failure;
        Assert.Equal(ErrorCode.InvalidToolOutput, failure.Code);
    }

    /// <summary>Parses one bounded fixture without opening any media or starting a worker.</summary>
    private static MediaInspection Parse(string json) => MkvIdentification.Parse(new(json, new("synthetic.mkv", "fixture", 1, 1)));
}
