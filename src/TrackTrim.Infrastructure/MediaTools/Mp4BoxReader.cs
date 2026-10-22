using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using TrackTrim.Application;
using TrackTrim.Application.Ports;
using TrackTrim.Core.Failures;
using TrackTrim.Infrastructure.Files;

namespace TrackTrim.Infrastructure.MediaTools;

/// <summary>Seeks over media/sample tables and checks only the bounded MP4 capability structures omitted by ffprobe.</summary>
internal sealed class Mp4BoxReader
{
    internal const int MaximumBoxHeaders = 100_000;
    internal const int MaximumDepth = 16;
    private const int HeaderBytes = 8;
    private const int ExtendedHeaderBytes = 16;
    private const int MaximumSmallBoxBytes = 65_536;
    private const int MaximumTracks = 256;
    private const int MaximumReferencedChapterTracks = 1;
    private static readonly HashSet<string> Brands = new(StringComparer.Ordinal)
        { "isom", "iso2", "iso3", "iso4", "iso5", "iso6", "mp41", "mp42", "avc1" };
    private static readonly HashSet<string> MetadataAtoms = new(StringComparer.Ordinal)
        { "©nam", "©ART", "aART", "©alb", "©day", "©cmt", "©gen", "©wrt", "©too", "cprt", "desc", "ldes", "purd", "trkn", "disk", "cpil", "pgap", "tmpo", "covr", "soal", "soar", "sonm", "soco", "sosn", "sost", "stik", "rtng" };
    private readonly FileStream stream;
    private readonly CancellationToken cancellationToken;
    private readonly List<MutableTrack> tracks = [];
    private readonly HashSet<uint> chapterTrackIds = [];
    private readonly List<string> artworkCodecs = [];
    private ImmutableArray<Mp4NeroChapter> neroChapters;
    private int headerCount;
    private string? majorBrand;
    private uint movieTimescale;
    private int movieCount;
    private int mediaCount;

    /// <summary>Keeps parsing state private to one regular-file handle.</summary>
    private Mp4BoxReader(FileStream stream, CancellationToken cancellationToken)
    {
        this.stream = stream;
        this.cancellationToken = cancellationToken;
    }

