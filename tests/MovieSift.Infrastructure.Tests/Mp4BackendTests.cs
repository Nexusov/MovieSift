using System.Collections.Immutable;
using System.Text.Json.Nodes;
using MovieSift.Application;
using MovieSift.Application.Ports;
using MovieSift.Core.Failures;
using MovieSift.Core.Media;
using MovieSift.Core.Planning;
using MovieSift.Core.Rules;
using MovieSift.Infrastructure.MediaTools;
using MovieSift.Infrastructure.Processes;
using Xunit;

namespace MovieSift.Infrastructure.Tests;

/// <summary>Checks normalized outcomes, exact arguments and untrusted structured MP4 facts without running media workers.</summary>
public sealed class Mp4BackendTests
{
    private const string Probe = """
        {"streams":[
        {"index":3,"id":"0x7","codec_type":"video","codec_name":"h264","codec_tag_string":"avc1","time_base":"1/10240","disposition":{"default":1,"forced":0,"attached_pic":0},"tags":{"language":"und","handler_name":"VideoHandler"}},
        {"index":8,"id":"0x9","codec_type":"audio","codec_name":"aac","codec_tag_string":"mp4a","channels":2,"time_base":"1/48000","disposition":{"default":0,"forced":0,"attached_pic":0},"tags":{"language":"eng","name":"Actual title","handler_name":"Different handler"}},
        {"index":12,"id":"0xb","codec_type":"subtitle","codec_name":"mov_text","codec_tag_string":"tx3g","time_base":"1/1000000","disposition":{"default":0,"forced":1,"attached_pic":0},"tags":{"language":"rus","handler_name":"Captions"}}],
        "chapters":[],"format":{"format_name":"mov,mp4,m4a,3gp,3g2,mj2","duration":"2.000000","tags":{"major_brand":"isom","minor_version":"512","compatible_brands":"isommp41","title":"Container title","artist":"Original author"}}}
        """;

    /// <summary>Retains actual stream indexes independently of optional Matroska UIDs and MP4 container track_IDs.</summary>
    [Fact]
    public void ProbeSeparatesStreamIndexFromContainerIdentityAndKeepsActualTitle()
    {
        var inspection = Parse(Probe);
        Assert.Equal(MediaContainer.Mp4, inspection.Container);
        Assert.Equal(new[] { 3, 8, 12 }, inspection.Tracks.Select(track => track.Id));
        Assert.All(inspection.Tracks, track => Assert.Null(track.Uid));
        Assert.Equal("9", inspection.Tracks[1].Properties["container_track_id"]);
        Assert.Equal("Actual title", inspection.Tracks[1].Title);
        Assert.True(inspection.Tracks[0].IsEnabled);
        Assert.False(inspection.Tracks[1].IsEnabled);
        Assert.True(inspection.Tracks[2].IsForced);
        Assert.Equal("Container title", inspection.Title);
    }

    /// <summary>Rejects malformed forced values instead of inventing a boolean effective flag.</summary>
    [Theory]
    [InlineData("\"true\"")]
    [InlineData("2")]
    [InlineData("null")]
    public void InvalidForcedFlagRejectsProbe(string value)
    {
        var root = JsonNode.Parse(Probe)!;
        root["streams"]![2]!["disposition"]!["forced"] = JsonNode.Parse(value);
        Assert.Throws<OperationFailureException>(() => Parse(root.ToJsonString()));
    }

    /// <summary>Does not invent effective booleans when the pinned probe omitted a required disposition field.</summary>
    [Theory]
    [InlineData("default")]
    [InlineData("forced")]
    [InlineData("attached_pic")]
    public void MissingRequiredDispositionIsInvalidToolOutput(string property)
    {
        var root = JsonNode.Parse(Probe)!;
        root["streams"]![0]!["disposition"]!.AsObject().Remove(property);
        Assert.Equal(ErrorCode.InvalidToolOutput,
            Assert.Throws<OperationFailureException>(() => Parse(root.ToJsonString())).Failure.Code);
    }

