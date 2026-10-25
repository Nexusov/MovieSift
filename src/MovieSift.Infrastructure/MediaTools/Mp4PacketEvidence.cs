using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MovieSift.Application;
using MovieSift.Core.Failures;
using MovieSift.Core.Media;

namespace MovieSift.Infrastructure.MediaTools;

/// <summary>Folds fixed-field ffprobe packet records into constant-memory per-stream evidence without retaining movie-sized output.</summary>
internal sealed class Mp4PacketEvidence : IDisposable
{
    private const string HashPrefix = "SHA256:";
    private const int DigestHexCharacters = 64;
    private const string SkipPrefix = "side_datum/skip_samples:";
    private static readonly string[] TimestampFields = ["pts", "dts", "duration"];
    private static readonly HashSet<string> PacketFields = new(StringComparer.Ordinal)
        { "stream_index", "pts", "dts", "duration", "size", "flags", "data_hash" };
    private static readonly string[] SkipFields = ["side_data_type", "skip_samples", "discard_padding", "skip_reason", "discard_reason"];
    private static readonly HashSet<string> PermittedSkipFields = SkipFields.Select(field => SkipPrefix + field).ToHashSet(StringComparer.Ordinal);
    private readonly Dictionary<int, StreamEvidence> streams;
    private bool failed;
    private string? failureDetail;

    /// <summary>Captures only this file's real retained stream indexes and rational timebases.</summary>
    internal Mp4PacketEvidence(IEnumerable<MediaTrack> tracks)
    {
        streams = tracks.ToDictionary(track => track.Id,
            track => new StreamEvidence(track.Properties["time_base"], track.Kind, track.Properties["attached_pic"] == "1"));
    }

    /// <summary>Processes one bounded machine record; unrelated removed/chapter streams are drained without being retained.</summary>
    internal void AcceptLine(string line)
    {
        try
        {
            var fields = line.Split('|').Select(field => field.Split('=', 2)).ToArray();

            if (fields.Any(field => field.Length != 2))
            {
                failed = true;
                return;
            }

            var values = fields.ToDictionary(field => field[0], field => field[1], StringComparer.Ordinal);

            if (!int.TryParse(values["stream_index"], NumberStyles.None, CultureInfo.InvariantCulture, out var identifier))
            {
                failed = true;
                return;
            }

            if (!streams.TryGetValue(identifier, out var evidence))
            {
                return;
            }

            var validationFailure = ValidateFields(new(values, evidence.Kind, evidence.IsArtwork));

            if (validationFailure is not null)
            {
                failed = true;
                failureDetail ??= validationFailure;
                return;
            }

            var canonical = new StringBuilder();

            foreach (var key in evidence.IsArtwork ? Array.Empty<string>() : TimestampFields)
            {
                canonical.Append(key).Append('=').Append(Mp4Probe.RationalTimestamp(values[key], evidence.Timebase)).Append('|');
            }

            canonical.Append("size=").Append(values["size"]).Append("|flags=").Append(values["flags"])
                .Append("|data_hash=").Append(values["data_hash"]);

            foreach (var field in values.Where(field => field.Key.StartsWith("side_datum/", StringComparison.Ordinal)).OrderBy(field => field.Key, StringComparer.Ordinal))
            {
                canonical.Append('|').Append(field.Key).Append('=').Append(field.Value);
            }

            canonical.Append('\n');
            evidence.Hash.AppendData(Encoding.UTF8.GetBytes(canonical.ToString()));
            evidence.Count = checked(evidence.Count + 1);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or ArgumentException or OperationFailureException or OverflowException)
        {
            failed = true;
        }
    }

    /// <summary>Requires complete valid records and at least one encoded packet for each admitted retained stream.</summary>
    internal ImmutableDictionary<int, Mp4PacketFingerprint> Finish()
    {
        if (failed || streams.Values.Any(stream => stream.Count == 0))
        {
            throw new OperationFailureException(new(ErrorCode.VerificationFailed,
                failureDetail ?? "The streamed MP4 packet evidence is incomplete or malformed."));
        }

        return streams.ToImmutableDictionary(stream => stream.Key,
            stream => new Mp4PacketFingerprint(stream.Value.Count, Convert.ToHexString(stream.Value.Hash.GetHashAndReset())));
    }

