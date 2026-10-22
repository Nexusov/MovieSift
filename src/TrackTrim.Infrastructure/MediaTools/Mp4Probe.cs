using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using TrackTrim.Application;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Media;

namespace TrackTrim.Infrastructure.MediaTools;

/// <summary>Snapshots bounded ffprobe facts while keeping MP4 stream indexes and container track identifiers distinct.</summary>
internal static class Mp4Probe
{
    internal const int IdentificationFormat = 1;
    internal const int MaximumJsonCharacters = 2 * 1024 * 1024;
    internal const string TagsProperty = "tags";
    internal const string ChaptersProperty = "chapters";
    private const int MaximumStreams = 256;
    private const int MaximumChapters = 1024;
    private const int MaximumTags = 128;
    private const int MaximumTagCharacters = 8192;
    private static readonly string[] CodecProperties =
    [
        "codec_tag_string", "profile", "width", "height", "coded_width", "coded_height", "pix_fmt", "level",
        "sample_aspect_ratio", "display_aspect_ratio", "has_b_frames", "chroma_location", "field_order", "is_avc",
        "nal_length_size", "sample_fmt", "sample_rate", "channels", "channel_layout", "bits_per_sample",
        "bits_per_raw_sample", "extradata_size", "extradata_hash", "initial_padding", "trailing_padding",
        "color_range", "color_space", "color_transfer", "color_primaries", "closed_captions", "film_grain",
    ];
    private static readonly HashSet<string> FormatTagNames = new(StringComparer.Ordinal)
    {
        "major_brand", "minor_version", "compatible_brands", "encoder", "title", "artist", "album_artist", "album",
        "date", "comment", "genre", "composer", "copyright", "description", "synopsis", "purchase_date", "track",
        "disc", "compilation", "gapless_playback", "tempo", "sort_album", "sort_artist", "sort_name", "sort_composer",
        "sort_show", "show", "episode_id", "season_number", "episode_sort", "media_type", "rating", "creation_time",
    };
    private static readonly HashSet<string> StreamTagNames = new(StringComparer.Ordinal)
        { "language", "handler_name", "title", "name", "encoder", "vendor_id", "creation_time" };
    private static readonly HashSet<string> PermittedDispositions = new(StringComparer.Ordinal)
        { "default", "forced", "attached_pic", "hearing_impaired", "visual_impaired", "descriptions", "captions", "dub", "original", "comment" };