    /// <summary>Rejects syntactically valid null required strings without escaping as a null-reference startup failure.</summary>
    [Theory]
    [InlineData("time_base")]
    [InlineData("id")]
    [InlineData("codec_name")]
    public void NullRequiredProbeStringIsInvalidOutput(string property)
    {
        var root = JsonNode.Parse(Probe)!;
        root["streams"]![0]![property] = null;
        Assert.Equal(ErrorCode.InvalidToolOutput,
            Assert.Throws<OperationFailureException>(() => Parse(root.ToJsonString())).Failure.Code);
    }

    /// <summary>Does not manufacture a blank title or language from a null tool tag.</summary>
    [Fact]
    public void NullMetadataValueIsInvalidOutput()
    {
        var root = JsonNode.Parse(Probe)!;
        root["streams"]![1]!["tags"]!["language"] = null;
        Assert.Equal(ErrorCode.InvalidToolOutput,
            Assert.Throws<OperationFailureException>(() => Parse(root.ToJsonString())).Failure.Code);
    }

    /// <summary>Matches actual title/name metadata without treating a preserved handler label as a user-authored track title.</summary>
    [Theory]
    [InlineData("title", true)]
    [InlineData("name", true)]
    [InlineData("handler only", false)]
    public void TitleRulesMatchActualTitlesButNeverHandlerFallback(string field, bool retained)
    {
        var root = JsonNode.Parse(Probe)!;

        if (field != "handler only")
        {
            root["streams"]![2]!["tags"]![field] = "Captions";
        }

        var inspection = Parse(root.ToJsonString());
        var rules = new LanguageRules(new(LanguageRuleMode.Unchanged, []),
            new(LanguageRuleMode.KeepMatching, ["ru"], new(true, "Captions")));
        var proposal = LanguageRuleMatcher.Preview(new(inspection, inspection.Tracks.Select(track => track.Id).ToImmutableArray(), rules));
        Assert.Empty(proposal.Issues);
        Assert.Equal(retained, proposal.RetainedTrackIds.Contains(12));
        Assert.Equal(!retained, proposal.NoSubtitleMatches);
        Assert.Contains("Captions", inspection.Tracks[2].Properties["tags"], StringComparison.Ordinal);
        Assert.Equal(retained ? "Captions" : string.Empty, inspection.Tracks[2].Title);
    }

    /// <summary>Refuses unsupported codecs, unknown effective dispositions and advanced HDR facts at inspection.</summary>
    [Theory]
    [InlineData("codec_name", "hevc")]
    [InlineData("color_transfer", "smpte2084")]
    public void UnsupportedVideoFactsRejectInspection(string property, string value)
    {
        var root = JsonNode.Parse(Probe)!;
        root["streams"]![0]![property] = value;
        var failure = Assert.Throws<OperationFailureException>(() => Parse(root.ToJsonString())).Failure;
        Assert.Equal(ErrorCode.UnsupportedFeature, failure.Code);
    }

    /// <summary>Does not reinterpret an arbitrary enabled flag or unreferenced timed stream as ordinary media.</summary>
    [Fact]
    public void UnsupportedTimedDataAndDispositionRejectInspection()
    {
        var root = JsonNode.Parse(Probe)!;
        root["streams"]![2]!["codec_type"] = "data";
        Assert.Equal(ErrorCode.UnsupportedFeature, Assert.Throws<OperationFailureException>(() => Parse(root.ToJsonString())).Failure.Code);
        root = JsonNode.Parse(Probe)!;
        root["streams"]![1]!["disposition"]!["dependent"] = 1;
        Assert.Equal(ErrorCode.UnsupportedFeature, Assert.Throws<OperationFailureException>(() => Parse(root.ToJsonString())).Failure.Code);
    }

