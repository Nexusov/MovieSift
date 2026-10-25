using System.Collections.Immutable;
using MovieSift.Application;
using MovieSift.Core.Failures;
using MovieSift.Core.Media;
using MovieSift.Infrastructure.MediaTools;
using Xunit;

namespace MovieSift.Infrastructure.Tests;

/// <summary>Rejects incomplete packet evidence rather than publishing on matching guessed or uninterpreted fields.</summary>
public sealed class Mp4PacketValidationTests
{
    private const int StreamIndex = 8;
    private const string Digest = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
    private const string SkipPrefix = "side_datum/skip_samples:";

    /// <summary>Requires real finite timing for audio/video/subtitles; an absent value is not a verified timestamp.</summary>
    [Theory]
    [InlineData("pts", null)]
    [InlineData("dts", null)]
    [InlineData("duration", null)]
    [InlineData("pts", "N/A")]
    [InlineData("dts", "N/A")]
    [InlineData("duration", "N/A")]
    [InlineData("pts", "9223372036854775808")]
    [InlineData("dts", "-9223372036854775809")]
    [InlineData("duration", "-1")]
    [InlineData("duration", "9223372036854775808")]
    public void IncompleteOrInvalidTimedPacketCannotBecomeEvidence(string field, string? value)
    {
        var fields = PacketFields();
        Replace(fields, field, value);
        AssertInvalid(fields);
    }