    /// <summary>Checks a local regular file without requesting source write access.</summary>
    internal static async Task<Mp4Structure> ReadAsync(SourceRequest request)
    {
        await using var stream = FileSourceAccess.OpenRegularReadStream(request.Path);
        return await ReadAsync(stream, request.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>Also permits the same checks on an already validated, exclusively owned candidate handle.</summary>
    internal static async Task<Mp4Structure> ReadAsync(FileStream stream, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        var reader = new Mp4BoxReader(stream, cancellationToken);

        try
        {
            await reader.ReadChildrenAsync(new(stream.Length, "root", 0, null)).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            throw Unsupported("The MP4 box header or required field is truncated.");
        }

        var invalidDocument = reader.majorBrand is null || reader.movieCount != 1 || reader.mediaCount == 0
            || reader.movieTimescale == 0 || reader.tracks.Count == 0;

        if (invalidDocument)
        {
            throw Unsupported("One ordinary MP4 ftyp/moov document with self-contained media is required.");
        }

        if (reader.tracks.Any(track => track.Id == 0 || track.SampleEntry is null || track.MediaTimescale == 0)
            || reader.tracks.Select(track => track.Id).Distinct().Count() != reader.tracks.Count)
        {
            throw Unsupported("Every MP4 track must have one distinct container identifier and sample description.");
        }

        if (!reader.chapterTrackIds.IsSubsetOf(reader.tracks.Select(track => track.Id)))
        {
            throw Unsupported("A chapter reference names an absent MP4 track.");
        }

        if (reader.chapterTrackIds.Count > MaximumReferencedChapterTracks)
        {
            throw Unsupported("Multiple distinct referenced QuickTime chapter tracks are unsupported; their separate chapter contents cannot be preserved safely.");
        }

        return new(reader.majorBrand!, reader.movieTimescale,
            reader.tracks.Select(track => new Mp4TrackHeader(track.Id, track.FlagsOffset, track.Flags, track.SampleEntry!)).ToImmutableArray(),
            reader.chapterTrackIds.ToImmutableHashSet(), reader.artworkCodecs.ToImmutableArray(), reader.neroChapters);
    }

    /// <summary>Visits only schema-defined containers; media/sample payloads are never recursively searched.</summary>
    private async Task ReadChildrenAsync(ChildrenRequest request)
    {
        if (request.Depth > MaximumDepth)
        {
            throw Unsupported("The MP4 capability-inspection depth bound was exceeded.");
        }

        while (stream.Position < request.End)
        {
            var box = await ReadHeaderAsync(request.End).ConfigureAwait(false);
            await ReadBoxAsync(new(box, request.Parent, request.Depth, request.Track)).ConfigureAwait(false);
            stream.Position = box.End;
        }
    }

    /// <summary>Rejects unsupported layouts at their actual schema positions rather than matching arbitrary bytes.</summary>
    private async Task ReadBoxAsync(BoxRequest request)
    {
        var (box, parent, depth, track) = request;

        if (box.Type is "free" or "skip" or "wide")
        {
            return;
        }

        if (parent == "root")
        {
            await ReadRootBoxAsync(box, depth).ConfigureAwait(false);
            return;
        }

        if (parent == "moov" && box.Type == "trak")
        {
            if (tracks.Count >= MaximumTracks)
            {
                throw Unsupported("The MP4 track-count bound was exceeded.");
            }

            var childTrack = new MutableTrack();
            tracks.Add(childTrack);
            await ReadChildrenAsync(new(box.End, box.Type, depth + 1, childTrack)).ConfigureAwait(false);
            return;
        }

        if (parent == "moov" && box.Type == "mvhd")
        {
            var bytes = await ReadSmallAsync(box).ConfigureAwait(false);
            var offset = VersionOffset(bytes, 12, 20);
            movieTimescale = ReadUInt32(bytes, offset);
            return;
        }

        if (parent == "trak" && box.Type == "tkhd" && track is not null)
        {
            var bytes = await ReadSmallAsync(box).ConfigureAwait(false);
            track.Id = ReadUInt32(bytes, VersionOffset(bytes, 12, 20));
            track.FlagsOffset = box.DataOffset + 1;
            track.Flags = ReadUInt32(bytes, 0) & 0x00FF_FFFF;

            if ((track.Flags & ~Mp4TrackHeader.SupportedFlags) != 0)
            {
                throw Unsupported("Only the ordinary enabled/in-movie/in-preview MP4 track header flags are supported.");
            }

            return;
        }

        if (parent == "mdia" && box.Type == "mdhd" && track is not null)
        {
            var bytes = await ReadSmallAsync(box).ConfigureAwait(false);

            if (track.MediaTimescale != 0)
            {
                throw Unsupported("Duplicate MP4 media headers are unsupported.");
            }

            track.MediaTimescale = ReadUInt32(bytes, VersionOffset(bytes, 12, 20));

            if (track.MediaTimescale == 0)
            {
                throw Unsupported("Every ordinary MP4 media track must have an explicit positive timescale.");
            }

            return;
        }

        if (parent == "edts" && box.Type == "elst")
        {
            await ValidateEditListAsync(box).ConfigureAwait(false);
            return;
        }

        if (parent == "tref" && box.Type == "chap")
        {
            var bytes = await ReadSmallAsync(box).ConfigureAwait(false);

            if (bytes.Length == 0 || bytes.Length % sizeof(uint) != 0)
            {
                throw Unsupported("The chapter-reference payload is malformed.");
            }

            for (var offset = 0; offset < bytes.Length; offset += sizeof(uint))
            {
                chapterTrackIds.Add(ReadUInt32(bytes, offset));
            }

            return;
        }

        if (parent == "dinf" && box.Type == "dref")
        {
            await ValidateDataReferencesAsync(box).ConfigureAwait(false);
            return;
        }

        if (parent == "udta" && box.Type == "chpl")
        {
            if (track is not null || !neroChapters.IsDefault)
            {
                throw Unsupported("Nero chapters must have one unambiguous movie-level representation.");
            }

            await ReadNeroChaptersAsync(box).ConfigureAwait(false);
            return;
        }

        if (parent == "udta" && box.Type == "kind" && track is not null)
        {
            var bytes = await ReadSmallAsync(box).ConfigureAwait(false);
            var text = bytes.Length < sizeof(uint) ? string.Empty : Encoding.UTF8.GetString(bytes, sizeof(uint), bytes.Length - sizeof(uint));
            var fields = text.Split('\0');
            var supportedRole = fields.Length == 3 && fields[0] == "urn:mpeg:dash:role:2011" && fields[2].Length == 0
                && fields[1] is "caption" or "commentary" or "description" or "dub" or "forced-subtitle";

            if (bytes.Length < sizeof(uint) || ReadUInt32(bytes, 0) != 0 || !supportedRole)
            {
                throw Unsupported("An unsupported MP4 track-kind scheme or role was found.");
            }

            return;
        }

        if (parent == "stbl" && box.Type == "stsd" && track is not null)
        {
            await ValidateSampleDescriptionAsync(box, track, depth).ConfigureAwait(false);
            return;
        }

        if (parent == "stbl" && box.Type is "sgpd" or "sbgp")
        {
            var bytes = await ReadPrefixAsync(box, 8).ConfigureAwait(false);
            var grouping = Encoding.Latin1.GetString(bytes, 4, 4);

            if (grouping is not ("roll" or "prol"))
            {
                throw Unsupported($"Unsupported MP4 sample grouping: {grouping}.");
            }

            return;
        }

        if (box.Type == "meta" && parent is "moov" or "udta")
        {
            var versionFlags = await ReadPrefixAsync(box, sizeof(uint)).ConfigureAwait(false);

            if (ReadUInt32(versionFlags, 0) != 0)
            {
                throw Unsupported("Only ordinary version-zero MP4 metadata is supported.");
            }

            await ReadChildrenAsync(new(box.End, "meta", depth + 1, track)).ConfigureAwait(false);
            return;
        }

        if (parent == "ilst")
        {
            if (!MetadataAtoms.Contains(box.Type))
            {
                throw Unsupported($"Unsupported MP4 metadata atom: {box.Type}.");
            }

            await ReadChildrenAsync(new(box.End, box.Type == "covr" ? "covr" : "metadata-value", depth + 1, track)).ConfigureAwait(false);
            return;
        }

        if (parent == "covr" && box.Type == "data")
        {
            await ValidateArtworkAsync(box).ConfigureAwait(false);
            return;
        }

        if (parent == "metadata-value" && box.Type == "data")
        {
            var prefix = await ReadPrefixAsync(box, 8).ConfigureAwait(false);
            var dataType = ReadUInt32(prefix, 0);

            if (dataType is not (0 or 1 or 21 or 22) || ReadUInt32(prefix, 4) != 0)
            {
                throw Unsupported("An unsupported MP4 metadata data type or locale was found.");
            }

            return;
        }

        if (parent == "avc1" && box.Type == "colr")
        {
            var prefix = await ReadPrefixAsync(box, 4).ConfigureAwait(false);
            var colorType = Encoding.Latin1.GetString(prefix);

            if (colorType is not ("nclx" or "nclc"))
            {
                throw Unsupported("ICC/profile MP4 color metadata is outside the proven subset.");
            }

            return;
        }

        if (IsContainer(parent, box.Type))
        {
            await ReadChildrenAsync(new(box.End, box.Type, depth + 1, track)).ConfigureAwait(false);
            return;
        }

        if (IsOrdinaryLeaf(parent, box.Type))
        {
            return;
        }

        throw Unsupported($"Unsupported MP4 structure {parent}/{box.Type}; it cannot be silently discarded.");
    }

    /// <summary>Requires an actual MP4 brand and rejects fragmented or additional container documents.</summary>
    private async Task ReadRootBoxAsync(Box box, int depth)
    {
        if (box.Type == "ftyp")
        {
            var bytes = await ReadSmallAsync(box).ConfigureAwait(false);

            if (majorBrand is not null || bytes.Length < 8 || bytes.Length % sizeof(uint) != 0)
            {
                throw Unsupported("The MP4 file-type box is missing, duplicated or malformed.");
            }

            majorBrand = Encoding.Latin1.GetString(bytes, 0, 4);

            if (!Brands.Contains(majorBrand))
            {
                throw new OperationFailureException(new(ErrorCode.UnsupportedContainer, $"Unsupported MP4 major brand: {majorBrand}."));
            }

            return;
        }

        if (box.Type == "moov")
        {
            movieCount++;
            await ReadChildrenAsync(new(box.End, "moov", depth + 1, null)).ConfigureAwait(false);
            return;
        }

        if (box.Type == "mdat")
        {
            mediaCount++;
            return;
        }

        throw Unsupported($"Unsupported MP4 top-level box: {box.Type}; fragmented/encrypted/extended layouts are outside the proven subset.");
    }

    /// <summary>Allows one normal rate-one edit, optionally preceded by one empty delay; rejects cutting/splicing/dwell sequences.</summary>
    private async Task ValidateEditListAsync(Box box)
    {
        var bytes = await ReadSmallAsync(box).ConfigureAwait(false);
        var version = bytes.Length == 0 ? byte.MaxValue : bytes[0];
        var count = ReadUInt32(bytes, 4);
        var entryBytes = version == 1 ? 20 : 12;
        var malformed = version > 1 || count is < 1 or > 2 || bytes.Length != 8 + count * entryBytes;

        if (malformed)
        {
            throw Unsupported("Only one rate-one MP4 presentation edit, optionally preceded by an empty delay, is supported.");
        }

        for (var index = 0; index < count; index++)
        {
            var offset = 8 + index * entryBytes;
            var duration = version == 1 ? BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(offset)) : ReadUInt32(bytes, offset);
            var mediaTime = version == 1 ? BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(offset + 8)) : BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset + 4));
            var rateOffset = offset + entryBytes - sizeof(uint);
            var isEmptyDelay = index == 0 && count == 2 && mediaTime == -1;
            var invalid = duration == 0 || (count == 2 && index == 0 && !isEmptyDelay)
                || (!isEmptyDelay && mediaTime < 0) || ReadUInt32(bytes, rateOffset) != 0x0001_0000;

