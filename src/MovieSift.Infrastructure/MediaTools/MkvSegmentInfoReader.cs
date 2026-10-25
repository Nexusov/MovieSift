using MovieSift.Application;
using MovieSift.Application.Ports;
using MovieSift.Core.Failures;
using MovieSift.Infrastructure.Files;

namespace MovieSift.Infrastructure.MediaTools;

/// <summary>Checks only Segment/Info linkage omitted by identification JSON, seeking past media payloads.</summary>
internal sealed class MkvSegmentInfoReader
{
    private const uint EbmlIdentifier = 0x1A45DFA3;
    private const uint SegmentIdentifier = 0x18538067;
    private const uint InfoIdentifier = 0x1549A966;
    private const uint FamilyIdentifier = 0x4444;
    private const uint PreviousIdentifier = 0x3CB923;
    private const uint NextIdentifier = 0x3EB923;
    private const uint VoidIdentifier = 0xEC;
    private const uint CrcIdentifier = 0xBF;
    private const int MaximumIdentifierBytes = 4;
    private const int MaximumSizeBytes = 8;
    private const int MaximumElementHeaders = 100_000;
    private const int BitsPerByte = 8;
    private const int DataBitsPerSizeByte = 7;
    private const int FirstSizeMarker = 0x80;
    private readonly FileStream stream;
    private readonly CancellationToken cancellationToken;
    private readonly byte[] buffer = new byte[MaximumSizeBytes];
    private int headerCount;

    /// <summary>Owns one bounded inspection cursor without interpreting tracks, clusters or encoded data.</summary>
    private MkvSegmentInfoReader(SourceScan request)
    {
        stream = request.Stream;
        cancellationToken = request.CancellationToken;
    }

    /// <summary>Rejects unsupported linkage or a layout whose absence of linkage cannot be established safely.</summary>
    internal static async Task ValidateAsync(SourceRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        await using var stream = FileSourceAccess.OpenRegularReadStream(request.Path);
        var reader = new MkvSegmentInfoReader(new(stream, request.CancellationToken));

        try
        {
            await reader.ValidateDocumentAsync().ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            throw UnsupportedStructure("The Segment/Info element header is truncated.");
        }
    }

    /// <summary>Requires a single bounded EBML document and inspects every top-level Info element.</summary>
    private async Task ValidateDocumentAsync()
    {
        var header = await ReadElementAsync(stream.Length).ConfigureAwait(false);

        if (header.Identifier != EbmlIdentifier || header.HasUnknownSize)
        {
            throw UnsupportedStructure("A bounded Matroska EBML header is required.");
        }

        stream.Position = header.EndOffset;
        var segment = await ReadElementAsync(stream.Length).ConfigureAwait(false);

        while (IsPadding(segment.Identifier))
        {
            SkipBounded(segment);
            segment = await ReadElementAsync(stream.Length).ConfigureAwait(false);
        }

        if (segment.Identifier != SegmentIdentifier)
        {
            throw UnsupportedStructure("A single Matroska Segment is required.");
        }

        var hasInfo = false;

        while (stream.Position < segment.EndOffset)
        {
            var element = await ReadElementAsync(segment.EndOffset).ConfigureAwait(false);
            var invalidInfo = element.Identifier == InfoIdentifier && (hasInfo || element.HasUnknownSize);

            if (invalidInfo)
            {
                throw UnsupportedStructure("Exactly one bounded Segment Info element is required.");
            }

            if (element.Identifier == InfoIdentifier)
            {
                await ValidateInfoAsync(element.EndOffset).ConfigureAwait(false);
                hasInfo = true;
                continue;
            }

            if (element.Identifier is SegmentIdentifier or EbmlIdentifier)
            {
                throw UnsupportedStructure("Multiple or nested Matroska documents are unsupported.");
            }

            SkipBounded(element);
        }

        if (!hasInfo)
        {
            throw UnsupportedStructure("The Segment Info element could not be located.");
        }

        while (stream.Position < stream.Length)
        {
            var trailing = await ReadElementAsync(stream.Length).ConfigureAwait(false);

            if (!IsPadding(trailing.Identifier))
            {
                throw UnsupportedStructure("Trailing Matroska documents or unknown data are unsupported.");
            }

            SkipBounded(trailing);
        }
    }

