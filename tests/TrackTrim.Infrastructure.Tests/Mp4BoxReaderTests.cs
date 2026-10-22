using System.Buffers.Binary;
using System.Text;
using TrackTrim.Application;
using TrackTrim.Core.Failures;
using TrackTrim.Infrastructure.MediaTools;
using Xunit;

namespace TrackTrim.Infrastructure.Tests;

/// <summary>Uses controlled small box documents to test the bounded capability reader, independently of codec/media acceptance.</summary>
public sealed class Mp4BoxReaderTests
{
    /// <summary>Accepts an ordinary rate-one priming edit and a single empty-delay prefix, without reading media payload bytes as boxes.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SimplePresentationEditsAreAdmitted(bool emptyDelay)
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Path, "ordinary edit.mp4");
        await File.WriteAllBytesAsync(path, Document(new(EmptyDelay: emptyDelay)), TestContext.Current.CancellationToken);
        var structure = await Mp4BoxReader.ReadAsync(new(path));
        Assert.Equal("isom", structure.MajorBrand);
        Assert.Equal(1000u, structure.MovieTimescale);
        Assert.Equal(7u, Assert.Single(structure.Tracks).Id);
    }

    /// <summary>Supports the actual ordinary FFmpeg name and forced-subtitle kind atoms rather than rejecting common title/flag metadata.</summary>
    [Fact]
    public async Task OrdinaryTrackNameAndForcedRoleAreRecognizedAtTheirSchemaPosition()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Path, "track metadata.mp4");
        var role = Combine(new byte[4], Encoding.UTF8.GetBytes("urn:mpeg:dash:role:2011\0forced-subtitle\0"));
        var metadata = Box("udta", Combine(Box("name", Encoding.UTF8.GetBytes("Actual title")), Box("kind", role)));
        await File.WriteAllBytesAsync(path, Document(new(TrackMetadata: metadata)), TestContext.Current.CancellationToken);
        Assert.Single((await Mp4BoxReader.ReadAsync(new(path))).Tracks);
    }

    /// <summary>Rejects complex edits, external media references and encryption sample entries before any tool can load them.</summary>
    [Theory]
    [InlineData("complex edit")]
    [InlineData("two normal edits")]
    [InlineData("external reference")]
    [InlineData("encrypted sample")]
    [InlineData("fragmented")]
    [InlineData("unsupported brand")]
    [InlineData("unknown metadata")]
    [InlineData("unknown kind")]
    public async Task UnsupportedCapabilityStructuresFailClosed(string feature)
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Path, "unsupported.mp4");
        var options = feature switch
        {
            "complex edit" => new DocumentOptions(ComplexEdit: true),
            "two normal edits" => new DocumentOptions(TwoNormalEdits: true),
            "external reference" => new DocumentOptions(ExternalReference: true),
            "encrypted sample" => new DocumentOptions(SampleEntry: "encv"),
            "fragmented" => new DocumentOptions(AdditionalRoot: Box("moof", [])),
            "unsupported brand" => new DocumentOptions(Brand: "qt  "),
            "unknown metadata" => new DocumentOptions(TrackMetadata: Box("udta", Box("uuid", [1, 2, 3]))),
            "unknown kind" => new DocumentOptions(TrackMetadata: Box("udta", Box("kind", Combine(new byte[4], Encoding.UTF8.GetBytes("private\0unknown\0"))))),
            _ => throw new InvalidOperationException(),
        };
        await File.WriteAllBytesAsync(path, Document(options), TestContext.Current.CancellationToken);
        var failure = Assert.ThrowsAsync<OperationFailureException>(() => Mp4BoxReader.ReadAsync(new(path)));
        Assert.Contains((await failure).Failure.Code, new[] { ErrorCode.UnsupportedFeature, ErrorCode.UnsupportedContainer });
    }

    /// <summary>Rejects truncated parent/child sizes instead of seeking beyond the containing file.</summary>
    [Fact]
    public async Task TruncatedBoxCannotEscapeItsParentBoundary()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Path, "truncated.mp4");
        var bytes = Document(new());
        await File.WriteAllBytesAsync(path, bytes[..^1], TestContext.Current.CancellationToken);
        Assert.Equal(ErrorCode.UnsupportedFeature,
            (await Assert.ThrowsAsync<OperationFailureException>(() => Mp4BoxReader.ReadAsync(new(path)))).Failure.Code);
    }

    /// <summary>Rejects an unproven size-is-aspect-ratio header mode rather than silently losing that defined flag.</summary>
    [Fact]
    public async Task UnsupportedTrackHeaderFlagsAreRejected()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Path, "unsupported track flags.mp4");
        await File.WriteAllBytesAsync(path, Document(new(TrackFlags: 11)), TestContext.Current.CancellationToken);
        Assert.Equal(ErrorCode.UnsupportedFeature,
            (await Assert.ThrowsAsync<OperationFailureException>(() => Mp4BoxReader.ReadAsync(new(path)))).Failure.Code);
    }

    /// <summary>Rejects unproven cover data types even when ffprobe could omit the unrecognized artwork entirely.</summary>
    [Theory]
    [InlineData(14u, true)]
    [InlineData(27u, false)]
    [InlineData(13u, false)]
    public async Task CoverTypeAndSignatureMustAgree(uint dataType, bool accepted)
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Path, "artwork.mp4");
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var metadata = Box("udta", Box("meta", Combine(UInt32(0), Box("ilst", Box("covr", Box("data", Combine(UInt32(dataType), UInt32(0), png)))))));
        await File.WriteAllBytesAsync(path, Document(new(MovieMetadata: metadata)), TestContext.Current.CancellationToken);

        if (!accepted)
        {
            Assert.Equal(ErrorCode.UnsupportedFeature,
                (await Assert.ThrowsAsync<OperationFailureException>(() => Mp4BoxReader.ReadAsync(new(path)))).Failure.Code);
            return;
        }

        Assert.Equal("png", Assert.Single((await Mp4BoxReader.ReadAsync(new(path))).ArtworkCodecs));
    }

    /// <summary>Reads only the bounded Nero chapter starts/titles in the pinned tool's version-zero/one representations.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NeroChapterMetadataRetainsItsActualSemantics(bool versionOne)
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Path, "Nero chapters.mp4");
        var header = Combine(UInt32(versionOne ? 0x0100_0000u : 0u), versionOne ? UInt32(0) : [], new byte[] { 2 });
        var chapters = Box("udta", Box("chpl", Combine(header, NeroChapter(0, "Начало"), NeroChapter(10_000_000, "Part two"))));
        await File.WriteAllBytesAsync(path, Document(new(MovieMetadata: chapters)), TestContext.Current.CancellationToken);
        var structure = await Mp4BoxReader.ReadAsync(new(path));
        Assert.Equal(new long[] { 0, 10_000_000 }, structure.NeroChapters.Select(chapter => chapter.StartTicks));
        Assert.Equal(new[] { "Начало", "Part two" }, structure.NeroChapters.Select(chapter => chapter.Title));
    }

    /// <summary>Refuses opaque, empty, ambiguous or malformed Nero metadata rather than assuming ffprobe saw every chapter.</summary>
    [Theory]
    [InlineData("empty")]
    [InlineData("truncated")]
    [InlineData("unsupported version")]
    [InlineData("negative start")]
    [InlineData("invalid UTF8")]
    [InlineData("reversed starts")]
    public async Task UnsupportedNeroChapterRepresentationIsRejected(string kind)
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Path, "unsupported Nero.mp4");
        var payload = kind switch
        {
            "empty" => Combine(UInt32(0), new byte[] { 0 }),
            "truncated" => Combine(UInt32(0), new byte[] { 1 }),
            "unsupported version" => Combine(UInt32(0x0200_0000), UInt32(0), new byte[] { 1 }, NeroChapter(0, "First")),
            "negative start" => Combine(UInt32(0), new byte[] { 1 }, NeroChapter(-1, "First")),
            "invalid UTF8" => Combine(UInt32(0), new byte[] { 1 }, new byte[8], new byte[] { 1, 0xFF }),
            "reversed starts" => Combine(UInt32(0), new byte[] { 2 }, NeroChapter(10_000_000, "First"), NeroChapter(0, "Second")),
            _ => throw new InvalidOperationException(),
        };
        await File.WriteAllBytesAsync(path, Document(new(MovieMetadata: Box("udta", Box("chpl", payload)))), TestContext.Current.CancellationToken);
        Assert.Equal(ErrorCode.UnsupportedFeature,
            (await Assert.ThrowsAsync<OperationFailureException>(() => Mp4BoxReader.ReadAsync(new(path)))).Failure.Code);
    }

    /// <summary>Constructs only the narrow reader's necessary capability boxes; this is not an encoded media fixture.</summary>
    private static byte[] Document(DocumentOptions options)
    {
        var movieHeader = new byte[20];
        BinaryPrimitives.WriteUInt32BigEndian(movieHeader.AsSpan(12), 1000);
        var trackHeader = new byte[24];
        BinaryPrimitives.WriteUInt32BigEndian(trackHeader.AsSpan(0), options.TrackFlags);
        BinaryPrimitives.WriteUInt32BigEndian(trackHeader.AsSpan(12), 7);
        var sampleHeader = new byte[78];
        BinaryPrimitives.WriteUInt16BigEndian(sampleHeader.AsSpan(6), 1);
        var description = Box("stsd", Combine(UInt32(0), UInt32(1), Box(options.SampleEntry, Combine(sampleHeader, Box("avcC", [1, 2, 3, 4])))));
        var reference = Box("dref", Combine(UInt32(0), UInt32(1), Box("url ", options.ExternalReference ? Combine(UInt32(0), Encoding.UTF8.GetBytes("file:///other\0")) : UInt32(1))));
        var media = Box("mdia", Combine(Box("mdhd", movieHeader), Box("minf", Combine(Box("dinf", reference), Box("stbl", description)))));
        var edits = options.ComplexEdit
            ? Combine(UInt32(0), UInt32(3), Edit(1000, 0), Edit(1000, 1000), Edit(1000, 2000))
            : options.TwoNormalEdits ? Combine(UInt32(0), UInt32(2), Edit(1000, 0), Edit(1000, 1000))
            : options.EmptyDelay ? Combine(UInt32(0), UInt32(2), Edit(100, -1), Edit(2000, 1024))
                : Combine(UInt32(0), UInt32(1), Edit(2000, 1024));
        var track = Box("trak", Combine(Box("tkhd", trackHeader), Box("edts", Box("elst", edits)), media, options.TrackMetadata ?? []));
        return Combine(Box("ftyp", Combine(Encoding.Latin1.GetBytes(options.Brand), UInt32(512), Encoding.Latin1.GetBytes("isommp41"))),
            Box("moov", Combine(Box("mvhd", movieHeader), track, options.MovieMetadata ?? [])), Box("mdat", [0, 0, 0, 0]), options.AdditionalRoot ?? []);
    }

    /// <summary>Writes one controlled version-zero edit with an explicit unity playback rate.</summary>
    private static byte[] Edit(uint duration, int mediaTime) => Combine(UInt32(duration), UInt32(unchecked((uint)mediaTime)), UInt32(0x0001_0000));

    /// <summary>Encodes one small Nero entry using its defined 100 ns start and UTF8 byte length.</summary>
    private static byte[] NeroChapter(long startTicks, string title)
    {
        var timestamp = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(timestamp, startTicks);
        var name = Encoding.UTF8.GetBytes(title);
        return Combine(timestamp, new byte[] { (byte)name.Length }, name);
    }

    /// <summary>Encodes a small big-endian integer used by the controlled box fixture.</summary>
    private static byte[] UInt32(uint value)
    {
        var bytes = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    /// <summary>Creates a finite schema-positioned test box.</summary>
    private static byte[] Box(string name, byte[] payload) => Combine(UInt32((uint)payload.Length + 8), Encoding.Latin1.GetBytes(name), payload);

    /// <summary>Combines only bounded test buffers, never real movie payloads.</summary>
    private static byte[] Combine(params byte[][] parts) => parts.SelectMany(part => part).ToArray();

    private sealed record DocumentOptions(bool EmptyDelay = false, bool ComplexEdit = false, bool TwoNormalEdits = false, bool ExternalReference = false,
        string SampleEntry = "avc1", string Brand = "isom", byte[]? AdditionalRoot = null, byte[]? TrackMetadata = null,
        byte[]? MovieMetadata = null, uint TrackFlags = 3);
}