    /// <summary>Requires actual box/probe agreement and refuses unrepresentable streams or metadata before planning.</summary>
    internal static MediaInspection Parse(Mp4ProbeRequest request)
    {
        try
        {
            if (request.Json.Length > MaximumJsonCharacters)
            {
                throw Invalid("MP4 identification JSON exceeded its output bound.");
            }

            using var document = JsonDocument.Parse(request.Json, new JsonDocumentOptions { MaxDepth = 24 });
            var root = document.RootElement;
            var streams = root.GetProperty("streams");
            var format = root.GetProperty("format");

            if (streams.ValueKind != JsonValueKind.Array || streams.GetArrayLength() is < 1 or > MaximumStreams
                || Text(format, "format_name") != "mov,mp4,m4a,3gp,3g2,mj2")
            {
                throw Invalid("MP4 identification lacks the required bounded stream/container information.");
            }

            var formatTags = ReadTags(format, FormatTagNames);

            if (!formatTags.TryGetValue("major_brand", out var majorBrand) || majorBrand != request.Structure.MajorBrand)
            {
                throw Invalid("The MP4 header brand and probe disagree.");
            }

            var chapters = ReadChapters(root);
            ValidateNeroChapters(request.Structure.NeroChapters, chapters);
            var parsedTracks = ImmutableArray.CreateBuilder<MediaTrack>();
            var chapterStreams = 0;

            foreach (var stream in streams.EnumerateArray())
            {
                var containerId = ContainerIdentifier(stream);
                var codecType = Text(stream, "codec_type");

                if (codecType == "data" && request.Structure.ChapterTrackIds.Contains(containerId))
                {
                    var chapterHeader = request.Structure.Tracks.Single(track => track.Id == containerId);
                    var validChapter = Text(stream, "codec_name") == "bin_data" && Text(stream, "codec_tag_string") == "text"
                        && chapterHeader.SampleEntry == "text" && chapters.Length != 0
                        && Scalar(stream.GetProperty("nb_frames")) == chapters.Length.ToString(CultureInfo.InvariantCulture);

                    if (!validChapter)
                    {
                        throw Unsupported("The referenced MP4 chapter track is not an ordinary text chapter representation.");
                    }

                    chapterStreams++;
                    continue;
                }

                parsedTracks.Add(ReadTrack(stream, request.Structure));
            }

            var tracks = parsedTracks.ToImmutable();
            var actualArtwork = tracks.Where(track => track.Properties["attached_pic"] == "1").Select(track => track.Codec);
            var expectedArtwork = request.Structure.ArtworkCodecs.IsDefault ? [] : request.Structure.ArtworkCodecs;
            var invalidTracks = tracks.IsEmpty || !tracks.Any(track => track.Kind == TrackKind.Video && track.Properties["attached_pic"] == "0")
                || tracks.Select(track => track.Id).Distinct().Count() != tracks.Length
                || chapterStreams != request.Structure.ChapterTrackIds.Count || !actualArtwork.SequenceEqual(expectedArtwork);

            if (invalidTracks)
            {
                throw Unsupported("The MP4 must have ordinary video and uniquely mapped admitted streams/chapter references.");
            }

            var commonTags = formatTags.Where(tag => tag.Key is not ("major_brand" or "minor_version" or "compatible_brands" or "encoder"))
                .ToImmutableSortedDictionary(StringComparer.Ordinal);
            var properties = ImmutableDictionary<string, string>.Empty
                .Add(TagsProperty, JsonSerializer.Serialize(commonTags))
                .Add(ChaptersProperty, JsonSerializer.Serialize(chapters))
                .Add("duration", Text(format, "duration"));
            return new(request.Source, new("FFmpeg", FfmpegToolPaths.SupportedVersion, IdentificationFormat), tracks, [],
                formatTags.GetValueOrDefault("title", string.Empty), chapters.Length != 0, commonTags.Count != 0,
                MediaContainer.Mp4, properties);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException
            or FormatException or OverflowException or ArgumentException)
        {
            throw Invalid($"Malformed required MP4 probe output: {exception.Message}");
        }
    }

    /// <summary>Compares the frozen retained order, complete admitted metadata and codec initialization rather than codec/language alone.</summary>
    internal static string? CompareTracks(Mp4TrackComparison request)
    {
        if (request.Expected.Count != request.Actual.Count)
        {
            return "The MP4 retained stream count does not match the approved mapping.";
        }

        for (var index = 0; index < request.Expected.Count; index++)
        {
            var expected = request.Expected[index];
            var actual = request.Actual[index];
            var commonMatches = expected.Kind == actual.Kind && expected.Codec == actual.Codec && expected.Language == actual.Language
                && expected.LanguageIetf == actual.LanguageIetf && expected.Title == actual.Title && expected.Channels == actual.Channels
                && expected.IsForced == actual.IsForced
                && (request.IgnoreHeaderFlags || expected.IsDefault == actual.IsDefault && expected.IsEnabled == actual.IsEnabled);

            if (!commonMatches)
            {
                return $"Retained input stream {expected.Id} changed its effective metadata or mapping at output position {index}.";
            }

            var expectedProperties = ComparableProperties(expected.Properties, request.IgnoreHeaderFlags);
            var actualProperties = ComparableProperties(actual.Properties, request.IgnoreHeaderFlags);

            if (expectedProperties.Count != actualProperties.Count || expectedProperties.Any(property =>
                !actualProperties.TryGetValue(property.Key, out var value) || value != property.Value))
            {
                return $"Retained input stream {expected.Id} changed its admitted codec configuration, metadata, artwork or dispositions.";
            }
        }

        return null;
    }