    /// <summary>Uses literal explicit maps and stream copy while retaining source timestamps and disabling interactive/external access.</summary>
    [Fact]
    public void RemuxArgumentsMapTheApprovedLocalIndexesAndPreserveMetadataDeliberately()
    {
        var inspection = Parse(Probe);
        var plan = new RemuxPlan(Guid.NewGuid(), inspection, [inspection.Tracks[0], inspection.Tracks[2]],
            Path.GetFullPath("result.mp4"), false);
        var arguments = FfmpegArguments.BuildRemux(new(plan, Path.GetFullPath("candidate.mp4"), null)).ToArray();
        var maps = arguments.Select((value, index) => (value, index)).Where(pair => pair.value == "-map")
            .Select(pair => arguments[pair.index + 1]).ToArray();
        Assert.Equal(new[] { "0:3", "0:12" }, maps);
        Assert.Contains("-nostdin", arguments);
        Assert.Contains("-n", arguments);
        Assert.Contains("-copyts", arguments);
        Assert.Equal("file", arguments[Array.IndexOf(arguments, "-protocol_whitelist") + 1]);
        Assert.Equal("mov", arguments[Array.IndexOf(arguments, "-f") + 1]);
        Assert.Equal("0", arguments[Array.IndexOf(arguments, "-enable_drefs") + 1]);
        Assert.Equal("copy", arguments[Array.IndexOf(arguments, "-c") + 1]);
        Assert.Equal("disabled", arguments[Array.IndexOf(arguments, "-avoid_negative_ts") + 1]);
        Assert.Equal("0", arguments[Array.IndexOf(arguments, "-write_btrt") + 1]);
        Assert.DoesNotContain("-shortest", arguments);
        Assert.DoesNotContain("-start_at_zero", arguments);
    }

    /// <summary>Preserves Qt name atoms by mapping the demuxer's name tag to the muxer's explicit title field.</summary>
    [Fact]
    public void DemuxedTrackNameIsExplicitlyPreservedAsMuxerTitle()
    {
        var inspection = Parse(Probe);
        var plan = new RemuxPlan(Guid.NewGuid(), inspection, inspection.Tracks, Path.GetFullPath("result.mp4"), false);
        var arguments = FfmpegArguments.BuildRemux(new(plan, Path.GetFullPath("candidate.mp4"), null));
        Assert.Contains("title=Actual title", arguments);
    }

    /// <summary>Never assigns MKV's overridable warning meaning to FFmpeg's exit one or successful warnings.</summary>
    [Theory]
    [InlineData(0, "", false, ToolDisposition.Success)]
    [InlineData(0, "", true, ToolDisposition.Success)]
    [InlineData(0, "A real warning", false, ToolDisposition.Failure)]
    [InlineData(1, "", false, ToolDisposition.Failure)]
    [InlineData(2, "error", false, ToolDisposition.Failure)]
    public void ExitAndDiagnosticPolicyIsExplicit(int exitCode, string diagnostics, bool stdoutTruncated, ToolDisposition expected)
    {
        Assert.Equal(expected, FfmpegDiagnostics.Classify(new(exitCode, "out_time_us=2000000", diagnostics, stdoutTruncated), null));
    }

    /// <summary>Allows only the pinned raw-name compatibility message backed by actual inspected name metadata; no arbitrary suffix is ignored.</summary>
    [Theory]
    [InlineData("00000A1B")]
    [InlineData("0x00000a1b")]
    public void KnownNameDiagnosticRequiresItsActualInspectedRepresentation(string address)
    {
        var diagnostic = $"[in#0 @ {address}] UDTA parsing failed retrying raw\n";
        var inspection = Parse(Probe);
        Assert.Equal(ToolDisposition.Success, FfmpegDiagnostics.Classify(new(0, "", diagnostic, false), inspection));
        Assert.Equal(ToolDisposition.Failure, FfmpegDiagnostics.Classify(new(0, "", diagnostic + "Another warning", false), inspection));
        var unnamed = inspection with
        {
            Tracks = inspection.Tracks.Select(track => track with
            {
                Properties = track.Properties.SetItem("tags", "{}"),
            }).ToImmutableArray(),
        };
        Assert.Equal(ToolDisposition.Failure, FfmpegDiagnostics.Classify(new(0, "", diagnostic, false), unnamed));
        Assert.Equal(ToolDisposition.Failure, FfmpegDiagnostics.Classify(new(0, "", diagnostic, false, StandardErrorTruncated: true), inspection));
        Assert.Equal(ToolDisposition.Failure, FfmpegDiagnostics.Classify(new(0, "", diagnostic, false, ProtocolLineTruncated: true), inspection));
    }