    /// <summary>Checks the fixed machine fields instead of accepting any same-length hash, packet size or flag text.</summary>
    [Theory]
    [InlineData("size", "-1")]
    [InlineData("size", "not-a-size")]
    [InlineData("size", "9223372036854775808")]
    [InlineData("data_hash", "SHA256:xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    [InlineData("flags", "XXX")]
    [InlineData("flags", "KK_")]
    [InlineData("flags", "K__extra")]
    public void MalformedFixedPacketFieldsAreRejected(string field, string value)
    {
        var fields = PacketFields();
        fields[field] = value;
        AssertInvalid(fields);
    }

    /// <summary>Requires the complete admitted skip/padding record, including its discriminator and numeric reason fields.</summary>
    [Theory]
    [InlineData("side_data_type", null)]
    [InlineData("skip_samples", null)]
    [InlineData("discard_padding", null)]
    [InlineData("skip_reason", null)]
    [InlineData("discard_reason", null)]
    [InlineData("side_data_type", "Unknown type")]
    [InlineData("skip_samples", "-1")]
    [InlineData("skip_samples", "4294967296")]
    [InlineData("discard_padding", "not-a-number")]
    [InlineData("skip_reason", "256")]
    [InlineData("discard_reason", "-1")]
    public void IncompleteOrMalformedAudioSkipDataIsRejected(string field, string? value)
    {
        var fields = PacketFields();
        AddSkipData(fields);
        Replace(fields, SkipPrefix + field, value);
        AssertInvalid(fields);
    }

    /// <summary>A side-data type label alone cannot prove that advanced HDR content was retained.</summary>
    [Theory]
    [InlineData("Mastering display metadata")]
    [InlineData("Content light level metadata")]
    [InlineData("DOVI configuration record")]
    public void UnprovenPacketSideDataIsRejected(string type)
    {
        var fields = PacketFields();
        fields["side_datum/unproven:side_data_type"] = type;
        AssertInvalid(fields);
    }

    /// <summary>Does not reinterpret audio skip metadata as an admitted video timing feature.</summary>
    [Fact]
    public void AudioSkipDataOnVideoIsRejected()
    {
        var fields = PacketFields();
        AddSkipData(fields);
        AssertInvalid(fields, TrackKind.Video);
    }

    /// <summary>Allows the genuine untimed-cover representation while still requiring complete machine fields.</summary>
    [Fact]
    public void ArtworkAllowsExplicitUnknownTimingButNotMissingTimingFields()
    {
        var fields = PacketFields();
        fields["pts"] = "N/A";
        fields["dts"] = "N/A";
        fields["duration"] = "N/A";
        using var evidence = new Mp4PacketEvidence([Track(TrackKind.Video, artwork: true)]);
        evidence.AcceptLine(Line(fields));
        Assert.Equal(1, evidence.Finish()[StreamIndex].Count);
        fields.Remove("pts");
        AssertInvalid(fields, TrackKind.Video, artwork: true);
    }

    /// <summary>Accepts actual initial AAC priming and terminal padding with their explicit unsigned counts.</summary>
    [Theory]
    [InlineData("1024", "0")]
    [InlineData("0", "256")]
    public void CompleteAudioSkipDataIsAdmitted(string skip, string discard)
    {
        var fields = PacketFields();
        AddSkipData(fields);
        fields[SkipPrefix + "skip_samples"] = skip;
        fields[SkipPrefix + "discard_padding"] = discard;
        using var evidence = new Mp4PacketEvidence([Track(TrackKind.Audio)]);
        evidence.AcceptLine(Line(fields));
        Assert.Equal(1, evidence.Finish()[StreamIndex].Count);
    }

    /// <summary>Compares actual digest bytes independently of hexadecimal letter casing.</summary>
    [Fact]
    public void EquivalentHexadecimalHashesHaveEquivalentEvidence()
    {
        var fields = PacketFields();
        using var lowercase = new Mp4PacketEvidence([Track(TrackKind.Audio)]);
        lowercase.AcceptLine(Line(fields));
        fields["data_hash"] = "SHA256:" + Digest.ToUpperInvariant();
        using var uppercase = new Mp4PacketEvidence([Track(TrackKind.Audio)]);
        uppercase.AcceptLine(Line(fields));
        Assert.Equal(lowercase.Finish()[StreamIndex], uppercase.Finish()[StreamIndex]);
    }

    /// <summary>Creates one complete controlled packet in the pinned compact protocol.</summary>
    private static Dictionary<string, string> PacketFields() => new(StringComparer.Ordinal)
    {
        ["stream_index"] = "8",
        ["pts"] = "-1024",
        ["dts"] = "-1024",
        ["duration"] = "1024",
        ["size"] = "200",
        ["flags"] = "KD_",
        ["data_hash"] = "SHA256:" + Digest,
    };

    /// <summary>Adds all fields emitted for the supported AAC skip-samples side-data type.</summary>
    private static void AddSkipData(Dictionary<string, string> fields)
    {
        fields[SkipPrefix + "side_data_type"] = "Skip Samples";
        fields[SkipPrefix + "skip_samples"] = "1024";
        fields[SkipPrefix + "discard_padding"] = "0";
        fields[SkipPrefix + "skip_reason"] = "0";
        fields[SkipPrefix + "discard_reason"] = "0";
    }

    /// <summary>Models either an absent required field or a concrete malformed value.</summary>
    private static void Replace(Dictionary<string, string> fields, string key, string? value)
    {
        if (value is null)
        {
            fields.Remove(key);
            return;
        }

        fields[key] = value;
    }

    /// <summary>Requires a stable verification failure, never a fabricated successful fingerprint.</summary>
    private static void AssertInvalid(Dictionary<string, string> fields, TrackKind kind = TrackKind.Audio, bool artwork = false)
    {
        using var evidence = new Mp4PacketEvidence([Track(kind, artwork)]);
        evidence.AcceptLine(Line(fields));
        Assert.Equal(ErrorCode.VerificationFailed, Assert.Throws<OperationFailureException>(() => evidence.Finish()).Failure.Code);
    }

    /// <summary>Uses controlled local stream identity without invoking media inspection or jobs.</summary>
    private static MediaTrack Track(TrackKind kind, bool artwork = false) => new(StreamIndex, null, kind,
        artwork ? "png" : kind == TrackKind.Audio ? "aac" : "h264", "eng", null, "", null, false, false, true,
        ImmutableDictionary<string, string>.Empty.Add("time_base", "1/48000").Add("attached_pic", artwork ? "1" : "0"));

    /// <summary>Serializes only the fixed controlled fields used by this parser regression.</summary>
    private static string Line(Dictionary<string, string> fields) => string.Join('|', fields.Select(field => field.Key + "=" + field.Value));
}
