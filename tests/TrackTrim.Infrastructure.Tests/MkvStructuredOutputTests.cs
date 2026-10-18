using System.Text.Json.Nodes;
using System.Xml.Linq;
using TrackTrim.Application;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Media;
using TrackTrim.Infrastructure.MediaTools;
using Xunit;

namespace TrackTrim.Infrastructure.Tests;

/// <summary>Checks malformed structured output and semantic metadata policies independently of worker timing.</summary>
public sealed class MkvStructuredOutputTests
{
    private const string MinimalIdentification = """
        {"identification_format_version":20,"attachments":[],"chapters":[],"global_tags":[],"track_tags":[],
        "container":{"recognized":true,"supported":true,"type":"Matroska","properties":{}},
        "tracks":[{"id":4,"type":"video","codec":"VP8","properties":{"uid":123,"codec_id":"V_VP8"}}],
        "errors":[],"warnings":[]}
        """;

    /// <summary>Parses complete text fixtures captured from the actual pinned tools.</summary>
    [Fact]
    public void ParsesActualPinnedToolJsonAndCompleteChapterTagXml()
    {
        var directory = Path.Combine(MediaTestEnvironment.RepositoryDirectory, "tests", "TrackTrim.Infrastructure.Tests", "Fixtures");
        var inspection = Parse(File.ReadAllText(Path.Combine(directory, "ordinary.identification.json")));
        Assert.Equal(4, inspection.Tracks.Length);
        Assert.True(inspection.HasChapters);
        Assert.True(inspection.HasTags);
        MkvMetadataXml.ValidateChapters(MkvMetadataXml.Read(Path.Combine(directory, "ordinary.chapters.xml")));
        MkvMetadataXml.ValidateTags(MkvMetadataXml.Read(Path.Combine(directory, "ordinary.tags.xml")));
    }

    /// <summary>Rejects incomplete or malformed structured identification rather than guessing defaults.</summary>
    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void RejectsMalformedOrIncompleteJson(string json)
    {
        var exception = Assert.Throws<OperationFailureException>(() => Parse(json));
        Assert.Equal(ErrorCode.InvalidToolOutput, exception.Failure.Code);
    }

    /// <summary>Enforces the output bound and the exact identification schema version.</summary>
    [Fact]
    public void RejectsExcessiveJsonAndUnsupportedFormat()
    {
        Assert.Throws<OperationFailureException>(() => Parse(new string(' ', MkvIdentification.MaximumJsonCharacters + 1)));
        var root = JsonNode.Parse(MinimalIdentification)!;
        root["identification_format_version"] = 19;
        var exception = Assert.Throws<OperationFailureException>(() => Parse(root.ToJsonString()));
        Assert.Equal(ErrorCode.InvalidToolOutput, exception.Failure.Code);
    }

    /// <summary>Retains raw field absence separately from documented Matroska defaults.</summary>
    [Fact]
    public void KeepsLocalIdSeparateFromUidAndAppliesDocumentedFlagDefaults()
    {
        var inspection = Parse(MinimalIdentification);
        var track = Assert.Single(inspection.Tracks);
        Assert.Equal(4, track.Id);
        Assert.Equal(123UL, track.Uid);
        Assert.True(track.IsDefault);
        Assert.True(track.IsEnabled);
        Assert.False(track.IsForced);
        Assert.DoesNotContain("default_track", track.Properties.Keys);
        Assert.Equal("eng", track.Language);
    }

    /// <summary>Rejects concrete advanced transfer characteristics and linked-segment metadata.</summary>
    [Fact]
    public void RejectsDetectedHdrAndLinkedSegments()
    {
        var root = JsonNode.Parse(MinimalIdentification)!;
        root["tracks"]![0]!["properties"]!["color_transfer_characteristics"] = 16;
        var exception = Assert.Throws<OperationFailureException>(() => Parse(root.ToJsonString()));
        Assert.Equal(ErrorCode.UnsupportedFeature, exception.Failure.Code);
        root = JsonNode.Parse(MinimalIdentification)!;
        root["container"]!["properties"]!["next_segment_uid"] = "linked";
        Assert.Equal(ErrorCode.UnsupportedFeature,
            Assert.Throws<OperationFailureException>(() => Parse(root.ToJsonString())).Failure.Code);
    }

    /// <summary>Rejects both known HDR light-level properties exposed by the pinned schema.</summary>
    [Theory]
    [InlineData("max_content_light")]
    [InlineData("max_frame_light")]
    public void RejectsDetectedHdrLightLevelProperties(string propertyName)
    {
        var root = JsonNode.Parse(MinimalIdentification)!;
        root["tracks"]![0]!["properties"]![propertyName] = 1000;
        var exception = Assert.Throws<OperationFailureException>(() => Parse(root.ToJsonString()));
        Assert.Equal(ErrorCode.UnsupportedFeature, exception.Failure.Code);
    }

