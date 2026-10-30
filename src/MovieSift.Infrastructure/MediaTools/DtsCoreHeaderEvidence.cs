using System.Buffers.Binary;
using System.Collections.Immutable;
using MovieSift.Infrastructure.Files;

namespace MovieSift.Infrastructure.MediaTools;

/// <summary>Reads bounded first-frame DTS core evidence without extracting an audio payload or trusting a container default.</summary>
internal static class DtsCoreHeaderEvidence
{
    private const int PrefixBytes = 16;
    private const int MaximumBlockHeaderBytes = 4096;
    private const int MaximumElements = 4096;
    private const int MaximumReadBytes = 256 * 1024;
    private const long MaximumProbeOffset = 64 * 1024 * 1024;
    private const ulong Segment = 0x18538067;
    private const ulong Cluster = 0x1f43b675;
    private const ulong BlockGroup = 0xa0;
    private const ulong SimpleBlock = 0xa3;
    private const ulong Block = 0xa1;
    private static readonly int[] Frequencies = [0, 8000, 16000, 32000, 0, 0, 11025, 22050, 44100, 0, 0, 12000, 24000, 48000, 96000, 192000];
    private static readonly int[] Channels = [1, 2, 2, 2, 2, 3, 3, 4, 4, 5, 6, 6, 6, 7, 8, 8];

    /// <summary>Recognizes ordinary normal 16-bit big/little-endian core frames; unfamiliar encodings provide no exception authority.</summary>
    internal static DtsCoreFrameHeader? Parse(ReadOnlySpan<byte> prefix)
    {
        if (prefix.Length < PrefixBytes)
        {
            return null;
        }

        Span<byte> bytes = stackalloc byte[PrefixBytes];
        prefix[..PrefixBytes].CopyTo(bytes);
        var sync = BinaryPrimitives.ReadUInt32BigEndian(bytes);

        if (sync == 0xfe7f0180)
        {
            for (var offset = 0; offset < PrefixBytes; offset += 2)
            {
                (bytes[offset], bytes[offset + 1]) = (bytes[offset + 1], bytes[offset]);
            }
        }

        if (BinaryPrimitives.ReadUInt32BigEndian(bytes) != 0x7ffe8001 || Bits(bytes, 32, 1) != 1)
        {
            return null;
        }

        var samples = (Bits(bytes, 39, 7) + 1) * 32;
        var size = Bits(bytes, 46, 14) + 1;
        var rate = Frequencies[Bits(bytes, 66, 4)];
        var channelMode = Bits(bytes, 60, 6);
        var lfe = Bits(bytes, 85, 2);
        var resolutionOffset = Bits(bytes, 38, 1) == 1 ? 111 : 95;
        var resolution = Bits(bytes, resolutionOffset, 3) switch { 0 or 1 => 16, 2 or 3 => 20, 5 or 6 => 24, _ => 0 };
        var supported = samples is 512 or 1024 or 2048 && size >= 96 && rate > 0 && channelMode < Channels.Length && lfe != 3 && resolution > 0;

        if (!supported)
        {
            return null;
        }

        return new(rate, samples, resolution, Channels[channelMode] + (lfe == 0 ? 0 : 1), size);
    }

    /// <summary>Finds first target blocks by real Matroska track numbers, bounded elements/reads/offset and safe regular-file access.</summary>
    internal static async Task<ImmutableDictionary<ulong, DtsCoreFrameHeader>> ReadAsync(DtsCoreProbeRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        await using var source = FileSourceAccess.OpenRegularReadStream(request.SourcePath, FileOptions.RandomAccess);
        var result = ImmutableDictionary.CreateBuilder<ulong, DtsCoreFrameHeader>();
        var seenTracks = new HashSet<ulong>();
        var examined = 0;
        var readBytes = 0;
        await VisitAsync(0, source.Length, 0);
        return result.ToImmutable();

        /// <summary>Traverses only the Segment/Cluster/BlockGroup hierarchy and skips all other payloads.</summary>
        async Task VisitAsync(long start, long end, ulong parent)
        {
            var position = start;

            while (position < end && position < MaximumProbeOffset && examined++ < MaximumElements && seenTracks.Count < request.TrackNumbers.Count)
            {
                request.CancellationToken.ThrowIfCancellationRequested();
                var header = await ReadAsync(position, PrefixBytes);
                var cursor = 0;

                if (!Vint(header, ref cursor, true, out var id, out _) || !Vint(header, ref cursor, false, out var size, out var unknown))
                {
                    return;
                }

                var payload = position + cursor;
                var validContainer = parent switch
                {
                    0 => id == Segment,
                    Segment => id == Cluster,
                    Cluster => id == BlockGroup,
                    _ => false
                };

                if (payload > end)
                {
                    return;
                }

                if (unknown && id is not (Segment or Cluster))
                {
                    return;
                }

                if (!unknown && size > (ulong)(end - payload))
                {
                    return;
                }

                var elementEnd = unknown ? end : payload + (long)size;

                if (validContainer)
                {
                    await VisitAsync(payload, elementEnd, id);
                }

                var simpleBlock = parent == Cluster && id == SimpleBlock;
                var groupedBlock = parent == BlockGroup && id == Block;

                if (simpleBlock || groupedBlock)
                {
                    await ReadBlockAsync(payload, elementEnd - payload);
                }

                position = elementEnd;
            }
        }

        /// <summary>Reads only a bounded prefix and never continues after its I/O budget is exhausted.</summary>
        async Task<byte[]> ReadAsync(long offset, int length)
        {
            var count = (int)Math.Min(length, Math.Max(0, Math.Min(source.Length, MaximumProbeOffset) - offset));

            if (offset < 0 || offset > MaximumProbeOffset || count > MaximumReadBytes - readBytes)
            {
                return [];
            }

            readBytes += count;
            var bytes = new byte[count];
            var consumed = 0;

            while (consumed < count)
            {
                var actual = await RandomAccess.ReadAsync(source.SafeFileHandle, bytes.AsMemory(consumed), offset + consumed, request.CancellationToken);

                if (actual == 0)
                {
                    return bytes[..consumed];
                }

                consumed += actual;
            }

            return bytes;
        }

        /// <summary>Interprets only target track prefixes and their bounded lacing descriptors.</summary>
        async Task ReadBlockAsync(long offset, long length)
        {
            var initial = await ReadAsync(offset, (int)Math.Min(PrefixBytes, length));
            var cursor = 0;

            if (!Vint(initial, ref cursor, false, out var track, out _) || track == 0 || !request.TrackNumbers.Contains(track) || !seenTracks.Add(track))
            {
                return;
            }

            // Pinned mkvmerge derives these defaults from its first frame. Later recognizable blocks cannot prove that frame's parameters.
            var prefix = await ReadAsync(offset, (int)Math.Min(MaximumBlockHeaderBytes, length));
            var firstFrame = FirstFrame(prefix, length, cursor);

            if (firstFrame is not { } frame || prefix.Length - frame.Offset < PrefixBytes)
            {
                return;
            }

            var evidence = Parse(prefix.AsSpan(frame.Offset));

            if (evidence is not null && evidence.FrameBytes <= frame.Length)
            {
                result.Add(track, evidence);
            }
        }
    }