    /// <summary>Parses only a positive rational timebase and formats timestamps without floating-point rounding.</summary>
    internal static string RationalTimestamp(string timestamp, string timebase)
    {
        var parts = timebase.Split('/');

        if (parts.Length != 2 || !BigInteger.TryParse(parts[0], CultureInfo.InvariantCulture, out var numerator)
            || !BigInteger.TryParse(parts[1], CultureInfo.InvariantCulture, out var denominator) || numerator <= 0 || denominator <= 0
            || !BigInteger.TryParse(timestamp, CultureInfo.InvariantCulture, out var value))
        {
            throw Invalid("An MP4 packet or chapter has an invalid rational timestamp.");
        }

        numerator *= value;
        var divisor = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
        return string.Create(CultureInfo.InvariantCulture, $"{numerator / divisor}/{denominator / divisor}");
    }

    /// <summary>Maps the actual supported stream and effective flags; attached artwork remains an always-retained video stream.</summary>
    private static MediaTrack ReadTrack(JsonElement stream, Mp4Structure structure)
    {
        var identifier = stream.GetProperty("index").GetInt32();
        var codec = Text(stream, "codec_name");
        var codecType = Text(stream, "codec_type");
        var dispositions = ReadDispositions(stream);
        var artwork = dispositions.GetValueOrDefault("attached_pic") == "1";
        var kind = codecType switch
        {
            "video" => TrackKind.Video,
            "audio" => TrackKind.Audio,
            "subtitle" => TrackKind.Subtitles,
            _ => throw Unsupported($"Unsupported MP4 timed stream type: {codecType}.")
        };
        var supportedCodec = (kind, codec, artwork) switch
        {
            (TrackKind.Video, "h264", false) or (TrackKind.Video, "mjpeg" or "png", true)
                or (TrackKind.Audio, "aac", false) or (TrackKind.Subtitles, "mov_text", false) => true,
            _ => false,
        };

        if (identifier < 0 || !supportedCodec)
        {
            throw Unsupported($"Unsupported MP4 codec/artwork representation: {codecType}/{codec}.");
        }

        var containerId = ContainerIdentifier(stream);
        var header = structure.Tracks.SingleOrDefault(track => track.Id == containerId);

        if (!artwork && header is null)
        {
            throw Invalid("An MP4 media stream has no corresponding container track header.");
        }

        var enabled = artwork || (header!.Flags & 1) != 0;

        if (!artwork && enabled != (dispositions.GetValueOrDefault("default") == "1"))
        {
            throw Invalid("The MP4 enabled/default bit and probe disposition disagree.");
        }

        var tags = ReadTags(stream, StreamTagNames);
        var properties = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

        foreach (var propertyName in CodecProperties)
        {
            if (stream.TryGetProperty(propertyName, out var property))
            {
                properties.Add(propertyName, Scalar(property));
            }
        }

        var advancedHdr = properties.GetValueOrDefault("color_transfer") is "smpte2084" or "arib-std-b67";

        if (advancedHdr)
        {
            throw Unsupported("Advanced HDR transfer characteristics are outside the proven MP4 subset.");
        }

        if (stream.TryGetProperty("side_data_list", out var sideData))
        {
            foreach (var entry in sideData.EnumerateArray())
            {
                if (Text(entry, "side_data_type") != "Display Matrix")
                {
                    throw Unsupported($"Unsupported MP4 stream side data: {Text(entry, "side_data_type")}.");
                }
            }

            properties.Add("side_data", JsonSerializer.Serialize(sideData));
        }

        var timebase = Text(stream, "time_base");
        _ = RationalTimestamp("1", timebase);
        properties.Add("time_base", timebase);
        properties.Add("container_track_id", containerId.ToString(CultureInfo.InvariantCulture));

        if (!artwork)
        {
            properties.Add("tkhd_flags", header!.Flags.ToString(CultureInfo.InvariantCulture));
        }

        properties.Add("attached_pic", artwork ? "1" : "0");
        properties.Add("tags", JsonSerializer.Serialize(tags.ToImmutableSortedDictionary(StringComparer.Ordinal)));

        foreach (var disposition in dispositions.Where(value => value.Key != "default"))
        {
            properties.Add("disposition:" + disposition.Key, disposition.Value);
        }

        return new(identifier, null, kind, codec, tags.GetValueOrDefault("language", "und"), null,
            tags.GetValueOrDefault("title", tags.GetValueOrDefault("name", string.Empty)),
            kind == TrackKind.Audio ? stream.GetProperty("channels").GetInt32() : null,
            dispositions.GetValueOrDefault("default") == "1", dispositions.GetValueOrDefault("forced") == "1", enabled,
            properties.ToImmutable(), SemanticDispositions(dispositions));
    }

