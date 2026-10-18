using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using TrackTrim.Application.Ports;
using TrackTrim.Core.Media;
using TrackTrim.Core.Planning;
using TrackTrim.Infrastructure.MediaTools;
using TrackTrim.Infrastructure.Processes;
using Xunit;

namespace TrackTrim.Infrastructure.Tests;

/// <summary>Exercises real MKVToolNix identification, remux, verification, and retained encoded payloads.</summary>
public sealed class MkvBackendIntegrationTests
{
    /// <summary>Proves exact retained bytes and metadata on single- and multiple-video fixtures.</summary>
    [MediaTheory]
    [InlineData("ordinary.mkv")]
    [InlineData("multi-video.mkv")]
    public async Task RemovesDefaultAudioAndAllSubtitlesPreservingExactRetainedPayloads(string fixtureName)
    {
        using var workspace = new TestWorkspace();
        var fixturePath = Path.Combine(MediaTestEnvironment.FixtureDirectory, fixtureName);
        var sourcePath = Path.Combine(workspace.Path, "-исходник ; & sample.mkv");
        File.Copy(fixturePath, sourcePath);
        var originalHash = await HashAsync(sourcePath);
        var backend = MediaTestEnvironment.CreateBackend(Path.Combine(workspace.Path, "scratch"));
        var inspection = await backend.InspectAsync(new InspectRequest(sourcePath));
        Assert.Equal(20, inspection.Backend.IdentificationFormatVersion);
        Assert.True(inspection.HasChapters);
        Assert.True(inspection.HasTags);
        Assert.Equal("Original attachment description / Описание", Assert.Single(inspection.Attachments).Description);
        var audioTracks = inspection.Tracks.Where(track => track.Kind == TrackKind.Audio).ToArray();
        Assert.Equal(audioTracks[0].Codec, audioTracks[1].Codec);
        Assert.Equal(audioTracks[0].Language, audioTracks[1].Language);
        Assert.Equal(audioTracks[0].Title, audioTracks[1].Title);
        var retained = inspection.Tracks.Where(track => track.Kind == TrackKind.Video || track.Id == audioTracks[1].Id).ToImmutableArray();
        var candidatePath = Path.Combine(workspace.Path, "новый output.mkv");
        var plan = PlanBuilder.Build(new PlanRequest(inspection, retained.Select(track => track.Id).ToArray(), candidatePath, false)).Plan!;
        var progress = new List<double>();
        var toolResult = await backend.RemuxAsync(new RemuxRequest(plan, candidatePath, new SynchronousProgress(progress.Add)));
        Assert.Equal(0, toolResult.ExitCode);
        Assert.Contains(100, progress);
        var verification = await backend.VerifyAsync(new VerifyRequest(plan, candidatePath));
        Assert.True(verification.Passed, verification.Details);
        var output = await backend.InspectAsync(new InspectRequest(candidatePath));
        Assert.DoesNotContain(output.Tracks, track => track.Kind == TrackKind.Subtitles);
        Assert.False(Assert.Single(output.Tracks.Where(track => track.Kind == TrackKind.Audio)).IsDefault);
        Assert.Equal(retained.Select(track => track.Uid), output.Tracks.Select(track => track.Uid));
        Assert.Equal(inspection.Attachments[0].Description, Assert.Single(output.Attachments).Description);

        for (var trackIndex = 0; trackIndex < retained.Length; trackIndex++)
        {
            var sourcePayload = Path.Combine(workspace.Path, $"source-{trackIndex}.payload");
            var outputPayload = Path.Combine(workspace.Path, $"output-{trackIndex}.payload");
            await ExtractAsync(new PayloadExtraction(sourcePath, "tracks", retained[trackIndex].Id, sourcePayload));
            await ExtractAsync(new PayloadExtraction(candidatePath, "tracks", output.Tracks[trackIndex].Id, outputPayload));
            Assert.Equal(await HashAsync(sourcePayload), await HashAsync(outputPayload));
        }

        var originalAttachment = Path.Combine(workspace.Path, "source-attachment.bin");
        var outputAttachment = Path.Combine(workspace.Path, "output-attachment.bin");
        await ExtractAsync(new PayloadExtraction(sourcePath, "attachments", inspection.Attachments[0].Id, originalAttachment));
        await ExtractAsync(new PayloadExtraction(candidatePath, "attachments", output.Attachments[0].Id, outputAttachment));
        Assert.Equal(await HashAsync(originalAttachment), await HashAsync(outputAttachment));
        Assert.Equal(originalHash, await HashAsync(sourcePath));
    }

