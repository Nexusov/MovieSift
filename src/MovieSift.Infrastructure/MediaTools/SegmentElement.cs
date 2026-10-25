namespace MovieSift.Infrastructure.MediaTools;

internal sealed record SegmentElement(uint Identifier, long EndOffset, bool HasUnknownSize);
