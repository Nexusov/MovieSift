using System.Buffers.Binary;
using System.Globalization;
using MovieSift.Infrastructure.Files;

namespace MovieSift.Infrastructure.MediaTools;

/// <summary>Reconstructs only codec-proven DTS SimpleBlock lace timestamps that the extractor cannot express without a default.</summary>
internal static class DtsLacedTimestampEvidence
{
    private const int MaximumElements = 10_000_000;
    private const int MaximumFrames = 10_000_000;
    private const int MaximumHeaderReadBytes = 128 * 1024 * 1024;
    private const int MaximumDepth = 4;
    private const int MaximumPendingFrames = 4096;
    private const int HeaderBytes = 16;
    private const decimal NanosecondsPerMillisecond = 1_000_000;
    private const decimal NanosecondsPerSecond = 1_000_000_000;
    private const decimal ExtractorRoundingNanoseconds = 1;
    private const ulong Segment = 0x18538067;
    private const ulong Info = 0x1549a966;
    private const ulong TimestampScale = 0x2ad7b1;
    private const ulong Cluster = 0x1f43b675;
    private const ulong ClusterTimestamp = 0xe7;
    private const ulong BlockGroup = 0xa0;
    private const ulong SimpleBlock = 0xa3;
    private const ulong Block = 0xa1;

    /// <summary>Writes a fresh owned scratch reference; false leaves it unusable and the caller retains ordinary scratch cleanup responsibility.</summary>
    internal static Task<bool> WriteReferenceAsync(DtsLacedTimestampRequest request) => TraverseAsync(new(request.SourcePath,
        request.TrackNumber, request.ExpectedHeader, request.CancellationToken, request.ExtractedTimestampPath, request.ReferencePath));

    /// <summary>Validates every target core frame and block anchor before materializing a duration in an owned input copy.</summary>
    internal static Task<bool> ValidateProfileAsync(DtsProfileValidation request) => TraverseAsync(new(request.SourcePath,
        request.TrackNumber, request.ExpectedHeader, request.CancellationToken));