    /// <summary>Checks explicit removal of all audio while preserving the supported retained content.</summary>
    [MediaFact]
    public async Task ConfirmedZeroAudioRetainsVideoSubtitlesChaptersAndTags()
    {
        using var workspace = new TestWorkspace();
        var sourcePath = Path.Combine(MediaTestEnvironment.FixtureDirectory, "ordinary.mkv");
        var originalHash = await HashAsync(sourcePath);
        var backend = MediaTestEnvironment.CreateBackend(Path.Combine(workspace.Path, "scratch"));
        var inspection = await backend.InspectAsync(new InspectRequest(sourcePath));
        var retainedIds = inspection.Tracks.Where(track => track.Kind != TrackKind.Audio).Select(track => track.Id).ToArray();
        var candidate = Path.Combine(workspace.Path, "silent.mkv");
        var plan = PlanBuilder.Build(new PlanRequest(inspection, retainedIds, candidate, true)).Plan!;
        var result = await backend.RemuxAsync(new RemuxRequest(plan, candidate, null));
        Assert.Equal(0, result.ExitCode);
        var verification = await backend.VerifyAsync(new VerifyRequest(plan, candidate));
        Assert.True(verification.Passed, verification.Details);
        var output = await backend.InspectAsync(new InspectRequest(candidate));
        Assert.DoesNotContain(output.Tracks, track => track.Kind == TrackKind.Audio);
        Assert.Single(output.Tracks.Where(track => track.Kind == TrackKind.Subtitles));
        Assert.Equal(originalHash, await HashAsync(sourcePath));
    }

    /// <summary>Rejects a wrong soundtrack even when codec, language, and title match.</summary>
    [MediaFact]
    public async Task WrongIdenticalMetadataAudioFailsExplicitIdentityMapping()
    {
        using var workspace = new TestWorkspace();
        var sourcePath = Path.Combine(MediaTestEnvironment.FixtureDirectory, "ordinary.mkv");
        var originalHash = await HashAsync(sourcePath);
        var backend = MediaTestEnvironment.CreateBackend(Path.Combine(workspace.Path, "scratch"));
        var inspection = await backend.InspectAsync(new InspectRequest(sourcePath));
        var audioTracks = inspection.Tracks.Where(track => track.Kind == TrackKind.Audio).ToArray();
        var candidatePath = Path.Combine(workspace.Path, "wrong-audio.mkv");
        var intendedIds = inspection.Tracks.Where(track => track.Kind == TrackKind.Video || track.Id == audioTracks[1].Id)
            .Select(track => track.Id).ToArray();
        var wrongIds = inspection.Tracks.Where(track => track.Kind == TrackKind.Video || track.Id == audioTracks[0].Id)
            .Select(track => track.Id).ToArray();
        var intended = PlanBuilder.Build(new PlanRequest(inspection, intendedIds, candidatePath, false)).Plan!;
        var wrong = PlanBuilder.Build(new PlanRequest(inspection, wrongIds, candidatePath, false)).Plan!;
        Assert.Equal(0, (await backend.RemuxAsync(new RemuxRequest(wrong, candidatePath, null))).ExitCode);
        var verification = await backend.VerifyAsync(new VerifyRequest(intended, candidatePath));
        Assert.False(verification.Passed);
        Assert.Contains("identity", verification.Details, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalHash, await HashAsync(sourcePath));
    }

