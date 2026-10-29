using System.Buffers.Binary;
using MovieSift.Infrastructure.MediaTools;
using Xunit;

namespace MovieSift.Infrastructure.Tests;

/// <summary>Checks reconstruction only for codec-proven fixed DTS laces, preserving every original block anchor and frame count.</summary>
public sealed class DtsLacedTimestampEvidenceTests
{
    private static readonly DtsCoreFrameHeader Header = new(48000, 512, 24, 6, 96);

    /// <summary>Combines signed offsets, cluster timecodes, a nondefault scale, laced and unlaced target blocks.</summary>
    [Fact]
    public async Task DerivesOnlyIntraLaceTimesAndPreservesBlockAnchors()
    {
        using var workspace = new TestWorkspace();
        var first = Cluster(100, Simple(-2, 4, Frame(), Frame()));
        var second = Cluster(200, Simple(5, 0, Frame()));
        var bytes = Segment(first.Concat(second).ToArray(), 2_000_000);
        var request = await PrepareAsync(workspace, bytes, "196\n196\n410\n410\n");

        Assert.True(await DtsLacedTimestampEvidence.WriteReferenceAsync(request));
        Assert.Equal(["# timestamp format v2", "196", "206.666667", "410", "410"],
            await File.ReadAllLinesAsync(request.ReferencePath, TestContext.Current.CancellationToken));
    }

    /// <summary>Changed anchors and missing/extra extractor frames cannot be normalized away.</summary>
    [Theory]
    [InlineData("0\n1\n0\n")]
    [InlineData("0\n0\n")]
    [InlineData("0\n0\n0\n0\n")]
    public async Task RejectsInconsistentExtractedEvidence(string raw)
    {
        using var workspace = new TestWorkspace();
        var request = await PrepareAsync(workspace, Segment(Cluster(0, Simple(0, 4, Frame(), Frame()))), raw);
        Assert.False(await DtsLacedTimestampEvidence.WriteReferenceAsync(request));
    }

    /// <summary>Every laced core frame must match the independently proven source/candidate parameters.</summary>
    [Fact]
    public async Task RejectsDifferentLaterCoreFrame()
    {
        using var workspace = new TestWorkspace();
        var altered = Frame();
        SetBits(altered, 39, 7, 31);
        var request = await PrepareAsync(workspace, Segment(Cluster(0, Simple(0, 4, Frame(), altered))), "0\n0\n0\n");
        Assert.False(await DtsLacedTimestampEvidence.WriteReferenceAsync(request));
    }

    /// <summary>Valid per-frame PCM precision changes do not alter a proven core frame's sample count or timing.</summary>
    [Fact]
    public async Task AllowsPrecisionChangeWithoutRelaxingTimingEvidence()
    {
        using var workspace = new TestWorkspace();
        var altered = Frame();
        SetBits(altered, 95, 3, 0);
        var request = await PrepareAsync(workspace, Segment(Cluster(0, Simple(0, 4, Frame(), altered))), "0\n0\n0\n");
        Assert.True(await DtsLacedTimestampEvidence.WriteReferenceAsync(request));
        Assert.Equal(["# timestamp format v2", "0", "10.666667", "10.666667"],
            await File.ReadAllLinesAsync(request.ReferencePath, TestContext.Current.CancellationToken));
    }

    /// <summary>Unproven group durations, other lace encodings and missing lace ambiguity retain the strict original comparison.</summary>
    [Theory]
    [InlineData("group")]
    [InlineData("xiph")]
    [InlineData("none")]
    public async Task RejectsUnsupportedLayouts(string layout)
    {
        using var workspace = new TestWorkspace();
        var block = layout switch
        {
            "group" => Element([0xa0], Element([0xa1], BlockBody(0, 4, Frame(), Frame()))),
            "xiph" => Simple(0, 2, Frame(), Frame()),
            _ => Simple(0, 0, Frame())
        };
        var request = await PrepareAsync(workspace, Segment(Cluster(0, block)), "0\n0\n0\n");
        Assert.False(await DtsLacedTimestampEvidence.WriteReferenceAsync(request));
    }