    /// <summary>Locates the first frame after none/fixed/Xiph/EBML lacing while validating every declared frame length.</summary>
    private static FrameRange? FirstFrame(ReadOnlySpan<byte> prefix, long payloadLength, int cursor)
    {
        if (prefix.Length - cursor < 3)
        {
            return null;
        }

        var lacing = prefix[cursor + 2] & 6;
        cursor += 3;

        if (lacing == 0)
        {
            return new(cursor, payloadLength - cursor);
        }

        if (cursor >= prefix.Length)
        {
            return null;
        }

        var count = prefix[cursor++] + 1;

        if (count == 1)
        {
            return new(cursor, payloadLength - cursor);
        }

        if (lacing == 4)
        {
            var available = payloadLength - cursor;
            return available > 0 && available % count == 0 ? new(cursor, available / count) : null;
        }

        long first = 0;
        long previous = 0;
        long sum = 0;

        for (var index = 0; index < count - 1; index++)
        {
            long size = 0;

            if (lacing == 2)
            {
                byte part;
                do
                {
                    if (cursor >= prefix.Length)
                    {
                        return null;
                    }

                    part = prefix[cursor++];
                    size += part;
                } while (part == byte.MaxValue);
            }

            if (lacing == 6)
            {
                var before = cursor;

                if (!Vint(prefix, ref cursor, false, out var encoded, out var unknown) || unknown || encoded > long.MaxValue)
                {
                    return null;
                }

                var width = cursor - before;
                size = index == 0 ? (long)encoded : previous + (long)encoded - ((1L << (7 * width - 1)) - 1);
            }

            if (size <= 0 || size > payloadLength - sum)
            {
                return null;
            }

            first = index == 0 ? size : first;
            previous = size;
            sum += size;
        }

        return first > 0 && sum < payloadLength - cursor ? new(cursor, first) : null;
    }

    /// <summary>Decodes bounded EBML identifiers/sizes without accepting malformed or truncated integers.</summary>
    private static bool Vint(ReadOnlySpan<byte> bytes, ref int cursor, bool identifier, out ulong value, out bool unknown)
    {
        value = 0;
        unknown = false;

        if (cursor >= bytes.Length || bytes[cursor] == 0)
        {
            return false;
        }

        var mask = 0x80;
        var width = 1;

        while ((bytes[cursor] & mask) == 0)
        {
            mask >>= 1;
            width++;
        }

        if (width > (identifier ? 4 : 8) || bytes.Length - cursor < width)
        {
            return false;
        }

        value = identifier ? bytes[cursor] : (ulong)(bytes[cursor] & (mask - 1));
        cursor++;

        for (var index = 1; index < width; index++)
        {
            value = (value << 8) | bytes[cursor++];
        }

        unknown = !identifier && value == (1UL << (7 * width)) - 1;
        return true;
    }

    /// <summary>Reads a fixed-width core header field in network bit order.</summary>
    private static int Bits(ReadOnlySpan<byte> bytes, int offset, int count)
    {
        var value = 0;

        for (var index = 0; index < count; index++)
        {
            var position = offset + index;
            value = (value << 1) | ((bytes[position / 8] >> (7 - position % 8)) & 1);
        }

        return value;
    }

    private sealed record FrameRange(int Offset, long Length);
}

internal sealed record DtsCoreFrameHeader(int SamplingFrequency, int SamplesPerFrame, int BitsPerSample, int Channels, int FrameBytes);
internal sealed record DtsCoreProbeRequest(string SourcePath, IReadOnlySet<ulong> TrackNumbers, CancellationToken CancellationToken);