    /// <summary>Cancels after actual machine progress and confirms the source bytes remain intact.</summary>
    [MediaFact]
    public async Task CancellationDuringRealWorkerOutputReapsWorkerAndPreservesOriginal()
    {
        using var workspace = new TestWorkspace();
        var sourcePath = Path.Combine(MediaTestEnvironment.FixtureDirectory, "ordinary.mkv");
        var originalHash = await HashAsync(sourcePath);
        var backend = MediaTestEnvironment.CreateBackend(Path.Combine(workspace.Path, "scratch"));
        var inspection = await backend.InspectAsync(new InspectRequest(sourcePath));
        var candidate = Path.Combine(workspace.Path, "cancel-during-work.mkv");
        var retainedIds = inspection.Tracks.Where(track => track.Kind != TrackKind.Subtitles).Select(track => track.Id).ToArray();
        var plan = PlanBuilder.Build(new PlanRequest(inspection, retainedIds, candidate, false)).Plan!;
        using var cancellationSource = new CancellationTokenSource();
        var progressObserved = false;
        var progress = new SynchronousProgress(_ =>
        {
            progressObserved = true;
            cancellationSource.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.RemuxAsync(
            new RemuxRequest(plan, candidate, progress, cancellationSource.Token)));
        Assert.True(progressObserved);
        Assert.Equal(originalHash, await HashAsync(sourcePath));
    }

    /// <summary>Checks that cancellation before launch produces no candidate and leaves the source intact.</summary>
    [MediaFact]
    public async Task PreCancelledRealRemuxDoesNotCreateCandidateOrChangeSource()
    {
        using var workspace = new TestWorkspace();
        var sourcePath = Path.Combine(MediaTestEnvironment.FixtureDirectory, "ordinary.mkv");
        var originalHash = await HashAsync(sourcePath);
        var backend = MediaTestEnvironment.CreateBackend(Path.Combine(workspace.Path, "scratch"));
        var inspection = await backend.InspectAsync(new InspectRequest(sourcePath));
        var candidate = Path.Combine(workspace.Path, "cancelled.mkv");
        var retainedIds = inspection.Tracks.Where(track => track.Kind != TrackKind.Subtitles).Select(track => track.Id).ToArray();
        var plan = PlanBuilder.Build(new PlanRequest(inspection, retainedIds, candidate, false)).Plan!;
        using var cancellationSource = new CancellationTokenSource();
        await cancellationSource.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.RemuxAsync(
            new RemuxRequest(plan, candidate, null, cancellationSource.Token)));
        Assert.False(File.Exists(candidate));
        Assert.Equal(originalHash, await HashAsync(sourcePath));
    }

    /// <summary>Extracts retained encoded streams or attachment bytes into trusted application-owned names.</summary>
    private static async Task ExtractAsync(PayloadExtraction request)
    {
        var toolPaths = MkvToolPaths.FromDirectory(MediaTestEnvironment.ToolDirectory);
        var environment = toolPaths.NativeLibraryDirectory is { } nativeLibraryDirectory
            ? new Dictionary<string, string> { ["LD_LIBRARY_PATH"] = nativeLibraryDirectory } : null;
        var result = await new OwnedProcessRunner().RunAsync(new ProcessRequest(toolPaths.MkvExtract,
            [request.SourcePath, request.Mode, $"{request.Identifier.ToString(CultureInfo.InvariantCulture)}:{request.DestinationPath}"],
            32768, TimeSpan.FromMinutes(2), null, CancellationToken.None, environment));
        Assert.Equal(0, result.ExitCode);
    }

    /// <summary>Hashes the actual file bytes without loading a media fixture into memory.</summary>
    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private sealed record PayloadExtraction(string SourcePath, string Mode, int Identifier, string DestinationPath);

    /// <summary>Delivers worker progress synchronously for deterministic protocol assertions.</summary>
    private sealed class SynchronousProgress(Action<double> callback) : IProgress<double>
    {
        /// <summary>Forwards the actual worker value directly to the deterministic test observer.</summary>
        public void Report(double value)
        {
            callback(value);
        }
    }
}

/// <summary>Owns one isolated test directory and deletes only its own generated outputs.</summary>
internal sealed class TestWorkspace : IDisposable
{
    internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TrackTrim tests", Guid.NewGuid().ToString("N"));

    /// <summary>Allocates a private directory for this test's generated outputs.</summary>
    internal TestWorkspace()
    {
        Directory.CreateDirectory(Path);
    }

    /// <summary>Removes only this test's uniquely owned directory.</summary>
    public void Dispose()
    {
        Directory.Delete(Path, recursive: true);
    }
}