            if (invalid)
            {
                throw Unsupported("Complex, reversed, zero-duration or non-rate-one MP4 edits are unsupported.");
            }
        }
    }

    /// <summary>Checks admitted cover types/signatures so artwork omitted by a probe cannot silently disappear.</summary>
    private async Task ValidateArtworkAsync(Box box)
    {
        var prefix = await ReadPrefixAsync(box, 8).ConfigureAwait(false);
        var dataType = ReadUInt32(prefix, 0);

        if (dataType is not (13 or 14) || ReadUInt32(prefix, 4) != 0)
        {
            throw Unsupported("Only ordinary JPEG/PNG MP4 cover artwork with a neutral locale is supported.");
        }

        var signature = await ReadPrefixAsync(box, dataType == 13 ? 3 : 8).ConfigureAwait(false);
        var valid = dataType == 13 ? signature.AsSpan().SequenceEqual(new byte[] { 0xFF, 0xD8, 0xFF })
            : signature.AsSpan().SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        if (!valid)
        {
            throw Unsupported("MP4 cover artwork does not match its declared JPEG/PNG representation.");
        }

        artworkCodecs.Add(dataType == 13 ? "mjpeg" : "png");
    }

    /// <summary>Reads Nero's version-zero/one chapter starts and UTF8 titles in 100 ns units, without parsing media samples.</summary>
    private async Task ReadNeroChaptersAsync(Box box)
    {
        var bytes = await ReadSmallAsync(box).ConfigureAwait(false);
        var versionFlags = ReadUInt32(bytes, 0);
        var offset = versionFlags == 0 ? sizeof(uint) : sizeof(ulong);
        var validHeader = versionFlags is 0 or 0x0100_0000 && bytes.Length > offset;

        if (!validHeader || versionFlags != 0 && ReadUInt32(bytes, sizeof(uint)) != 0 || bytes[offset] == 0)
        {
            throw Unsupported("Only nonempty ordinary version-zero/one Nero chapter lists are supported.");
        }

        var count = bytes[offset++];
        var chapters = ImmutableArray.CreateBuilder<Mp4NeroChapter>(count);
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        for (var index = 0; index < count; index++)
        {
            if (bytes.Length - offset < sizeof(long) + sizeof(byte))
            {
                throw Unsupported("A Nero chapter entry is truncated.");
            }

            var startTicks = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(offset));
            offset += sizeof(long);
            var titleLength = bytes[offset++];
            var invalidEntry = startTicks < 0 || bytes.Length - offset < titleLength
                || chapters.Count != 0 && startTicks <= chapters[^1].StartTicks;

            if (invalidEntry)
            {
                throw Unsupported("Nero chapter starts must be nonnegative and increasing, with complete title bytes.");
            }

            string title;

            try
            {
                title = encoding.GetString(bytes, offset, titleLength);
            }
            catch (DecoderFallbackException)
            {
                throw Unsupported("Nero chapter titles must use valid UTF8.");
            }

            if (title.Contains('\0'))
            {
                throw Unsupported("Embedded nulls in Nero chapter titles are unsupported.");
            }

            chapters.Add(new(startTicks, title));
            offset += titleLength;
        }

        if (offset != bytes.Length)
        {
            throw Unsupported("Uninterpreted trailing Nero chapter data is unsupported.");
        }

        neroChapters = chapters.MoveToImmutable();
    }

    /// <summary>Requires every data reference to identify this file; external protocols remain disabled independently in tools.</summary>
    private async Task ValidateDataReferencesAsync(Box box)
    {
        var prefix = await ReadPrefixAsync(box, 8).ConfigureAwait(false);
        var count = ReadUInt32(prefix, 4);

        if (count != 1 || ReadUInt32(prefix, 0) != 0)
        {
            throw Unsupported("Exactly one ordinary self-contained MP4 data reference is required.");
        }

        var entry = await ReadHeaderAsync(box.End).ConfigureAwait(false);
        var flags = await ReadPrefixAsync(entry, 4).ConfigureAwait(false);

        if (entry.Type != "url " || ReadUInt32(flags, 0) != 1 || entry.End != stream.Position || entry.End != box.End)
        {
            throw Unsupported("External MP4 media references are not supported.");
        }
    }

    /// <summary>Admits only the proven sample entries and rejects encrypted or advanced codec-child representations.</summary>
    private async Task ValidateSampleDescriptionAsync(Box box, MutableTrack track, int depth)
    {
        var prefix = await ReadPrefixAsync(box, 8).ConfigureAwait(false);

        if (ReadUInt32(prefix, 0) != 0 || ReadUInt32(prefix, 4) != 1 || track.SampleEntry is not null)
        {
            throw Unsupported("One ordinary sample description per MP4 track is required.");
        }

        var sample = await ReadHeaderAsync(box.End).ConfigureAwait(false);
        track.SampleEntry = sample.Type;
        var samplePrefixLength = sample.Type switch { "avc1" => 78, "mp4a" => 28, "tx3g" => 8, "text" => 8, _ => -1 };

        if (samplePrefixLength < 0)
        {
            throw Unsupported($"Unsupported/encrypted MP4 sample entry: {sample.Type}.");
        }

        var samplePrefix = await ReadPrefixAsync(sample, samplePrefixLength).ConfigureAwait(false);

        if (BinaryPrimitives.ReadUInt16BigEndian(samplePrefix.AsSpan(6)) != 1)
        {
            throw Unsupported("The MP4 sample entry names an unsupported data reference.");
        }

        if (sample.Type == "mp4a" && BinaryPrimitives.ReadUInt16BigEndian(samplePrefix.AsSpan(8)) != 0)
        {
            throw Unsupported("QuickTime extended audio sample descriptions are unsupported.");
        }

        if (sample.Type is "avc1" or "mp4a")
        {
            await ReadChildrenAsync(new(sample.End, sample.Type, depth + 1, track)).ConfigureAwait(false);
        }

        if (sample.End != box.End)
        {
            throw Unsupported("Additional MP4 sample descriptions are unsupported.");
        }
    }

    /// <summary>Reads a finite header and validates arithmetic before seeking over any payload.</summary>
    private async Task<Box> ReadHeaderAsync(long parentEnd)
    {
        cancellationToken.ThrowIfCancellationRequested();
        headerCount++;

        if (headerCount > MaximumBoxHeaders || parentEnd - stream.Position < HeaderBytes)
        {
            throw Unsupported("The bounded MP4 box-header budget or parent boundary was exceeded.");
        }

        var start = stream.Position;
        var bytes = new byte[ExtendedHeaderBytes];
        await stream.ReadExactlyAsync(bytes.AsMemory(0, HeaderBytes), cancellationToken).ConfigureAwait(false);
        ulong length = ReadUInt32(bytes, 0);
        var headerLength = HeaderBytes;

        if (length == 1)
        {
            if (parentEnd - start < ExtendedHeaderBytes)
            {
                throw Unsupported("An extended MP4 box header exceeds its parent.");
            }

            await stream.ReadExactlyAsync(bytes.AsMemory(HeaderBytes, HeaderBytes), cancellationToken).ConfigureAwait(false);
            length = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(HeaderBytes));
            headerLength = ExtendedHeaderBytes;
        }

        if (length == 0)
        {
            length = (ulong)(parentEnd - start);
        }

        if (length < (ulong)headerLength || length > (ulong)(parentEnd - start))
        {
            throw Unsupported("An MP4 box extends beyond its parent or file.");
        }

        return new(Encoding.Latin1.GetString(bytes, 4, 4), start + headerLength, start + (long)length);
    }

    /// <summary>Reads only a small required field prefix; tables and encoded payloads stay on disk.</summary>
    private async Task<byte[]> ReadPrefixAsync(Box box, int length)
    {
        if (box.End - stream.Position < length)
        {
            throw Unsupported($"The MP4 {box.Type} field prefix is truncated.");
        }

        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    /// <summary>Bounds allocations for the handful of variable-length capability boxes actually interpreted.</summary>
    private Task<byte[]> ReadSmallAsync(Box box)
    {
        var length = box.End - stream.Position;

        if (length > MaximumSmallBoxBytes)
        {
            throw Unsupported($"The MP4 {box.Type} capability box exceeds its bound.");
        }

        return ReadPrefixAsync(box, (int)length);
    }

    /// <summary>Interprets only version-zero/one full-box layouts supported by the selected structures.</summary>
    private static int VersionOffset(byte[] bytes, int versionZeroOffset, int versionOneOffset)
    {
        if (bytes.Length == 0 || bytes[0] > 1)
        {
            throw Unsupported("An unsupported MP4 full-box version was found.");
        }

        return bytes[0] == 0 ? versionZeroOffset : versionOneOffset;
    }

    /// <summary>Rejects absent required integers before using their values or offsets.</summary>
    private static uint ReadUInt32(byte[] bytes, int offset)
    {
        if (offset < 0 || bytes.Length - offset < sizeof(uint))
        {
            throw Unsupported("A required MP4 integer is truncated.");
        }

        return BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset));
    }

    /// <summary>Lists only the container nodes needed for the admitted baseline.</summary>
    private static bool IsContainer(string parent, string child) => (parent, child) switch
    {
        ("moov", "udta") or ("trak", "mdia" or "edts" or "tref" or "udta")
            or ("mdia", "minf") or ("minf", "dinf" or "stbl") or ("meta", "ilst") => true,
        _ => false,
    };

    /// <summary>Allows ordinary noncapability leaves without treating unknown boxes as preserved.</summary>
    private static bool IsOrdinaryLeaf(string parent, string child) => (parent, child) switch
    {
        ("mdia", "mdhd" or "hdlr") or ("minf", "vmhd" or "smhd" or "nmhd" or "gmhd")
            or ("stbl", "stts" or "ctts" or "stsc" or "stsz" or "stz2" or "stco" or "co64" or "stss" or "sdtp")
            or ("udta", "name") or ("meta", "hdlr")
            or ("avc1", "avcC" or "pasp" or "btrt") or ("mp4a", "esds" or "btrt") => true,
        _ => false,
    };

    /// <summary>Fails closed with a stable expected unsupported-feature outcome.</summary>
    private static OperationFailureException Unsupported(string detail) => new(new(ErrorCode.UnsupportedFeature, detail));

    private sealed record Box(string Type, long DataOffset, long End);
    private sealed record ChildrenRequest(long End, string Parent, int Depth, MutableTrack? Track);
    private sealed record BoxRequest(Box Box, string Parent, int Depth, MutableTrack? Track);
    private sealed class MutableTrack
    {
        internal uint Id { get; set; }
        internal long FlagsOffset { get; set; }
        internal uint Flags { get; set; }
        internal uint MediaTimescale { get; set; }
        internal string? SampleEntry { get; set; }
    }
}