    /// <summary>Shares one bounded traversal between read-only profile validation and an independently corroborated timestamp reference.</summary>
    private static async Task<bool> TraverseAsync(TraversalRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();

        if (request.TrackNumber == 0 || request.ExpectedHeader.SamplingFrequency <= 0)
        {
            return false;
        }

        try
        {
            await using var source = FileSourceAccess.OpenRegularReadStream(request.SourcePath, FileOptions.SequentialScan);
            using var extracted = request.ExtractedTimestampPath is null ? null : File.OpenText(request.ExtractedTimestampPath);

            if (extracted is not null && await extracted.ReadLineAsync(request.CancellationToken).ConfigureAwait(false) != "# timestamp format v2")
            {
                return false;
            }

            await using var referenceStream = request.ReferencePath is null ? null :
                new FileStream(request.ReferencePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await using var reference = referenceStream is null ? null : new StreamWriter(referenceStream);

            if (reference is not null)
            {
                await reference.WriteLineAsync("# timestamp format v2").ConfigureAwait(false);
            }

            var elements = 0;
            var readBytes = 0;
            var frames = 0;
            var scale = 1_000_000UL;
            var sawScale = false;
            var sawLace = false;
            decimal? lastRawTimestamp = null;
            decimal? previousBlockTimestamp = null;
            decimal? lastWrittenTimestamp = null;
            var pending = new PriorityQueue<decimal, decimal>();
            var frameDuration = request.ExpectedHeader.SamplesPerFrame * NanosecondsPerSecond / request.ExpectedHeader.SamplingFrequency;
            await VisitAsync(0, source.Length, 0, null, 0).ConfigureAwait(false);

            if (lastRawTimestamp is null)
            {
                return false;
            }

            if (reference is null)
            {
                return true;
            }

            if (!sawLace)
            {
                return false;
            }

            await RequireRawTimestampAsync(lastRawTimestamp.Value).ConfigureAwait(false);

            if (extracted is null || await extracted.ReadLineAsync(request.CancellationToken).ConfigureAwait(false) is not null)
            {
                return false;
            }

            await FlushBeforeAsync(decimal.MaxValue).ConfigureAwait(false);
            // Match pinned timestamps_v2 presentation sorting, while keeping its unknown terminal separate from real frames.
            await WriteTimestampAsync(lastWrittenTimestamp!.Value).ConfigureAwait(false);
            await reference.FlushAsync(request.CancellationToken).ConfigureAwait(false);
            return true;

            /// <summary>Visits only valid Matroska parent relationships, skipping encoded media payloads by their finite lengths.</summary>
            async Task VisitAsync(long start, long end, ulong parent, ulong? clusterTime, int depth)
            {

                if (depth > MaximumDepth)
                {
                    throw new UnprovenLayoutException();
                }

                var position = start;
                var currentClusterTime = clusterTime;

                while (position < end)
                {
                    request.CancellationToken.ThrowIfCancellationRequested();

                    if (++elements > MaximumElements)
                    {
                        throw new UnprovenLayoutException();
                    }

                    var bytes = await ReadAsync(position, HeaderBytes).ConfigureAwait(false);
                    var cursor = 0;

                    if (!Vint(bytes, ref cursor, true, out var identifier, out _) || !Vint(bytes, ref cursor, false, out var size, out var unknown))
                    {
                        throw new UnprovenLayoutException();
                    }

                    var payload = position + cursor;

                    if (payload > end || unknown && identifier != Segment || !unknown && size > (ulong)(end - payload))
                    {
                        throw new UnprovenLayoutException();
                    }

                    var elementEnd = unknown ? end : payload + (long)size;
                    var nested = parent switch
                    {
                        0 => identifier == Segment,
                        Segment => identifier is Info or Cluster,
                        Cluster => identifier == BlockGroup,
                        _ => false
                    };

                    if (nested)
                    {
                        await VisitAsync(payload, elementEnd, identifier, null, depth + 1).ConfigureAwait(false);
                    }

                    if (parent == Info && identifier == TimestampScale)
                    {

                        if (sawScale || frames != 0)
                        {
                            throw new UnprovenLayoutException();
                        }

                        scale = await ReadUnsignedAsync(payload, elementEnd - payload).ConfigureAwait(false);
                        sawScale = true;

                        if (scale == 0 || scale > 1_000_000_000)
                        {
                            throw new UnprovenLayoutException();
                        }
                    }

                    if (parent == Cluster && identifier == ClusterTimestamp)
                    {

                        if (currentClusterTime is not null)
                        {
                            throw new UnprovenLayoutException();
                        }

                        currentClusterTime = await ReadUnsignedAsync(payload, elementEnd - payload).ConfigureAwait(false);
                    }

                    var simple = parent == Cluster && identifier == SimpleBlock;
                    var grouped = parent == BlockGroup && identifier == Block;

                    if (simple || grouped)
                    {
                        await ReadBlockAsync(payload, elementEnd - payload, simple, currentClusterTime).ConfigureAwait(false);
                    }

                    position = elementEnd;
                }
            }

            /// <summary>Validates actual target lace lengths and every core prefix before deriving any previously unknown intra-lace offset.</summary>
            async Task ReadBlockAsync(long offset, long length, bool simple, ulong? clusterTime)
            {
                var bytes = await ReadAsync(offset, (int)Math.Min(HeaderBytes, length)).ConfigureAwait(false);
                var cursor = 0;

                if (!Vint(bytes, ref cursor, false, out var number, out _))
                {
                    throw new UnprovenLayoutException();
                }

                if (number != request.TrackNumber)
                {
                    return;
                }

                if (!simple || clusterTime is null || bytes.Length - cursor < 3)
                {
                    throw new UnprovenLayoutException();
                }

                var relative = BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(cursor, 2));
                var lacing = bytes[cursor + 2] & 6;
                cursor += 3;

                if (lacing is not (0 or 4))
                {
                    throw new UnprovenLayoutException();
                }

                var count = 1;

                if (lacing == 4)
                {

                    if (cursor >= bytes.Length)
                    {
                        throw new UnprovenLayoutException();
                    }

                    count = bytes[cursor++] + 1;
                    sawLace |= count > 1;
                }

                var available = length - cursor;

                if (available <= 0 || available % count != 0 || available / count != request.ExpectedHeader.FrameBytes)
                {
                    throw new UnprovenLayoutException();
                }

                var rawTime = ((decimal)clusterTime.Value + relative) * scale;

                if (previousBlockTimestamp is { } previous && rawTime < previous)
                {
                    throw new UnprovenLayoutException();
                }

                previousBlockTimestamp = rawTime;
                await FlushBeforeAsync(rawTime).ConfigureAwait(false);

                for (var frame = 0; frame < count; frame++)
                {

                    if (++frames > MaximumFrames)
                    {
                        throw new UnprovenLayoutException();
                    }

                    var prefix = await ReadAsync(offset + cursor + (long)frame * request.ExpectedHeader.FrameBytes, HeaderBytes).ConfigureAwait(false);

                    var actualHeader = DtsCoreHeaderEvidence.Parse(prefix);
                    var timingProven = actualHeader is not null && actualHeader.SamplingFrequency == request.ExpectedHeader.SamplingFrequency &&
                        actualHeader.SamplesPerFrame == request.ExpectedHeader.SamplesPerFrame && actualHeader.FrameBytes == request.ExpectedHeader.FrameBytes &&
                        actualHeader.Channels == request.ExpectedHeader.Channels;

                    // PCM precision may vary across encoded core frames; it does not determine their sample count or presentation duration.
                    if (!timingProven)
                    {
                        throw new UnprovenLayoutException();
                    }

                    await RequireRawTimestampAsync(rawTime).ConfigureAwait(false);
                    var derived = rawTime + frame * frameDuration;

                    if (pending.Count >= MaximumPendingFrames)
                    {
                        throw new UnprovenLayoutException();
                    }

                    pending.Enqueue(derived, derived);
                    lastRawTimestamp = rawTime;
                }
            }

            /// <summary>Requires every original extractor line to agree with its actual block anchor, including all duplicated lace entries.</summary>
            async Task RequireRawTimestampAsync(decimal nanoseconds)
            {

                if (extracted is null)
                {
                    return;
                }

                var line = await extracted.ReadLineAsync(request.CancellationToken).ConfigureAwait(false);
                var valid = decimal.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds);

                if (!valid || Math.Abs(milliseconds * NanosecondsPerMillisecond - nanoseconds) > ExtractorRoundingNanoseconds)
                {
                    throw new UnprovenLayoutException();
                }
            }

            /// <summary>Writes one rationally derived timestamp with the extractor's nanosecond display precision.</summary>
            async Task WriteTimestampAsync(decimal nanoseconds)
            {

                if (reference is null)
                {
                    return;
                }

                await reference.WriteLineAsync((nanoseconds / NanosecondsPerMillisecond)
                    .ToString("0.######", CultureInfo.InvariantCulture)).ConfigureAwait(false);
                lastWrittenTimestamp = nanoseconds;
            }

            /// <summary>Future blocks cannot precede their monotone anchors, so only a bounded overlap window needs sorting.</summary>
            async Task FlushBeforeAsync(decimal boundary)
            {
                while (pending.TryPeek(out var timestamp, out _) && timestamp <= boundary)
                {
                    pending.Dequeue();
                    await WriteTimestampAsync(timestamp).ConfigureAwait(false);
                }
            }

            /// <summary>Reads no more than a bounded metadata/prefix budget, independently of source length.</summary>
            async Task<byte[]> ReadAsync(long offset, int length)
            {
                var count = (int)Math.Min(length, Math.Max(0, source.Length - offset));

                if (offset < 0 || count > MaximumHeaderReadBytes - readBytes)
                {
                    throw new UnprovenLayoutException();
                }

                readBytes += count;
                var bytes = new byte[count];
                var consumed = 0;

                while (consumed < count)
                {
                    var actual = await RandomAccess.ReadAsync(source.SafeFileHandle, bytes.AsMemory(consumed), offset + consumed,
                        request.CancellationToken).ConfigureAwait(false);

                    if (actual == 0)
                    {
                        throw new UnprovenLayoutException();
                    }

                    consumed += actual;
                }

                return bytes;
            }

            /// <summary>Reads only finite Matroska unsigned scalar values.</summary>
            async Task<ulong> ReadUnsignedAsync(long offset, long length)
            {

                if (length is < 1 or > 8)
                {
                    throw new UnprovenLayoutException();
                }

                var bytes = await ReadAsync(offset, (int)length).ConfigureAwait(false);
                ulong value = 0;

                foreach (var part in bytes)
                {
                    value = (value << 8) | part;
                }

                return value;
            }
        }
        catch (Exception exception) when (exception is UnprovenLayoutException or OverflowException)
        {
            return false;
        }
    }

    /// <summary>Reads bounded EBML identifiers and sizes while retaining unknown-size information.</summary>
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

    private sealed class UnprovenLayoutException : Exception;
    private sealed record TraversalRequest(string SourcePath, ulong TrackNumber, DtsCoreFrameHeader ExpectedHeader,
        CancellationToken CancellationToken, string? ExtractedTimestampPath = null, string? ReferencePath = null);
}

internal sealed record DtsLacedTimestampRequest(string SourcePath, ulong TrackNumber, DtsCoreFrameHeader ExpectedHeader,
    string ExtractedTimestampPath, string ReferencePath, CancellationToken CancellationToken);
internal sealed record DtsProfileValidation(string SourcePath, ulong TrackNumber, DtsCoreFrameHeader ExpectedHeader,
    CancellationToken CancellationToken);