    /// <summary>Detects real linkage element IDs at their defined Info level, never by searching arbitrary bytes.</summary>
    private async Task ValidateInfoAsync(long endOffset)
    {
        while (stream.Position < endOffset)
        {
            var element = await ReadElementAsync(endOffset).ConfigureAwait(false);

            if (element.Identifier is FamilyIdentifier or PreviousIdentifier or NextIdentifier)
            {
                throw new OperationFailureException(new(ErrorCode.UnsupportedFeature,
                    "Detected SegmentFamily or previous/next segment linkage in Segment Info; choose a standalone MKV.",
                    ErrorReason.LinkedSegments));
            }

            SkipBounded(element);
        }
    }

    /// <summary>Reads at most twelve bytes per element and validates all lengths against the containing boundary.</summary>
    private async Task<SegmentElement> ReadElementAsync(long parentEnd)
    {
        cancellationToken.ThrowIfCancellationRequested();
        headerCount++;

        if (headerCount > MaximumElementHeaders)
        {
            throw UnsupportedStructure("The bounded Segment/Info element-header budget was exceeded.");
        }

        var identifier = await ReadVariableIntegerAsync(new(parentEnd, MaximumIdentifierBytes, true)).ConfigureAwait(false);
        var size = await ReadVariableIntegerAsync(new(parentEnd, MaximumSizeBytes, false)).ConfigureAwait(false);
        var dataOffset = stream.Position;

        if (!size.IsUnknown && size.Value > (ulong)(parentEnd - dataOffset))
        {
            throw UnsupportedStructure("An element extends beyond its containing element or the file.");
        }

        var endOffset = size.IsUnknown ? parentEnd : dataOffset + (long)size.Value;
        return new((uint)identifier.Value, endOffset, size.IsUnknown);
    }

    /// <summary>Decodes only the bounded EBML ID/size prefixes needed to seek between Segment/Info elements.</summary>
    private async Task<VariableInteger> ReadVariableIntegerAsync(IntegerRequest request)
    {
        if (stream.Position >= request.EndOffset)
        {
            throw UnsupportedStructure("An element header exceeds its parent boundary.");
        }

        await stream.ReadExactlyAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        var marker = FirstSizeMarker;
        var length = 1;

        while ((buffer[0] & marker) == 0 && length <= request.MaximumBytes)
        {
            marker >>= 1;
            length++;
        }

        if (length > request.MaximumBytes || stream.Position + length - 1 > request.EndOffset)
        {
            throw UnsupportedStructure("An EBML ID or size has an invalid or unsupported length.");
        }

        await stream.ReadExactlyAsync(buffer.AsMemory(1, length - 1), cancellationToken).ConfigureAwait(false);
        ulong value = request.IsIdentifier ? buffer[0] : (byte)(buffer[0] & (marker - 1));

        for (var byteIndex = 1; byteIndex < length; byteIndex++)
        {
            value = (value << BitsPerByte) | buffer[byteIndex];
        }

        var unknownSize = !request.IsIdentifier && value == (1UL << (DataBitsPerSizeByte * length)) - 1;
        return new(value, unknownSize);
    }

    /// <summary>Skips payloads only when a finite trustworthy boundary is available.</summary>
    private void SkipBounded(SegmentElement element)
    {
        if (element.HasUnknownSize)
        {
            throw UnsupportedStructure("An unknown-sized element prevents bounded Segment Info inspection; streaming clusters are unsupported.");
        }

        stream.Position = element.EndOffset;
    }

    /// <summary>Allows only schema-defined global padding outside the single Segment.</summary>
    private static bool IsPadding(uint identifier) => identifier is VoidIdentifier or CrcIdentifier;

    /// <summary>Explains an unsupported inspection layout without claiming that an uninspected file is safe.</summary>
    private static OperationFailureException UnsupportedStructure(string detail) =>
        new(new(ErrorCode.UnsupportedFeature, detail, ErrorReason.SegmentStructure));

    private sealed record SourceScan(FileStream Stream, CancellationToken CancellationToken);
    private sealed record IntegerRequest(long EndOffset, int MaximumBytes, bool IsIdentifier);
    private sealed record VariableInteger(ulong Value, bool IsUnknown);
}
