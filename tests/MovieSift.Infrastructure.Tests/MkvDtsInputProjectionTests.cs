using System.Collections.Immutable;
using MovieSift.Application;
using MovieSift.Application.Ports;
using MovieSift.Core.Failures;
using MovieSift.Core.Media;
using MovieSift.Core.Planning;
using MovieSift.Infrastructure.Files;
using MovieSift.Infrastructure.MediaTools;
using MovieSift.Infrastructure.Processes;
using Xunit;

namespace MovieSift.Infrastructure.Tests;

/// <summary>Checks the owned preparation boundary with controlled prefixes and a controlled native-runner outcome.</summary>
public sealed class MkvDtsInputProjectionTests
{
    /// <summary>Copies only into the reserved inode and invokes duration-only native editing while leaving the original intact.</summary>
    [Fact]
    public async Task PreparesOnlyOwnedCopyWithExactDurationEdit()
    {
        using var workspace = new TestWorkspace();
        var fixture = await PrepareAsync(workspace);
        var calls = new List<ProcessRequest>();
        var progress = new InlineProgress();
        var helper = new MkvDtsInputProjection(new(fixture.Access, fixture.Editor, request =>
        {
            calls.Add(request);
            return Task.FromResult(new ProcessResult(0, request.Arguments.Contains("--version") ? "mkvpropedit v102.0 test" : "", "", false));
        }));
        Assert.True(MkvDtsInputProjection.RequiresProjection(fixture.Request.Plan));
        Assert.False(MkvDtsInputProjection.RequiresProjection(fixture.Request.Plan with { RetainedTracks = fixture.Request.Plan.Inspection.Tracks }));
        await helper.PrepareAsync(fixture.Request with { Progress = progress });
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Request.Plan.Inspection.Source.FullPath, TestContext.Current.CancellationToken));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Request.InputProjection.CreatedFile.FullPath, TestContext.Current.CancellationToken));
        var after = await fixture.Access.ReadRevisionAsync(new(fixture.Request.InputProjection.CreatedFile.FullPath, TestContext.Current.CancellationToken));
        Assert.Equal(fixture.Request.InputProjection.CreatedFile.FileIdentity, after.FileIdentity);
        Assert.Equal(2, calls.Count);
        Assert.Equal(["--output-charset", "UTF-8", after.FullPath, "--edit", "track:=123", "--set", "default-duration=10666667"], calls[1].Arguments);
        Assert.Equal(100, progress.Values.Last());
    }

    /// <summary>A native warning is not silently accepted as preparation success and never changes the original.</summary>
    [Fact]
    public async Task RejectsNativeWarningAndPreservesOriginal()
    {
        using var workspace = new TestWorkspace();
        var fixture = await PrepareAsync(workspace);
        var helper = new MkvDtsInputProjection(new(fixture.Access, fixture.Editor, request =>
            Task.FromResult(request.Arguments.Contains("--version") ? new ProcessResult(0, "mkvpropedit v102.0 test", "", false) :
                new ProcessResult(1, "controlled warning", "", false))));
        var failure = await Assert.ThrowsAsync<OperationFailureException>(() => helper.PrepareAsync(fixture.Request));
        Assert.Equal(ErrorCode.ToolFailed, failure.Failure.Code);
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Request.Plan.Inspection.Source.FullPath, TestContext.Current.CancellationToken));
    }

    /// <summary>An existing reserved-file payload is preserved rather than truncated or reused as an input projection.</summary>
    [Fact]
    public async Task RejectsNonemptyReservedCopyBeforeNativeWork()
    {
        using var workspace = new TestWorkspace();
        var fixture = await PrepareAsync(workspace);
        await File.WriteAllTextAsync(fixture.Request.InputProjection.CreatedFile.FullPath, "sentinel", TestContext.Current.CancellationToken);
        var calls = 0;
        var helper = new MkvDtsInputProjection(new(fixture.Access, fixture.Editor, _ =>
        {
            calls++;
            return Task.FromResult(new ProcessResult(0, "", "", false));
        }));
        var failure = await Assert.ThrowsAsync<OperationFailureException>(() => helper.PrepareAsync(fixture.Request));
        Assert.Equal(ErrorCode.OwnershipInvalid, failure.Failure.Code);
        Assert.Equal(0, calls);
        Assert.Equal("sentinel", await File.ReadAllTextAsync(fixture.Request.InputProjection.CreatedFile.FullPath, TestContext.Current.CancellationToken));
    }

    /// <summary>Changed sources and cancellation cannot reach copying or native editing.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsStaleSourceOrCancellation(bool cancel)
    {
        using var workspace = new TestWorkspace();
        var fixture = await PrepareAsync(workspace);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var calls = 0;
        var helper = new MkvDtsInputProjection(new(fixture.Access, fixture.Editor, _ =>
        {
            calls++;
            return Task.FromResult(new ProcessResult(0, "", "", false));
        }));

        if (cancel)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => helper.PrepareAsync(fixture.Request with { CancellationToken = cancellation.Token }));
        }

        if (!cancel)
        {
            await File.AppendAllTextAsync(fixture.Request.Plan.Inspection.Source.FullPath, "changed", TestContext.Current.CancellationToken);
            var failure = await Assert.ThrowsAsync<OperationFailureException>(() => helper.PrepareAsync(fixture.Request));
            Assert.Equal(ErrorCode.SourceChanged, failure.Failure.Code);
        }

        Assert.Equal(0, calls);
        Assert.Empty(await File.ReadAllBytesAsync(fixture.Request.InputProjection.CreatedFile.FullPath, TestContext.Current.CancellationToken));
    }

    /// <summary>Creates only controlled container/header bytes, with a distinct retained track and an actually reserved regular copy.</summary>
    private static async Task<Fixture> PrepareAsync(TestWorkspace workspace)
    {
        var sourcePath = Path.Combine(workspace.Path, "original.mkv");
        var projectionPath = Path.Combine(workspace.Path, "owned-input.mkv");
        var editorPath = Path.Combine(workspace.Path, "mkvpropedit.exe");
        var frame = new byte[96];
        new byte[] { 0x7f, 0xfe, 0x80, 1 }.CopyTo(frame, 0);
        SetBits(frame, 32, 1, 1);
        SetBits(frame, 33, 5, 31);
        SetBits(frame, 39, 7, 15);
        SetBits(frame, 46, 14, 95);
        SetBits(frame, 60, 6, 9);
        SetBits(frame, 66, 4, 13);
        SetBits(frame, 85, 2, 1);
        SetBits(frame, 95, 3, 6);
        var block = Element([0xa3], new byte[] { 0x87, 0, 0, 0 }.Concat(frame).ToArray());
        var bytes = Element([0x18, 0x53, 0x80, 0x67], Element([0x1f, 0x43, 0xb6, 0x75], Element([0xe7], [0]).Concat(block).ToArray()));
        await File.WriteAllBytesAsync(sourcePath, bytes, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(projectionPath, [], TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(editorPath, "controlled runner only", TestContext.Current.CancellationToken);
        var access = new FileSourceAccess();
        var source = await access.ReadRevisionAsync(new(sourcePath, TestContext.Current.CancellationToken));
        var projection = await access.ReadRevisionAsync(new(projectionPath, TestContext.Current.CancellationToken));
        var properties = ImmutableDictionary<string, string>.Empty.Add("codec_id", "A_DTS").Add("number", "7")
            .Add("audio_sampling_frequency", "48000").Add(DtsDefaultDurationPolicy.EvidenceKey, "48000:512:24:6:96");
        var track = new MediaTrack(6, 123, TrackKind.Audio, "DTS", "eng", null, "Original", 6, false, false, true, properties);
        var removed = track with { Id = 1, Uid = 124, Properties = properties.SetItem("codec_id", "A_AAC") };
        var inspection = new MediaInspection(source, new("MKVToolNix", "102.0", 20), [track, removed], [], "", false, false);
        var plan = new RemuxPlan(Guid.NewGuid(), inspection, [track], Path.Combine(workspace.Path, "output.mkv"), false);
        return new(new(plan, new(projection, source), null, TestContext.Current.CancellationToken), access, editorPath, bytes);
    }

    /// <summary>Writes finite test element lengths independently of actual media payloads.</summary>
    private static byte[] Element(byte[] identifier, byte[] payload) => identifier.Concat(new byte[]
    {
        (byte)(0x10 | payload.Length >> 24), (byte)(payload.Length >> 16), (byte)(payload.Length >> 8), (byte)payload.Length
    }).Concat(payload).ToArray();

    /// <summary>Writes a deterministic network-order field in the controlled DTS prefix.</summary>
    private static void SetBits(byte[] bytes, int offset, int count, int value)
    {
        for (var index = 0; index < count; index++)
        {
            var position = offset + index;
            var mask = 1 << (7 - position % 8);
            bytes[position / 8] = (byte)((bytes[position / 8] & ~mask) | (((value >> (count - index - 1)) & 1) == 0 ? 0 : mask));
        }
    }

    private sealed record Fixture(PrepareInputRequest Request, FileSourceAccess Access, string Editor, byte[] Bytes);

    /// <summary>Delivers synchronous deterministic progress observations without a UI synchronization context.</summary>
    private sealed class InlineProgress : IProgress<double>
    {
        internal List<double> Values { get; } = [];
        public void Report(double value) => Values.Add(value);
    }
}
