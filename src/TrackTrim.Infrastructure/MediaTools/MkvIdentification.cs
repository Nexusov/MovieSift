using System.Collections.Immutable;
using System.Text.Json;
using TrackTrim.Application;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Media;

namespace TrackTrim.Infrastructure.MediaTools;

/// <summary>Parses the actual pinned identification format without conflating local IDs and UIDs.</summary>
internal static class MkvIdentification
{
    internal const int SupportedIdentificationFormat = 20;
    internal const int MaximumJsonCharacters = 8 * 1024 * 1024;
    private const int MaximumJsonDepth = 32;
    private static readonly string[] UnsupportedContainerProperties =
        ["next_segment_uid", "previous_segment_uid", "segment_family"];
    private static readonly string[] UnsupportedTrackProperties =
        ["max_luminance", "min_luminance", "max_content_light", "max_frame_light", "chromaticity_coordinates",
            "white_color_coordinates", "dolby_vision", "hdr_dynamic_metadata", "multiplexed_tracks"];

    /// <summary>Validates required capability information and snapshots the inspection.</summary>
    internal static MediaInspection Parse(IdentificationRequest request)
    {
        try
        {

            if (request.Json.Length > MaximumJsonCharacters)
            {
                throw Invalid("Identification JSON exceeded the supported structured-output bound.");
            }

            using var document = JsonDocument.Parse(request.Json, new JsonDocumentOptions { MaxDepth = MaximumJsonDepth });
            var root = document.RootElement;
            var formatVersion = root.GetProperty("identification_format_version").GetInt32();

            if (formatVersion != SupportedIdentificationFormat)
            {
                throw Invalid($"Unsupported identification format {formatVersion}; expected {SupportedIdentificationFormat}.");
            }

            var container = root.GetProperty("container");
            var supportedContainer = container.GetProperty("recognized").GetBoolean()
                && container.GetProperty("supported").GetBoolean()
                && container.GetProperty("type").GetString() == "Matroska";

            if (!supportedContainer)
            {
                throw new OperationFailureException(new Failure(ErrorCode.UnsupportedContainer, "Select a recognized Matroska MKV file."));
            }

            if (root.GetProperty("errors").GetArrayLength() != 0 || root.GetProperty("warnings").GetArrayLength() != 0)
            {
                throw Invalid("Identification reported warnings or errors; the source cannot be approved safely.");
            }

            var containerProperties = container.GetProperty("properties");

            foreach (var propertyName in UnsupportedContainerProperties)
            {

                if (containerProperties.TryGetProperty(propertyName, out _))
                {
                    throw Unsupported($"Linked segments ({propertyName}) are not supported.");
                }
            }

            var writingApplication = OptionalString(new OptionalProperty(containerProperties, "writing_application")) ?? string.Empty;
            var tracks = root.GetProperty("tracks").EnumerateArray().Select(track =>
                ParseTrack(new TrackParsingRequest(track, writingApplication))).ToImmutableArray();

            if (tracks.IsEmpty || tracks.Select(track => track.Id).Distinct().Count() != tracks.Length
                || tracks.Select(track => track.Uid).Distinct().Count() != tracks.Length)
            {
                throw Invalid("The source must contain uniquely identified tracks.");
            }

            var attachments = root.GetProperty("attachments").EnumerateArray().Select(ParseAttachment).ToImmutableArray();
            var hasTags = root.GetProperty("global_tags").GetArrayLength() != 0
                || root.GetProperty("track_tags").GetArrayLength() != 0;
            return new MediaInspection(request.Source,
                new BackendIdentity("MKVToolNix", MkvToolPaths.SupportedVersion, formatVersion),
                tracks, attachments, OptionalString(new OptionalProperty(containerProperties, "title")) ?? string.Empty,
                root.GetProperty("chapters").GetArrayLength() != 0, hasTags);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            throw Invalid($"Malformed or incomplete MKV identification JSON: {exception.Message}");
        }
    }

