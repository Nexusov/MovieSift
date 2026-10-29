using MovieSift.Application;
using MovieSift.Core.Failures;
using MovieSift.Infrastructure.MediaTools;
using Xunit;

namespace MovieSift.Infrastructure.Tests;

/// <summary>Checks bounded Segment/Info traversal using small controlled EBML structures, not media preservation claims.</summary>
public sealed class SegmentInfoReaderTests
{
    /// <summary>Supports finite and unknown-sized Segments while ignoring family-like bytes inside a finite media payload.</summary>
    [Theory]
    [InlineData("1A45DFA38018538067851549A96680")]
    [InlineData("1A45DFA38018538067FF1549A96680")]
    [InlineData("1A45DFA380185380678D1549A966801F43B67583444480")]
    [InlineData("1A45DFA301000000000000001853806701FFFFFFFFFFFFFF1549A9660100000000000000")]
    public async Task ReadsOnlyDefinedInfoChildren(string hexadecimal)
    {
        await ValidateBytesAsync(Convert.FromHexString(hexadecimal));
    }

    /// <summary>Still examines Info beyond skipped media while ignoring linkage-like payload bytes.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PositionalHeaderReadsPreserveTheCompleteWalk(bool linked)
    {
        using var workspace = new TestWorkspace();
        const int payloadBytes = 16384;
        var payload = new byte[payloadBytes];
        Convert.FromHexString("1549A96683444480").CopyTo(payload, 0);
        byte[] document = [.. Convert.FromHexString("1A45DFA38018538067FF1F43B675204000"), .. payload,
            .. Convert.FromHexString(linked ? "1549A96683444480" : "1549A96680")];
        var path = Path.Combine(workspace.Path, "far-info.mkv");
        await File.WriteAllBytesAsync(path, document, TestContext.Current.CancellationToken);

        if (linked)
        {
            var failure = await Assert.ThrowsAsync<OperationFailureException>(() =>
                MkvSegmentInfoReader.ValidateAsync(new(path, TestContext.Current.CancellationToken)));
            Assert.Equal(ErrorReason.LinkedSegments, failure.Failure.Reason);
        }

        if (!linked)
        {
            var metrics = await MkvSegmentInfoReader.ValidateAsync(new(path, TestContext.Current.CancellationToken));
            Assert.Equal(4, metrics.ElementHeaders);
            Assert.InRange(metrics.HeaderBytesRead, 1, metrics.ElementHeaders * 12L);
        }

        Assert.Equal(document, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    /// <summary>Fails closed when the narrow reader cannot establish complete single-Segment Info boundaries.</summary>
    [Theory]
    [InlineData("1A45DFA3801853806780")]
    [InlineData("1A45DFA380185380678A1549A966801549A96680")]
    [InlineData("1A45DFA38018538067851549A966FF")]
    [InlineData("1A45DFA380185380678A1549A966801F43B675FF")]
    [InlineData("1A45DFA38018538067851549A96688")]
    [InlineData("1A45DFA38018538067851549A96600")]
    [InlineData("1A45DFA38018538067851549A966801853806780")]
    public async Task RejectsAmbiguousUnknownOrMalformedBoundaries(string hexadecimal)
    {
        var exception = await Assert.ThrowsAsync<OperationFailureException>(() => ValidateBytesAsync(Convert.FromHexString(hexadecimal)));
        Assert.Equal(ErrorCode.UnsupportedFeature, exception.Failure.Code);
        Assert.Equal(ErrorReason.SegmentStructure, exception.Failure.Reason);
    }

    /// <summary>Bounds pathological header counts without imposing an input media file-size quota.</summary>
    [Fact]
    public async Task RejectsExcessiveElementHeaders()
    {
        const int paddingElements = 100_001;
        const int emptyElementBytes = 2;
        const byte voidIdentifier = 0xEC;
        const byte emptyPayloadSize = 0x80;
        var padding = new byte[paddingElements * emptyElementBytes];

        for (var elementIndex = 0; elementIndex < paddingElements; elementIndex++)
        {
            padding[elementIndex * emptyElementBytes] = voidIdentifier;
            padding[elementIndex * emptyElementBytes + 1] = emptyPayloadSize;
        }

        byte[] document = [.. Convert.FromHexString("1A45DFA38018538067FF1549A96680"), .. padding];
        var exception = await Assert.ThrowsAsync<OperationFailureException>(() => ValidateBytesAsync(document));
        Assert.Equal(ErrorReason.SegmentStructure, exception.Failure.Reason);
        Assert.Contains("budget", exception.Failure.Detail);
    }

    /// <summary>Observes pending cancellation before opening or inspecting a source.</summary>
    [Fact]
    public async Task CancelledInspectionDoesNotOpenSource()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MkvSegmentInfoReader.ValidateAsync(new("unused-source.mkv", cancellation.Token)));
    }

    /// <summary>Writes one small isolated structure and invokes the same reader used by production identification.</summary>
    private static async Task ValidateBytesAsync(byte[] bytes)
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Path, "structure.mkv");
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
        await MkvSegmentInfoReader.ValidateAsync(new(path, cancellationToken));
    }
}