    /// <summary>Disables external XML resolution and rejects excessive nesting before loading a tree.</summary>
    [Fact]
    public void XmlDisablesExternalEntityResolutionAndRejectsExcessiveDepth()
    {
        using var workspace = new TestWorkspace();
        var secretPath = Path.Combine(workspace.Path, "secret.txt");
        File.WriteAllText(secretPath, "must never be resolved");
        var xmlPath = Path.Combine(workspace.Path, "metadata.xml");
        File.WriteAllText(xmlPath, $"<!DOCTYPE Tags [<!ENTITY external SYSTEM '{new Uri(secretPath).AbsoluteUri}'>]><Tags>&external;</Tags>");
        Assert.Throws<OperationFailureException>(() => MkvMetadataXml.Read(xmlPath));
        File.WriteAllText(xmlPath, string.Concat(Enumerable.Repeat("<nested>", 40))
            + string.Concat(Enumerable.Repeat("</nested>", 40)));
        Assert.Throws<OperationFailureException>(() => MkvMetadataXml.Read(xmlPath));
    }

    /// <summary>Preserves global/retained targets and detects real semantic or attribute changes.</summary>
    [Fact]
    public void XmlRetainsSemanticTagsAndRemovesOnlyDeletedTrackTargets()
    {
        var source = XDocument.Parse("<Tags><Tag><Targets/><Simple><Name>COMMENT</Name><String>global</String></Simple></Tag>"
            + "<Tag><Targets><TrackUID>12</TrackUID></Targets><Simple><Name>COMMENT</Name><String>retained</String></Simple></Tag>"
            + "<Tag><Targets><TrackUID>13</TrackUID></Targets><Simple><Name>COMMENT</Name><String>removed</String></Simple></Tag></Tags>");
        var expected = MkvMetadataXml.ExpectedTags(new MkvMetadataXml.TagExpectation(source,
            new Dictionary<ulong, ulong> { [12] = 112 }));
        Assert.Equal(2, expected.Descendants("Tag").Count());
        Assert.Equal("112", Assert.Single(expected.Descendants("TrackUID")).Value);
        var changed = new XDocument(expected);
        changed.Descendants("String").First().Value = "different";
        Assert.False(MkvMetadataXml.Equivalent(new MkvMetadataXml.XmlComparison(expected, changed)));
        changed = new XDocument(expected);
        changed.Root!.SetAttributeValue("extra", "changed");
        Assert.False(MkvMetadataXml.Equivalent(new MkvMetadataXml.XmlComparison(expected, changed)));
    }

    /// <summary>Rejects mixed and dangling references that need a separate post-removal semantics policy.</summary>
    [Fact]
    public void RejectsMixedAndDanglingTagTargets()
    {
        var mixedTargets = XDocument.Parse("<Tags><Tag><Targets><TrackUID>12</TrackUID><AttachmentUID>13</AttachmentUID></Targets></Tag></Tags>");
        Assert.Equal(ErrorCode.UnsupportedFeature,
            Assert.Throws<OperationFailureException>(() => MkvMetadataXml.ValidateTags(mixedTargets)).Failure.Code);
        var danglingTarget = XDocument.Parse("<Tags><Tag><Targets><TrackUID>99</TrackUID></Targets></Tag></Tags>");
        Assert.Equal(ErrorCode.UnsupportedFeature, Assert.Throws<OperationFailureException>(() =>
            MkvMetadataXml.ValidateTrackTargets(new MkvMetadataXml.TrackTargetValidation(danglingTarget,
                new HashSet<ulong> { 12 }))).Failure.Code);
    }

    /// <summary>Rejects chapter semantics that the current remux policy cannot preserve safely.</summary>
    [Theory]
    [InlineData("<ChapterSegmentUID>123</ChapterSegmentUID>")]
    [InlineData("<EditionFlagOrdered>1</EditionFlagOrdered>")]
    [InlineData("<ChapterTrack><ChapterTrackNumber>1</ChapterTrackNumber></ChapterTrack>")]
    public void RejectsUnsupportedChapterReferences(string element)
    {
        var chapters = XDocument.Parse($"<Chapters><EditionEntry>{element}</EditionEntry></Chapters>");
        Assert.Equal(ErrorCode.UnsupportedFeature,
            Assert.Throws<OperationFailureException>(() => MkvMetadataXml.ValidateChapters(chapters)).Failure.Code);
    }

    /// <summary>Parses an isolated identification fixture with no filesystem access.</summary>
    private static MediaInspection Parse(string json)
    {
        return MkvIdentification.Parse(new MkvIdentification.IdentificationRequest(json,
            new SourceRevision("synthetic.mkv", "fixture", 1, 1)));
    }
}