    /// <summary>Reference creation cannot overwrite an unrelated file, and cancellation never opens the source.</summary>
    [Fact]
    public async Task PreservesExistingReferenceAndHonorsCancellation()
    {
        using var workspace = new TestWorkspace();
        var request = await PrepareAsync(workspace, Segment(Cluster(0, Simple(0, 4, Frame(), Frame()))), "0\n0\n0\n");
        await File.WriteAllTextAsync(request.ReferencePath, "sentinel", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => DtsLacedTimestampEvidence.WriteReferenceAsync(request));
        Assert.Equal("sentinel", await File.ReadAllTextAsync(request.ReferencePath, TestContext.Current.CancellationToken));
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DtsLacedTimestampEvidence.WriteReferenceAsync(
            request with { SourcePath = Path.Combine(workspace.Path, "absent.mkv"), CancellationToken = cancelled.Token }));
    }

    /// <summary>Writes only task-owned controlled metadata/prefix bytes and the extractor's separate terminal line.</summary>
    private static async Task<DtsLacedTimestampRequest> PrepareAsync(TestWorkspace workspace, byte[] source, string raw)
    {
        var sourcePath = Path.Combine(workspace.Path, "controlled source.mkv");
        var extracted = Path.Combine(workspace.Path, "extracted.txt");
        var reference = Path.Combine(workspace.Path, "reference.txt");
        await File.WriteAllBytesAsync(sourcePath, source, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(extracted, "# timestamp format v2\n" + raw, TestContext.Current.CancellationToken);
        return new(sourcePath, 7, Header, extracted, reference, TestContext.Current.CancellationToken);
    }

    /// <summary>Creates a finite Segment containing a documented timestamp scale and controlled cluster metadata.</summary>
    private static byte[] Segment(byte[] clusters, ulong scale = 1_000_000)
    {
        return Element([0x18, 0x53, 0x80, 0x67], Element([0x15, 0x49, 0xa9, 0x66],
            Element([0x2a, 0xd7, 0xb1], Unsigned(scale))).Concat(clusters).ToArray());
    }

    /// <summary>Creates a finite cluster with its actual timestamp before target blocks.</summary>
    private static byte[] Cluster(ulong timestamp, byte[] blocks) => Element([0x1f, 0x43, 0xb6, 0x75],
        Element([0xe7], Unsigned(timestamp)).Concat(blocks).ToArray());

    /// <summary>Wraps a target SimpleBlock with controlled lacing.</summary>
    private static byte[] Simple(short relative, byte flags, params byte[][] frames) => Element([0xa3], BlockBody(relative, flags, frames));

    /// <summary>Combines block timestamps and actual frame boundaries; malformed Xiph encoding is intentionally unsupported.</summary>
    private static byte[] BlockBody(short relative, byte flags, params byte[][] frames)
    {
        var header = new byte[] { 0x87, 0, 0, flags };
        BinaryPrimitives.WriteInt16BigEndian(header.AsSpan(1, 2), relative);
        return header.Concat(flags == 0 ? [] : new byte[] { (byte)(frames.Length - 1) }).Concat(frames.SelectMany(frame => frame)).ToArray();
    }

    /// <summary>Builds a minimal synthetic core prefix without claiming it is an encoded media fixture.</summary>
    private static byte[] Frame()
    {
        var bytes = new byte[Header.FrameBytes];
        new byte[] { 0x7f, 0xfe, 0x80, 1 }.CopyTo(bytes, 0);
        SetBits(bytes, 32, 1, 1);
        SetBits(bytes, 33, 5, 31);
        SetBits(bytes, 39, 7, 15);
        SetBits(bytes, 46, 14, Header.FrameBytes - 1);
        SetBits(bytes, 60, 6, 9);
        SetBits(bytes, 66, 4, 13);
        SetBits(bytes, 85, 2, 1);
        SetBits(bytes, 95, 3, 6);
        return bytes;
    }

    /// <summary>Writes one network-order codec field for a deterministic prefix test.</summary>
    private static void SetBits(byte[] bytes, int offset, int count, int value)
    {
        for (var index = 0; index < count; index++)
        {
            var position = offset + index;
            var mask = 1 << (7 - position % 8);
            bytes[position / 8] = (byte)((bytes[position / 8] & ~mask) | (((value >> (count - index - 1)) & 1) == 0 ? 0 : mask));
        }
    }

    /// <summary>Encodes finite test element sizes without depending on movie payloads.</summary>
    private static byte[] Element(byte[] identifier, byte[] payload) => identifier.Concat(new byte[]
    {
        (byte)(0x10 | payload.Length >> 24), (byte)(payload.Length >> 16), (byte)(payload.Length >> 8), (byte)payload.Length
    }).Concat(payload).ToArray();

    /// <summary>Encodes a small Matroska unsigned scalar in network order.</summary>
    private static byte[] Unsigned(ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        var first = Array.FindIndex(bytes, part => part != 0);
        return first < 0 ? [0] : bytes[first..];
    }
}