    /// <summary>Disposes only in-memory hashing state; no media or ownership paths are touched.</summary>
    public void Dispose()
    {
        foreach (var evidence in streams.Values)
        {
            evidence.Hash.Dispose();
        }
    }

    /// <summary>Requires complete bounded primitive fields and the one admitted audio side-data representation.</summary>
    private static string? ValidateFields(Mp4PacketValidationRequest request)
    {
        var fields = request.Fields;

        if (PacketFields.Any(field => !fields.ContainsKey(field)))
        {
            return "Required MP4 packet fields are absent; missing timing is not verified timing.";
        }

        var unknownField = fields.Keys.FirstOrDefault(field => !PacketFields.Contains(field) && !PermittedSkipFields.Contains(field));

        if (unknownField is not null)
        {
            return $"Unsupported MP4 packet side data or unexpected protocol field: {unknownField}.";
        }

        var timingFailure = ValidateTiming(request);

        if (timingFailure is not null)
        {
            return timingFailure;
        }

        if (!long.TryParse(fields["size"], NumberStyles.None, CultureInfo.InvariantCulture, out var size))
        {
            return "The MP4 packet size is not a finite nonnegative integer.";
        }

        var hash = fields["data_hash"];
        var validHash = hash.StartsWith(HashPrefix, StringComparison.Ordinal) && hash.Length == HashPrefix.Length + DigestHexCharacters
            && hash.AsSpan(HashPrefix.Length).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

        if (!validHash)
        {
            return "The MP4 packet SHA256 is incomplete or non-hexadecimal.";
        }

        var flags = fields["flags"];
        var validFlags = flags.Length == 3 && flags[0] is 'K' or '_' && flags[1] is 'D' or '_' && flags[2] is 'C' or '_';

        if (!validFlags)
        {
            return "The MP4 packet flags do not match the pinned probe protocol.";
        }

        fields["size"] = size.ToString(CultureInfo.InvariantCulture);
        fields["data_hash"] = HashPrefix + hash[HashPrefix.Length..].ToUpperInvariant();
        return ValidateSkipData(request);
    }

    /// <summary>Allows unknown timestamps only for explicitly untimed artwork, never as a missing-field fallback.</summary>
    private static string? ValidateTiming(Mp4PacketValidationRequest request)
    {
        foreach (var field in TimestampFields)
        {
            var text = request.Fields[field];

            if (request.IsArtwork && text == "N/A")
            {
                continue;
            }

            var style = field == "duration" ? NumberStyles.None : NumberStyles.AllowLeadingSign;

            if (!long.TryParse(text, style, CultureInfo.InvariantCulture, out _))
            {
                return $"The retained MP4 packet has no finite supported {field}; its timing cannot be verified.";
            }
        }

        return null;
    }

    /// <summary>Rejects unproven side-data types instead of hashing their name while omitting their actual contents.</summary>
    private static string? ValidateSkipData(Mp4PacketValidationRequest request)
    {
        var fields = request.Fields;

        if (!fields.Keys.Any(PermittedSkipFields.Contains))
        {
            return null;
        }

        var completeAudioSkip = request.Kind == TrackKind.Audio && !request.IsArtwork
            && PermittedSkipFields.All(fields.ContainsKey);

        if (!completeAudioSkip || fields[SkipPrefix + "side_data_type"] != "Skip Samples")
        {
            return "Only complete audio Skip Samples packet side data is supported.";
        }

        foreach (var field in SkipFields.Skip(1))
        {
            var key = SkipPrefix + field;
            var isReason = field is "skip_reason" or "discard_reason";

            if (isReason)
            {
                if (!byte.TryParse(fields[key], NumberStyles.None, CultureInfo.InvariantCulture, out var reason))
                {
                    return "An MP4 audio skip/padding reason is not a valid byte value.";
                }

                fields[key] = reason.ToString(CultureInfo.InvariantCulture);
                continue;
            }

            if (!uint.TryParse(fields[key], NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            {
                return "An MP4 audio skip/padding sample count is not an unsigned 32-bit integer.";
            }

            fields[key] = count.ToString(CultureInfo.InvariantCulture);
        }

        return null;
    }

    private sealed class StreamEvidence(string timebase, TrackKind kind, bool isArtwork)
    {
        internal string Timebase { get; } = timebase;
        internal TrackKind Kind { get; } = kind;
        internal bool IsArtwork { get; } = isArtwork;
        internal IncrementalHash Hash { get; } = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        internal long Count { get; set; }
    }
}