    /// <summary>Retains original probe properties separately from effective Matroska flags.</summary>
    private static MediaTrack ParseTrack(TrackParsingRequest request)
    {
        var track = request.Track;
        var kind = track.GetProperty("type").GetString() switch
        {
            "video" => TrackKind.Video,
            "audio" => TrackKind.Audio,
            "subtitles" => TrackKind.Subtitles,
            _ => throw Unsupported("Uncommon track types are not supported in this MKV workflow."),
        };
        var properties = track.GetProperty("properties");

        foreach (var property in properties.EnumerateObject())
        {
            var advancedProperty = UnsupportedTrackProperties.Contains(property.Name, StringComparer.Ordinal)
                || property.Name.Contains("dolby_vision", StringComparison.Ordinal)
                || property.Name.Contains("hdr", StringComparison.Ordinal);

            if (advancedProperty)
            {
                throw Unsupported($"Advanced video metadata ({property.Name}) requires a separately verified workflow.");
            }
        }

        var transfer = OptionalString(new OptionalProperty(properties, "color_transfer_characteristics"));

        if (transfer is "16" or "18")
        {
            throw Unsupported("Detected PQ/HLG HDR transfer characteristics require a separately verified workflow.");
        }

        var rawProperties = properties.EnumerateObject().ToImmutableDictionary(
            property => property.Name, property => property.Value.ToString(), StringComparer.Ordinal)
            .Add("_source_writing_application", request.WritingApplication);
        var channels = properties.TryGetProperty("audio_channels", out var channelValue) ? channelValue.GetInt32() : (int?)null;
        return new MediaTrack(track.GetProperty("id").GetInt32(), properties.GetProperty("uid").GetUInt64(), kind,
            track.GetProperty("codec").GetString() ?? throw Invalid("A track codec is missing."),
            OptionalString(new OptionalProperty(properties, "language")) ?? "eng",
            OptionalString(new OptionalProperty(properties, "language_ietf")),
            OptionalString(new OptionalProperty(properties, "track_name")) ?? string.Empty, channels,
            EffectiveFlag(new FlagRequest(properties, "default_track", true)),
            EffectiveFlag(new FlagRequest(properties, "forced_track", false)),
            EffectiveFlag(new FlagRequest(properties, "enabled_track", true)), rawProperties);
    }

    /// <summary>Reads attachment metadata without ever turning its filename into a filesystem path.</summary>
    private static Attachment ParseAttachment(JsonElement attachment)
    {
        return new Attachment(attachment.GetProperty("id").GetInt32(),
            attachment.GetProperty("properties").GetProperty("uid").GetUInt64(),
            attachment.GetProperty("file_name").GetString() ?? string.Empty,
            attachment.GetProperty("content_type").GetString() ?? throw Invalid("Attachment MIME type is missing."),
            attachment.GetProperty("size").GetInt64(),
            OptionalString(new OptionalProperty(attachment, "description")) ?? string.Empty);
    }

    /// <summary>Applies format-defined defaults only where Matroska defines them.</summary>
    private static bool EffectiveFlag(FlagRequest request)
    {
        return request.Properties.TryGetProperty(request.Name, out var value) ? value.GetBoolean() : request.DefaultValue;
    }

    /// <summary>Reads an optional raw field without normalizing media metadata.</summary>
    private static string? OptionalString(OptionalProperty request)
    {
        return request.Element.TryGetProperty(request.Name, out var value) ? value.ToString() : null;
    }

    /// <summary>Creates a stable malformed-output failure.</summary>
    private static OperationFailureException Invalid(string detail)
    {
        return new OperationFailureException(new Failure(ErrorCode.InvalidToolOutput, detail));
    }

    /// <summary>Creates an actionable capability failure.</summary>
    private static OperationFailureException Unsupported(string detail)
    {
        return new OperationFailureException(new Failure(ErrorCode.UnsupportedFeature, detail));
    }

    internal sealed record IdentificationRequest(string Json, SourceRevision Source);
    private sealed record OptionalProperty(JsonElement Element, string Name);
    private sealed record FlagRequest(JsonElement Properties, string Name, bool DefaultValue);
    private sealed record TrackParsingRequest(JsonElement Track, string WritingApplication);
}
