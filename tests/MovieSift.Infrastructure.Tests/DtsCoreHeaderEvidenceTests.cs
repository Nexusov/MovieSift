using MovieSift.Infrastructure.MediaTools;
using Xunit;

namespace MovieSift.Infrastructure.Tests;

/// <summary>Checks bounded codec-header evidence independently of any container-reported timing default.</summary>
public sealed class DtsCoreHeaderEvidenceTests
{
    /// <summary>Reads actual core fields in either admitted byte order, including optional CRC placement.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ParsesCoreHeaderFields(bool littleEndian, bool crc)
    {
        var prefix = Header(crc);

        if (littleEndian)
        {
            for (var offset = 0; offset < prefix.Length; offset += 2)
            {
                (prefix[offset], prefix[offset + 1]) = (prefix[offset + 1], prefix[offset]);
            }
        }

        var actual = Assert.IsType<DtsCoreFrameHeader>(DtsCoreHeaderEvidence.Parse(prefix));
        Assert.Equal(48000, actual.SamplingFrequency);
        Assert.Equal(512, actual.SamplesPerFrame);
        Assert.Equal(24, actual.BitsPerSample);
        Assert.Equal(6, actual.Channels);
        Assert.Equal(1024, actual.FrameBytes);
    }

    /// <summary>Unproven encodings, truncated frames and termination frames cannot authorize a metadata exception.</summary>
    [Theory]
    [InlineData(0, 8, 0)]
    [InlineData(32, 1, 0)]
    [InlineData(66, 4, 0)]
    [InlineData(95, 3, 4)]
    public void RejectsUnprovenHeader(int offset, int count, int value)
    {
        var prefix = Header(false);
        SetBits(prefix, offset, count, value);
        Assert.Null(DtsCoreHeaderEvidence.Parse(prefix));
        Assert.Null(DtsCoreHeaderEvidence.Parse(prefix.AsSpan(0, 12)));
    }

