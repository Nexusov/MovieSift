using System.Security.Cryptography;
using TrackTrim.Application.Jobs;
using TrackTrim.Application.Ports;
using TrackTrim.Core.Media;
using TrackTrim.Core.Planning;
using TrackTrim.Infrastructure.Files;
using TrackTrim.Infrastructure.MediaTools;
using Xunit;

namespace TrackTrim.Infrastructure.Tests;

/// <summary>Protects known MP4 header presentation flags that the pinned muxer otherwise resets despite retaining encoded packets.</summary>
public sealed class Mp4HeaderFlagIntegrationTests
{
    /// <summary>Retains the source preview/movie/enabled bits, then rejects a separate candidate that loses only its preview bit.</summary>
    [MediaFact]
    public async Task PreservesKnownPreviewFlagAndRejectsItsLossDuringVerification()
    {
        using var workspace = new TestWorkspace();
        using var workers = new ObservedMediaWorkers();
        var cancellationToken = TestContext.Current.CancellationToken;
        var source = Path.Combine(workspace.Path, "исходник preview.mp4");
        var fixture = Path.Combine(MediaTestEnvironment.FixtureDirectory, "mp4", "ordinary.mp4");
        var originalFixtureHash = await HashAsync(fixture);
        File.Copy(fixture, source);
        await SetVideoFlagsAsync(source, 7);
        var sourceHash = await HashAsync(source);
        var access = new FileSourceAccess();
        var backend = new FfmpegBackend(new FfmpegBackendOptions(access,
            FfmpegToolPaths.FromDirectory(MediaTestEnvironment.ToolDirectory))
        { RemuxProcessStarted = workers.Observe });
        var inspection = await backend.InspectAsync(new(source, cancellationToken));
        var destination = Path.Combine(workspace.Path, "сохранённый preview.mp4");
        var retained = inspection.Tracks.Where(track => track.Kind != TrackKind.Subtitles).Select(track => track.Id).ToArray();
        var plan = Assert.IsType<RemuxPlan>(PlanBuilder.Build(new(inspection, retained, destination, false)).Plan);
        var store = new OwnedOutputStore(Path.Combine(workspace.Path, "data"));
        var result = await new JobCoordinator(backend, access, store).RunAsync(new(plan, CancellationToken: cancellationToken));
        Assert.True(result.Phase == JobPhase.Completed, $"{result.Phase}: {result.Failure}; {result.Verification}");
        var outputStructure = await Mp4BoxReader.ReadAsync(new SourceRequest(destination, cancellationToken));
        Assert.Equal(7u, Assert.Single(outputStructure.Tracks.Where(track => track.SampleEntry == "avc1")).Flags);
        var sourceVideo = Assert.Single(plan.RetainedTracks.Where(track => track.Kind == TrackKind.Video && track.Codec == "h264"));
        Assert.Equal("7", sourceVideo.Properties["tkhd_flags"]);
        var output = await backend.InspectAsync(new(destination, cancellationToken));
        var outputVideo = Assert.Single(output.Tracks.Where(track => track.Kind == TrackKind.Video && track.Codec == "h264"));
        await Mp4PayloadEvidence.CompareAsync(new(source, sourceVideo.Id, destination, outputVideo.Id));

        var outputHash = await HashAsync(destination);
        var tampered = Path.Combine(workspace.Path, "separate candidate without preview.mp4");
        File.Copy(destination, tampered);
        await SetVideoFlagsAsync(tampered, 3);
        Assert.False((await backend.VerifyAsync(new(plan, tampered, cancellationToken))).Passed);
        Assert.Equal(outputHash, await HashAsync(destination));
        Assert.Equal(sourceHash, await HashAsync(source));
        Assert.Equal(originalFixtureHash, await HashAsync(fixture));
        Assert.Empty(result.CleanupIssues);
        Assert.Empty(await store.FindInterruptedAsync(cancellationToken));
        workers.AssertSequentialAndExited(1);
        Assert.DoesNotContain(Directory.EnumerateDirectories(workspace.Path),
            path => Path.GetFileName(path).StartsWith(".tracktrim-", StringComparison.Ordinal));
    }

    /// <summary>Changes only a test-owned copy's first AVC track flags at the actual reader-identified header position.</summary>
    private static async Task SetVideoFlagsAsync(string path, byte flags)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var structure = await Mp4BoxReader.ReadAsync(new SourceRequest(path, cancellationToken));
        var header = Assert.Single(structure.Tracks.Where(track => track.SampleEntry == "avc1"));
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.Position = header.FlagsOffset;
        await stream.WriteAsync(new byte[] { 0, 0, flags }, cancellationToken);
    }

    /// <summary>Hashes complete fixture and output bytes independently of the production metadata comparison.</summary>
    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, TestContext.Current.CancellationToken));
    }
}