    /// <summary>Projects the existing shared descriptive flags while retaining every admitted raw disposition in properties.</summary>
    private static ImmutableArray<TrackDisposition> SemanticDispositions(ImmutableDictionary<string, string> dispositions)
    {
        var mappings = new[]
        {
            KeyValuePair.Create("hearing_impaired", TrackDisposition.HearingImpaired),
            KeyValuePair.Create("visual_impaired", TrackDisposition.VisualImpaired),
            KeyValuePair.Create("descriptions", TrackDisposition.TextDescriptions),
            KeyValuePair.Create("original", TrackDisposition.Original),
            KeyValuePair.Create("comment", TrackDisposition.Commentary),
        };
        return mappings.Where(mapping => dispositions.GetValueOrDefault(mapping.Key) == "1").Select(mapping => mapping.Value).ToImmutableArray();
    }

    /// <summary>Retains only explicitly representable effective dispositions and rejects unknown true flags.</summary>
    private static ImmutableDictionary<string, string> ReadDispositions(JsonElement stream)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var dispositions = stream.GetProperty("disposition");
        _ = dispositions.GetProperty("default").GetInt32();
        _ = dispositions.GetProperty("forced").GetInt32();
        _ = dispositions.GetProperty("attached_pic").GetInt32();

        foreach (var property in dispositions.EnumerateObject())
        {
            var value = property.Value.GetInt32();

            if (value is not (0 or 1) || value == 1 && !PermittedDispositions.Contains(property.Name))
            {
                throw Unsupported($"Unsupported or malformed MP4 disposition: {property.Name}.");
            }

            if (value != 0 || property.Name is "default" or "forced" or "attached_pic")
            {
                builder.Add(property.Name, value.ToString(CultureInfo.InvariantCulture));
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>Validates known metadata keys and bounds every retained user-controlled string.</summary>
    private static ImmutableDictionary<string, string> ReadTags(JsonElement element, HashSet<string> supportedNames)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

        if (!element.TryGetProperty("tags", out var tags))
        {
            return builder.ToImmutable();
        }

        foreach (var property in tags.EnumerateObject())
        {
            var value = RequiredString(property.Value);
            var unsupportedTag = !supportedNames.Contains(property.Name) || value.Length > MaximumTagCharacters
                || builder.Count >= MaximumTags || !builder.TryAdd(property.Name, value);

            if (unsupportedTag)
            {
                throw Unsupported($"Unsupported, duplicated or oversized MP4 metadata tag: {property.Name}.");
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>Freezes ordinary chapter names and exact rational presentation boundaries independently of regenerated chapter track IDs.</summary>
    private static ImmutableArray<Mp4Chapter> ReadChapters(JsonElement root)
    {
        if (!root.TryGetProperty("chapters", out var chapters))
        {
            return [];
        }

        if (chapters.GetArrayLength() > MaximumChapters)
        {
            throw Unsupported("The ordinary MP4 chapter-count bound was exceeded.");
        }

        var builder = ImmutableArray.CreateBuilder<Mp4Chapter>();

        foreach (var chapter in chapters.EnumerateArray())
        {
            var start = 0L;
            var end = 0L;
            var validBounds = long.TryParse(Scalar(chapter.GetProperty("start")), NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out start) && start >= 0
                && long.TryParse(Scalar(chapter.GetProperty("end")), NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out end) && end > start;

            if (!validBounds)
            {
                throw Invalid("Ordinary MP4 chapters require finite nonnegative starts and later ends.");
            }

            var timebase = Text(chapter, "time_base");
            builder.Add(new(RationalTimestamp(start.ToString(CultureInfo.InvariantCulture), timebase),
                RationalTimestamp(end.ToString(CultureInfo.InvariantCulture), timebase),
                ReadTags(chapter, new HashSet<string>(StringComparer.Ordinal) { "title" })));
        }

        return builder.ToImmutable();
    }

    /// <summary>Requires Nero's bounded secondary list to describe the same starts/titles, allowing only its defined 100 ns rounding and 255-entry cap.</summary>
    private static void ValidateNeroChapters(ImmutableArray<Mp4NeroChapter> nero, ImmutableArray<Mp4Chapter> chapters)
    {
        if (nero.IsDefault)
        {
            return;
        }

        if (nero.IsEmpty || nero.Length != Math.Min(chapters.Length, byte.MaxValue))
        {
            throw Unsupported("The Nero chapter list is absent from, or conflicts with, the probed ordinary chapters.");
        }

        const int neroTicksPerSecond = 10_000_000;

        for (var index = 0; index < nero.Length; index++)
        {
            var chapter = chapters[index];
            var ratio = chapter.Start.Split('/');
            var numerator = BigInteger.Parse(ratio[0], CultureInfo.InvariantCulture);
            var denominator = BigInteger.Parse(ratio[1], CultureInfo.InvariantCulture);
            // The pinned muxer uses av_rescale_q/AV_ROUND_NEAR_INF: positive ties round upward to one 100 ns tick.
            var roundedStart = (numerator * neroTicksPerSecond * 2 + denominator) / (denominator * 2);

            if (roundedStart != nero[index].StartTicks || chapter.Tags.GetValueOrDefault("title", string.Empty) != nero[index].Title)
            {
                throw Unsupported("The Nero and QuickTime/probed chapter starts or titles disagree.");
            }
        }
    }

    /// <summary>Allows container track renumbering and exact timebase rescaling while keeping all admitted semantic fields strict.</summary>
    private static Dictionary<string, string> ComparableProperties(ImmutableDictionary<string, string> properties, bool ignoreHeaderFlags) =>
        properties.Where(property => property.Key is not ("time_base" or "container_track_id")
            && (!ignoreHeaderFlags || property.Key != "tkhd_flags")).ToDictionary();

    /// <summary>Separates actual MP4 track_ID from the ffprobe stream index.</summary>
    private static uint ContainerIdentifier(JsonElement stream)
    {
        if (!stream.TryGetProperty("id", out var value))
        {
            return 0;
        }

        var text = RequiredString(value);
        return text.StartsWith("0x", StringComparison.Ordinal)
            ? uint.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : uint.Parse(text, CultureInfo.InvariantCulture);
    }

    /// <summary>Reads a required tool string without culture-sensitive conversion.</summary>
    private static string Text(JsonElement element, string property) => RequiredString(element.GetProperty(property));

    /// <summary>Preserves invariant JSON scalar spelling for exact property comparison.</summary>
    private static string Scalar(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => RequiredString(element),
        JsonValueKind.Number => element.GetRawText(),
        _ => throw Invalid("A required MP4 probe scalar is null or has an invalid type."),
    };

    /// <summary>Rejects JSON null and non-string fields before dereferencing required probe metadata.</summary>
    private static string RequiredString(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String || element.GetString() is not { } value)
        {
            throw Invalid("A required MP4 probe string is null or has an invalid type.");
        }

        return value;
    }

    /// <summary>Reports unsupported media separately from malformed machine output.</summary>
    private static OperationFailureException Unsupported(string detail) => new(new(ErrorCode.UnsupportedFeature, detail));

    /// <summary>Reports missing/truncated/inconsistent machine data without manufacturing media facts.</summary>
    private static OperationFailureException Invalid(string detail) => new(new(ErrorCode.InvalidToolOutput, detail));
}