    /// <summary>Finds target-track evidence through no/fixed/Xiph/EBML lacing and ignores the other track.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(4, false)]
    [InlineData(6, false)]
    [InlineData(0, true)]
    public async Task FindsTargetInBoundedMatroskaBlock(int lacing, bool grouped)
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Path, "header evidence.mkv");
        var frame = new byte[1024];
        Header(false).CopyTo(frame, 0);
        var frames = lacing == 0 ? frame : frame.Concat(frame).ToArray();
        byte[] laceHeader = lacing switch
        {
            2 => [1, 255, 255, 255, 255, 4],
            4 => [1],
            6 => [1, 0x44, 0x00],
            _ => []
        };
        var body = new byte[] { 0x87, 0, 0, (byte)lacing }.Concat(laceHeader).Concat(frames).ToArray();
        var block = grouped ? Element([0xa0], Element([0xa1], body)) : Element([0xa3], body);
        var cluster = Element([0x1f, 0x43, 0xb6, 0x75], block);
        await File.WriteAllBytesAsync(path, Element([0x18, 0x53, 0x80, 0x67], cluster), TestContext.Current.CancellationToken);
        var found = await DtsCoreHeaderEvidence.ReadAsync(new(path, new HashSet<ulong> { 7, 8 }, TestContext.Current.CancellationToken));
        Assert.Equal(512, Assert.Single(found).Value.SamplesPerFrame);
        Assert.True(found.ContainsKey(7));
        Assert.False(found.ContainsKey(8));
    }

    /// <summary>Ignored elements outside the actual media hierarchy cannot authorize derived metadata exceptions.</summary>
    [Theory]
    [InlineData("root-simple")]
    [InlineData("root-block")]
    [InlineData("root-cluster")]
    [InlineData("segment-simple")]
    [InlineData("segment-block")]
    [InlineData("cluster-block")]
    [InlineData("group-simple")]
    [InlineData("nested-segment")]
    public async Task RejectsEvidenceFromMisplacedBlocks(string placement)
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Path, "misplaced header evidence.mkv");
        var frame = new byte[1024];
        Header(false).CopyTo(frame, 0);
        var body = new byte[] { 0x87, 0, 0, 0 }.Concat(frame).ToArray();
        var simple = Element([0xa3], body);
        var block = Element([0xa1], body);
        byte[] payload = placement switch
        {
            "root-simple" => simple,
            "root-block" => block,
            "root-cluster" => Element([0x1f, 0x43, 0xb6, 0x75], simple),
            "segment-simple" => Element([0x18, 0x53, 0x80, 0x67], simple),
            "segment-block" => Element([0x18, 0x53, 0x80, 0x67], block),
            "cluster-block" => Element([0x18, 0x53, 0x80, 0x67], Element([0x1f, 0x43, 0xb6, 0x75], block)),
            "group-simple" => Element([0x18, 0x53, 0x80, 0x67], Element([0x1f, 0x43, 0xb6, 0x75], Element([0xa0], simple))),
            "nested-segment" => Element([0x18, 0x53, 0x80, 0x67], Element([0x1f, 0x43, 0xb6, 0x75],
                Element([0x18, 0x53, 0x80, 0x67], Element([0x1f, 0x43, 0xb6, 0x75], simple)))),
            _ => throw new ArgumentOutOfRangeException(nameof(placement))
        };
        await File.WriteAllBytesAsync(path, payload, TestContext.Current.CancellationToken);
        Assert.Empty(await DtsCoreHeaderEvidence.ReadAsync(new(path, new HashSet<ulong> { 7 }, TestContext.Current.CancellationToken)));
    }

    /// <summary>A cancelled probe does not even open a source, and malformed lengths cannot expose bytes outside an element.</summary>
    [Fact]
    public async Task RejectsCancellationAndTruncatedElement()
    {
        using var workspace = new TestWorkspace();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var path = Path.Combine(workspace.Path, "not-created.mkv");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DtsCoreHeaderEvidence.ReadAsync(new(path, new HashSet<ulong> { 7 }, cancellation.Token)));
        await File.WriteAllBytesAsync(path, [0x18, 0x53, 0x80, 0x67, 0x44, 0x00, 0xa3, 0x44, 0x00], TestContext.Current.CancellationToken);
        Assert.Empty(await DtsCoreHeaderEvidence.ReadAsync(new(path, new HashSet<ulong> { 7 }, TestContext.Current.CancellationToken)));
    }

    /// <summary>A later recognizable frame must not substitute for unsupported evidence in the first target block.</summary>
    [Fact]
    public async Task DoesNotUseLaterBlockToProveFirstFrame()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Path, "unsupported first frame.mkv");
        var firstFrame = new byte[1024];
        var laterFrame = new byte[1024];
        Header(false).CopyTo(laterFrame, 0);
        var blockHeader = new byte[] { 0x87, 0, 0, 0 };
        var firstBlock = Element([0xa3], blockHeader.Concat(firstFrame).ToArray());
        var laterBlock = Element([0xa3], blockHeader.Concat(laterFrame).ToArray());
        var cluster = Element([0x1f, 0x43, 0xb6, 0x75], firstBlock.Concat(laterBlock).ToArray());
        await File.WriteAllBytesAsync(path, Element([0x18, 0x53, 0x80, 0x67], cluster), TestContext.Current.CancellationToken);
        Assert.Empty(await DtsCoreHeaderEvidence.ReadAsync(new(path, new HashSet<ulong> { 7 }, TestContext.Current.CancellationToken)));
    }

    /// <summary>Builds only a synthetic codec prefix; media integration uses separately generated actual encoded frames.</summary>
    private static byte[] Header(bool crc)
    {
        var prefix = new byte[16];
        new byte[] { 0x7f, 0xfe, 0x80, 1 }.CopyTo(prefix, 0);
        SetBits(prefix, 32, 1, 1);
        SetBits(prefix, 33, 5, 31);
        SetBits(prefix, 38, 1, crc ? 1 : 0);
        SetBits(prefix, 39, 7, 15);
        SetBits(prefix, 46, 14, 1023);
        SetBits(prefix, 60, 6, 9);
        SetBits(prefix, 66, 4, 13);
        SetBits(prefix, 85, 2, 1);
        SetBits(prefix, crc ? 111 : 95, 3, 6);
        return prefix;
    }

    /// <summary>Writes a named network-order field for small deterministic parser cases.</summary>
    private static void SetBits(byte[] bytes, int offset, int count, int value)
    {
        for (var index = 0; index < count; index++)
        {
            var position = offset + index;
            var mask = 1 << (7 - position % 8);
            bytes[position / 8] = (byte)((bytes[position / 8] & ~mask) | (((value >> (count - index - 1)) & 1) == 0 ? 0 : mask));
        }
    }

    /// <summary>Wraps controlled bytes in a two-byte finite EBML size for the probe's hierarchy tests.</summary>
    private static byte[] Element(byte[] identifier, byte[] payload)
    {
        return identifier.Concat(new byte[] { (byte)(0x40 | payload.Length >> 8), (byte)payload.Length }).Concat(payload).ToArray();
    }
}