    /// <summary>Accepts the synthetic cover timescale message only when admitted artwork is present, never for an ordinary media stream.</summary>
    [Fact]
    public void CoverDiagnosticRequiresArtworkAndDoesNotOverrideNonzeroExit()
    {
        const string diagnostic = "[in#0 @ 00000A1B] stream 0, timescale not set";
        var inspection = Parse(Probe);
        var cover = inspection.Tracks[0] with { Id = 99, Codec = "png", Properties = inspection.Tracks[0].Properties.SetItem("attached_pic", "1") };
        var withArtwork = inspection with { Tracks = inspection.Tracks.Add(cover) };
        Assert.Equal(ToolDisposition.Failure, FfmpegDiagnostics.Classify(new(0, "", diagnostic, false), inspection));
        Assert.Equal(ToolDisposition.Success, FfmpegDiagnostics.Classify(new(0, "", diagnostic, false), withArtwork));
        Assert.Equal(ToolDisposition.Failure, FfmpegDiagnostics.Classify(new(1, "", diagnostic, false), withArtwork));
    }

    /// <summary>Uses exact rational equality rather than a rounded milliseconds/seconds duration test.</summary>
    [Theory]
    [InlineData("1024", "1/48000", "8/375")]
    [InlineData("-2048", "1/10240", "-1/5")]
    [InlineData("0", "1/44100", "0/1")]
    public void RationalTimestampsAreExact(string value, string timebase, string expected)
    {
        Assert.Equal(expected, Mp4Probe.RationalTimestamp(value, timebase));
    }

    /// <summary>Computes finite fixed-field fingerprints and includes AAC initial skip rather than comparing only packet counts.</summary>
    [Fact]
    public void PacketFingerprintsIncludePayloadAndPrimingAndNormalizeOnlyExactTimebaseRescaling()
    {
        var track = Parse(Probe).Tracks[1];
        using var source = new Mp4PacketEvidence([track]);
        using var output = new Mp4PacketEvidence([track with { Id = 1, Properties = track.Properties.SetItem("time_base", "1/96000") }]);
        using var changed = new Mp4PacketEvidence([track]);
        var payload = "SHA256:" + new string('a', 64);
        const string initialSkip = "|side_datum/skip_samples:side_data_type=Skip Samples|side_datum/skip_samples:skip_samples=1024|side_datum/skip_samples:discard_padding=0|side_datum/skip_samples:skip_reason=0|side_datum/skip_samples:discard_reason=0";
        const string noSkip = "|side_datum/skip_samples:side_data_type=Skip Samples|side_datum/skip_samples:skip_samples=0|side_datum/skip_samples:discard_padding=0|side_datum/skip_samples:skip_reason=0|side_datum/skip_samples:discard_reason=0";
        source.AcceptLine($"stream_index=8|pts=-1024|dts=-1024|duration=1024|size=200|flags=KD_|data_hash={payload}{initialSkip}");
        output.AcceptLine($"stream_index=1|pts=-2048|dts=-2048|duration=2048|size=200|flags=KD_|data_hash={payload}{initialSkip}");
        changed.AcceptLine($"stream_index=8|pts=-1024|dts=-1024|duration=1024|size=200|flags=KD_|data_hash={payload}{noSkip}");
        Assert.Equal(source.Finish()[8], output.Finish()[1]);
        using var original = new Mp4PacketEvidence([track]);
        original.AcceptLine($"stream_index=8|pts=-1024|dts=-1024|duration=1024|size=200|flags=KD_|data_hash={payload}{initialSkip}");
        Assert.NotEqual(original.Finish()[8], changed.Finish()[8]);
    }

    /// <summary>Creates only controlled machine data; real MP4 coverage is in the separately provisioned media suite.</summary>
    private static MediaInspection Parse(string json) => Mp4Probe.Parse(new(json,
        new(Path.GetFullPath("source.mp4"), "controlled", 1, 1),
        new("isom", 1000, [new(7, 1, 3, "avc1"), new(9, 1, 2, "mp4a"), new(11, 1, 2, "tx3g")], ImmutableHashSet<uint>.Empty)));
}
