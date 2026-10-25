using System.Collections.Immutable;
using System.Text.Json.Nodes;
using MovieSift.Application;
using MovieSift.Core.Failures;
using MovieSift.Core.Media;
using MovieSift.Infrastructure.MediaTools;
using Xunit;

namespace MovieSift.Infrastructure.Tests;

/// <summary>Requires known ordinary chapter boundaries and agreement with admitted bounded Nero chapter metadata.</summary>
public sealed class Mp4ChapterValidationTests
{
    private const string Probe = """
        {"streams":[{"index":0,"id":"0x1","codec_type":"video","codec_name":"h264","codec_tag_string":"avc1","time_base":"1/10240","disposition":{"default":1,"forced":0,"attached_pic":0},"tags":{"language":"und","handler_name":"VideoHandler"}}],
        "chapters":[{"id":0,"time_base":"1/1000","start":0,"end":1000,"tags":{"title":"Начало"}},
        {"id":1,"time_base":"1/1000","start":1000,"end":2000,"tags":{"title":"Part two"}}],
        "format":{"format_name":"mov,mp4,m4a,3gp,3g2,mj2","duration":"2.000000","tags":{"major_brand":"isom","title":"Chapter test"}}}
        """;

    /// <summary>Does not accept unknown, negative, empty or reversed chapter boundaries as successful preservation evidence.</summary>
    [Theory]
    [InlineData("start", "N/A")]
    [InlineData("end", "N/A")]
    [InlineData("start", "-1")]
    [InlineData("end", "-1")]
    [InlineData("end", "0")]
    [InlineData("end", "9223372036854775808")]
    public void InvalidOrdinaryChapterBoundariesRejectProbe(string field, string value)
    {
        var root = JsonNode.Parse(Probe)!;
        root["chapters"]![0]![field] = value;
        Assert.Equal(ErrorCode.InvalidToolOutput,
            Assert.Throws<OperationFailureException>(() => Parse(root.ToJsonString())).Failure.Code);
    }

    /// <summary>Requires an end strictly after the actual start, independently of the chapter's position in the list.</summary>
    [Fact]
    public void ReversedPositiveChapterBoundariesRejectProbe()
    {
        var root = JsonNode.Parse(Probe)!;
        root["chapters"]![1]!["end"] = 999;
        Assert.Equal(ErrorCode.InvalidToolOutput,
            Assert.Throws<OperationFailureException>(() => Parse(root.ToJsonString())).Failure.Code);
    }

    /// <summary>Rejects a second chapter representation that would be silently omitted or changed by the selected probe.</summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("count")]
    [InlineData("start")]
    [InlineData("title")]
    public void NeroMetadataMustAgreeWithTheProbedOrdinaryChapters(string difference)
    {
        var root = JsonNode.Parse(Probe)!;
        ImmutableArray<Mp4NeroChapter> nero = [new(0, "Начало"), new(10_000_000, "Part two")];

        if (difference == "missing")
        {
            root["chapters"] = new JsonArray();
        }

        if (difference == "count")
        {
            nero = [nero[0]];
        }

        if (difference == "start")
        {
            nero = nero.SetItem(1, nero[1] with { StartTicks = 11_000_000 });
        }

        if (difference == "title")
        {
            nero = nero.SetItem(1, nero[1] with { Title = "Different title" });
        }

        Assert.Equal(ErrorCode.UnsupportedFeature,
            Assert.Throws<OperationFailureException>(() => Parse(root.ToJsonString(), nero)).Failure.Code);
    }

    /// <summary>Compares source-defined 100 ns Nero starts as exact rationals against a different probe timebase.</summary>
    [Fact]
    public void EquivalentNeroAndProbeChaptersAreAdmitted()
    {
        var inspected = Parse(Probe, [new(0, "Начало"), new(10_000_000, "Part two")]);
        Assert.True(inspected.HasChapters);
    }

    /// <summary>Constructs controlled chapter machine data without a media worker or fake approval.</summary>
    private static MediaInspection Parse(string json, ImmutableArray<Mp4NeroChapter> nero = default) => Mp4Probe.Parse(new(json,
        new(Path.GetFullPath("chapters.mp4"), "controlled", 1, 1),
        new("isom", 1000, [new(1, 1, 3, "avc1")], ImmutableHashSet<uint>.Empty, NeroChapters: nero)));
}
