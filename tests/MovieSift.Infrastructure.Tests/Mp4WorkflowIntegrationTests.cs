using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MovieSift.Application;
using MovieSift.Application.Jobs;
using MovieSift.Application.Ports;
using MovieSift.Core.Failures;
using MovieSift.Core.Media;
using MovieSift.Core.Planning;
using MovieSift.Infrastructure.Files;
using MovieSift.Infrastructure.MediaTools;
using MovieSift.Infrastructure.Processes;
using Xunit;

namespace MovieSift.Infrastructure.Tests;

/// <summary>Checks the admitted MP4 preservation matrix with actual pinned workers and encoded packet evidence.</summary>
public sealed class Mp4WorkflowIntegrationTests(ITestOutputHelper evidence)
{
    private const int MaximumDiagnosticsCharacters = 32768;
    private static readonly TimeSpan FixtureOperationTimeout = TimeSpan.FromMinutes(2);
    /// <summary>Removes all subtitles and the only default audio without promoting either retained non-default audio.</summary>
    [MediaTheory]
    [InlineData("ordinary.mp4")]
    [InlineData("jpeg-artwork.mp4")]
    public async Task RemovesDefaultAudioAndAllSubtitlesWhilePreservingBothOtherAudioAndArtwork(string fixtureName)
    {
        using var workspace = new TestWorkspace();
        var source = CopyFixture(workspace.Path, fixtureName);
        var sourceHash = await HashAsync(source);
        using var workers = new ObservedMediaWorkers();
        var backend = CreateBackend(workers.Observe);
        var inspected = await backend.InspectAsync(new(source, TestContext.Current.CancellationToken));
        Assert.Equal(MediaContainer.Mp4, inspected.Container);
        Assert.True(inspected.HasChapters);
        var audio = inspected.Tracks.Where(track => track.Kind == TrackKind.Audio).ToArray();
        Assert.Equal(3, audio.Length);
        Assert.Single(audio, track => track.IsDefault);
        Assert.All(audio, track => Assert.Equal("eng", track.Language));
        var ids = inspected.Tracks.Where(track => track.Kind == TrackKind.Video ||
            track.Kind == TrackKind.Audio && !track.IsDefault).Select(track => track.Id).ToImmutableArray();
        var destination = Path.Combine(workspace.Path, "результат без default и субтитров.mp4");
        var plan = Assert.IsType<RemuxPlan>(PlanBuilder.Build(new(inspected, ids, destination, false)).Plan);
        Assert.True(plan.RemovesDefaultAudio);
        Assert.False(plan.HasDefaultAudio);
        var result = await CreateCoordinator(workspace.Path, backend).RunAsync(new(plan, CancellationToken: TestContext.Current.CancellationToken));
        AssertCompleted(result);
        var output = await backend.InspectAsync(new(destination, TestContext.Current.CancellationToken));
        Assert.Empty(output.Tracks.Where(track => track.Kind == TrackKind.Subtitles));
        Assert.All(output.Tracks.Where(track => track.Kind == TrackKind.Audio), track => Assert.False(track.IsDefault));
        Assert.Equal(inspected.ContainerProperties, output.ContainerProperties);
        var payloads = await CompareAllAsync(plan, output);
        var audioPayloads = output.Tracks.Select((track, index) => (track, index)).Where(value => value.track.Kind == TrackKind.Audio)
            .Select(value => payloads[value.index].Sha256).ToArray();
        Assert.NotEqual(audioPayloads[0], audioPayloads[1]);
        Assert.Equal(sourceHash, await HashAsync(source));
        workers.AssertSequentialAndExited(1);
        AssertClean(workspace.Path);
        evidence.WriteLine(JsonSerializer.Serialize(new
        {
            kind = "real-mp4-default-removal",
            sourceSha256 = sourceHash,
            originalUnchanged = true,
            retained = plan.RetainedTracks.Select(track => new { sourceIndex = track.Id, track.Kind, track.Title, track.IsDefault }),
            payloads,
            result.Verification,
            chapters = output.HasChapters,
            artworkRetained = output.Tracks.Count(track => track.Properties.GetValueOrDefault("attached_pic") == "1"),
            workersExited = true,
            ownedCleanupEmpty = true,
        }));
    }

    /// <summary>Preserves both ordinary video streams and proves that no-change never starts a media worker.</summary>
    [MediaFact]
    public async Task MultipleVideosAreAlwaysKeptAndNoChangeDoesNotRemux()
    {
        using var workspace = new TestWorkspace();
        var source = CopyFixture(workspace.Path, "multi-video.mp4");
        var sourceHash = await HashAsync(source);
        using var workers = new ObservedMediaWorkers();
        var backend = CreateBackend(workers.Observe);
        var inspected = await backend.InspectAsync(new(source, TestContext.Current.CancellationToken));
        Assert.Equal(2, inspected.Tracks.Count(track => track.Kind == TrackKind.Video));
        var jobs = CreateCoordinator(workspace.Path, backend);
        var unchanged = Assert.IsType<RemuxPlan>(PlanBuilder.Build(new(inspected,
            inspected.Tracks.Select(track => track.Id).ToArray(), Path.Combine(workspace.Path, "unchanged.mp4"), false)).Plan);
        Assert.Equal(JobPhase.NoChange, (await jobs.RunAsync(new(unchanged))).Phase);
        Assert.Empty(workers.Identifiers);
        Assert.False(File.Exists(unchanged.DestinationPath));
        var ids = inspected.Tracks.Where(track => track.Kind != TrackKind.Subtitles).Select(track => track.Id).ToArray();
        var plan = Assert.IsType<RemuxPlan>(PlanBuilder.Build(new(inspected, ids,
            Path.Combine(workspace.Path, "both videos.mp4"), false)).Plan);
        var result = await jobs.RunAsync(new(plan, CancellationToken: TestContext.Current.CancellationToken));
        AssertCompleted(result);
        var output = await backend.InspectAsync(new(plan.DestinationPath, TestContext.Current.CancellationToken));
        Assert.Equal(2, output.Tracks.Count(track => track.Kind == TrackKind.Video));
        await CompareAllAsync(plan, output);
        Assert.Equal(sourceHash, await HashAsync(source));
        workers.AssertSequentialAndExited(1);
        AssertClean(workspace.Path);
    }

    /// <summary>Requires explicit zero-audio consent and preserves H.264 packets when all audio/subtitles are removed.</summary>
    [MediaFact]
    public async Task ZeroAudioRequiresConsentAndPublishesVideoAndArtworkOnly()
    {
        using var workspace = new TestWorkspace();
        var source = CopyFixture(workspace.Path, "ordinary.mp4");
        var originalHash = await HashAsync(source);
        var backend = CreateBackend();
        var inspected = await backend.InspectAsync(new(source, TestContext.Current.CancellationToken));
        var ids = inspected.Tracks.Where(track => track.Kind == TrackKind.Video).Select(track => track.Id).ToArray();
        var destination = Path.Combine(workspace.Path, "explicit silent.mp4");
        Assert.Equal(ErrorCode.ZeroAudioConfirmationRequired, PlanBuilder.Build(new(inspected, ids, destination, false)).Failure?.Code);
        var plan = Assert.IsType<RemuxPlan>(PlanBuilder.Build(new(inspected, ids, destination, true)).Plan);
        AssertCompleted(await CreateCoordinator(workspace.Path, backend).RunAsync(new(plan, CancellationToken: TestContext.Current.CancellationToken)));
        var output = await backend.InspectAsync(new(destination, TestContext.Current.CancellationToken));
        Assert.All(output.Tracks, track => Assert.Equal(TrackKind.Video, track.Kind));
        await CompareAllAsync(plan, output);
        Assert.Equal(originalHash, await HashAsync(source));
        AssertClean(workspace.Path);
    }

    /// <summary>Admits ordinary AAC encoder priming and simple presentation edits with B-frame video.</summary>
    [MediaFact]
    public async Task AacPrimingAndBFramePresentationTimelineSurviveSubtitleRemoval()
    {
        using var workspace = new TestWorkspace();
        var source = CopyFixture(workspace.Path, "priming-bframes.mp4");
        var originalHash = await HashAsync(source);
        var backend = CreateBackend();
        var inspected = await backend.InspectAsync(new(source, TestContext.Current.CancellationToken));
        Assert.Contains(inspected.Tracks, track => track.Kind == TrackKind.Video && track.Properties["has_b_frames"] == "2");
        var plan = Assert.IsType<RemuxPlan>(PlanBuilder.Build(new(inspected,
            inspected.Tracks.Where(track => track.Kind != TrackKind.Subtitles).Select(track => track.Id).ToArray(),
            Path.Combine(workspace.Path, "presentation timeline.mp4"), false)).Plan);
        var result = await CreateCoordinator(workspace.Path, backend).RunAsync(new(plan, CancellationToken: TestContext.Current.CancellationToken));
        AssertCompleted(result);
        var output = await backend.InspectAsync(new(plan.DestinationPath, TestContext.Current.CancellationToken));
        await CompareAllAsync(plan, output);
        Assert.Equal(originalHash, await HashAsync(source));
        AssertClean(workspace.Path);
        evidence.WriteLine($"Real AAC priming/B-frame/simple-edit verification: {result.Verification!.Details}");
    }

    /// <summary>Refuses actual fragmented media before it can acquire output ownership.</summary>
    [MediaFact]
    public async Task FragmentedInputFailsInspectionWithoutWritingAnything()
    {
        using var workspace = new TestWorkspace();
        var source = CopyFixture(workspace.Path, "fragmented.mp4");
        var originalHash = await HashAsync(source);
        var failure = await Assert.ThrowsAsync<OperationFailureException>(() =>
            CreateBackend().InspectAsync(new(source, TestContext.Current.CancellationToken)));
        Assert.Equal(ErrorCode.UnsupportedFeature, failure.Failure.Code);
        Assert.Equal(originalHash, await HashAsync(source));
        Assert.Equal([source], Directory.EnumerateFiles(workspace.Path));
    }

    /// <summary>Proves that per-job packet verification rejects the wrong same-codec/language audio despite matching copied metadata.</summary>
    [MediaFact]
    public async Task VerificationRejectsWrongEncodedAudioEvenWhenOutputMetadataMatches()
    {
        using var workspace = new TestWorkspace();
        var source = CopyFixture(workspace.Path, "ordinary.mp4");
        var sourceHash = await HashAsync(source);
        var backend = CreateBackend();
        var inspection = await backend.InspectAsync(new(source, TestContext.Current.CancellationToken));
        var expectedAudio = inspection.Tracks.Single(track => track.Title == "Alternate audio");
        var wrongAudio = inspection.Tracks.Single(track => track.Title == "Commentary audio");
        Assert.Equal(expectedAudio.Codec, wrongAudio.Codec);
        Assert.Equal(expectedAudio.Language, wrongAudio.Language);
        var plan = Assert.IsType<RemuxPlan>(PlanBuilder.Build(new(inspection,
            inspection.Tracks.Where(track => track.Kind == TrackKind.Video || track.Id == expectedAudio.Id).Select(track => track.Id).ToArray(),
            Path.Combine(workspace.Path, "must not publish.mp4"), false)).Plan);
        var store = new OwnedOutputStore(Path.Combine(workspace.Path, "data"));
        var output = await store.ReserveAsync(new(plan));
        var arguments = FfmpegArguments.BuildRemux(new(plan, output.CandidatePath, null)).ToArray();

        for (var index = 0; index < arguments.Length - 1; index++)
        {
            if (arguments[index] == "-map" && arguments[index + 1] == $"0:{expectedAudio.Id}")
            {
                arguments[index + 1] = $"0:{wrongAudio.Id}";
            }
        }

        // Only the encoded input map is deliberately wrong. Explicit expected metadata/dispositions remain exact.
        var tool = await new OwnedProcessRunner().RunAsync(new(FfmpegToolPaths.FromDirectory(MediaTestEnvironment.ToolDirectory).Ffmpeg,
            arguments, MaximumDiagnosticsCharacters, FixtureOperationTimeout, null, TestContext.Current.CancellationToken));
        Assert.Equal(0, tool.ExitCode);
        var candidate = await backend.InspectAsync(new(output.CandidatePath, TestContext.Current.CancellationToken));
        await Mp4CandidateFlags.RestoreAsync(new(inspection.Source, plan.RetainedTracks, candidate, TestContext.Current.CancellationToken));
        var verification = await backend.VerifyAsync(new(plan, output.CandidatePath, TestContext.Current.CancellationToken));
        Assert.False(verification.Passed);
        Assert.Contains("packet payloads", verification.Details, StringComparison.Ordinal);
        Assert.False(File.Exists(plan.DestinationPath));
        Assert.Empty(await store.CleanupAsync(new(output)));
        Assert.Equal(sourceHash, await HashAsync(source));
        AssertClean(workspace.Path);
    }

    /// <summary>Retains a destination created after reservation and reports publication conflict after a real verified remux.</summary>
    [MediaFact]
    public async Task CollisionAppearingDuringRealRemuxPreservesSentinelSourceAndOwnedCleanup()
    {
        using var workspace = new TestWorkspace();
        using var workers = new ObservedMediaWorkers();
        var source = CopyFixture(workspace.Path, "ordinary.mp4");
        var sourceHash = await HashAsync(source);
        var destination = Path.Combine(workspace.Path, "occupied output.mp4");
        byte[] sentinel = [11, 29, 47, 83];
        var backend = CreateBackend(identifier =>
        {
            workers.Observe(identifier);
            using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            stream.Write(sentinel);
        });
        var inspection = await backend.InspectAsync(new(source, TestContext.Current.CancellationToken));
        var plan = Assert.IsType<RemuxPlan>(PlanBuilder.Build(new(inspection,
            inspection.Tracks.Where(track => track.Kind != TrackKind.Subtitles).Select(track => track.Id).ToArray(), destination, false)).Plan);
        var result = await CreateCoordinator(workspace.Path, backend).RunAsync(new(plan, CancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(JobPhase.Failed, result.Phase);
        Assert.Equal(ErrorCode.DestinationExists, result.Failure?.Code);
        Assert.True(result.Verification!.Passed, result.Verification.Details);
        Assert.Null(result.PublishedPath);
        Assert.Empty(result.CleanupIssues);
        Assert.Equal(sentinel, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        Assert.Equal(sourceHash, await HashAsync(source));
        workers.AssertSequentialAndExited(1);
        AssertClean(workspace.Path);
    }

    /// <summary>Runs an actual FFmpeg failure without treating exit one as a publishable warning or touching the source.</summary>
    [MediaFact]
    public async Task RealFfmpegFailureRetainsDiagnosticsAndDoesNotProduceMedia()
    {
        using var workspace = new TestWorkspace();
        using var workers = new ObservedMediaWorkers();
        var source = CopyFixture(workspace.Path, "ordinary.mp4");
        var sourceHash = await HashAsync(source);
        var backend = CreateBackend(workers.Observe);
        var inspection = await backend.InspectAsync(new(source, TestContext.Current.CancellationToken));
        var plan = Assert.IsType<RemuxPlan>(PlanBuilder.Build(new(inspection,
            inspection.Tracks.Where(track => track.Kind != TrackKind.Subtitles).Select(track => track.Id).ToArray(),
            Path.Combine(workspace.Path, "not published.mp4"), false)).Plan);
        var missingParent = Path.Combine(workspace.Path, "absent directory", "candidate.mp4");
        var outcome = await backend.RemuxAsync(new(plan, missingParent, null, TestContext.Current.CancellationToken));
        Assert.NotEqual(0, outcome.ExitCode);
        Assert.Equal(ToolDisposition.Failure, outcome.EffectiveDisposition);
        Assert.NotEmpty(outcome.Diagnostics);
        Assert.False(File.Exists(missingParent));
        Assert.False(File.Exists(plan.DestinationPath));
        Assert.Equal(sourceHash, await HashAsync(source));
        workers.AssertSequentialAndExited(1);
    }

    /// <summary>Cancels an observed live FFmpeg process at the production start boundary, then verifies reap and owned cleanup.</summary>
    [MediaFact]
    public async Task RealFfmpegCancellationReapsTheRetainedWorkerAndLeavesTheSourceUnchanged()
    {
        using var workspace = new TestWorkspace();
        using var cancellation = new CancellationTokenSource();
        var source = CopyFixture(workspace.Path, "ordinary.mp4");
        var sourceHash = await HashAsync(source);
        Process? worker = null;
        var liveAtCancellation = false;
        var backend = CreateBackend(identifier =>
        {
            worker = Process.GetProcessById(identifier);
            _ = worker.SafeHandle;
            liveAtCancellation = !worker.HasExited;
            cancellation.Cancel();
        });

        try
        {
            var inspected = await backend.InspectAsync(new(source, TestContext.Current.CancellationToken));
            var plan = Assert.IsType<RemuxPlan>(PlanBuilder.Build(new(inspected,
                inspected.Tracks.Where(track => track.Kind != TrackKind.Subtitles).Select(track => track.Id).ToArray(),
                Path.Combine(workspace.Path, "cancelled output.mp4"), false)).Plan);
            var result = await CreateCoordinator(workspace.Path, backend).RunAsync(new(plan, CancellationToken: cancellation.Token));
            evidence.WriteLine($"Cancellation result: {result.Phase}; failure: {result.Failure}; cleanup: {string.Join("; ", result.CleanupIssues)}");
            Assert.True(liveAtCancellation, "Cancellation must be requested while the retained owned worker is still active.");
            Assert.Equal(JobPhase.Cancelled, result.Phase);
            Assert.True(worker!.HasExited);
            Assert.Empty(result.CleanupIssues);
            Assert.Null(result.PublishedPath);
            Assert.False(File.Exists(plan.DestinationPath));
            Assert.Equal(sourceHash, await HashAsync(source));
            AssertClean(workspace.Path);
            evidence.WriteLine("Real FFmpeg started and retained live handle observed before token cancellation; same worker exited; no output/candidate/journal; source SHA256 unchanged. This is not native GUI input evidence.");
        }
        finally
        {
            worker?.Dispose();
        }
    }

    /// <summary>Copies only original task-owned fixtures into an isolated Unicode path.</summary>
    private static string CopyFixture(string workspace, string name)
    {
        var source = Path.Combine(workspace, "исходный " + name);
        File.Copy(Path.Combine(MediaTestEnvironment.FixtureDirectory, "mp4", name), source);
        return source;
    }

    /// <summary>Constructs the actual adapter with explicit pinned tools and an optional read-only process observer.</summary>
    private static FfmpegBackend CreateBackend(Action<int>? started = null) => new(
        new FfmpegBackendOptions(new FileSourceAccess(), FfmpegToolPaths.FromDirectory(MediaTestEnvironment.ToolDirectory))
        { RemuxProcessStarted = started });

    /// <summary>Uses the existing single lifecycle for every format-specific assertion.</summary>
    private static JobCoordinator CreateCoordinator(string workspace, FfmpegBackend backend) =>
        new(backend, new FileSourceAccess(), new OwnedOutputStore(Path.Combine(workspace, "data")));

    /// <summary>Requires publication, verification and cleanup rather than relying on a tool's exit status.</summary>
    private static void AssertCompleted(JobResult result)
    {
        Assert.True(result.Phase == JobPhase.Completed, $"{result.Phase}: {result.Failure}; {result.Tool}; {result.Verification}");
        Assert.NotNull(result.PublishedPath);
        Assert.True(result.Verification!.Passed, result.Verification.Details);
        Assert.Empty(result.CleanupIssues);
    }

    /// <summary>Checks every retained encoded stream with its explicit source-to-output index mapping.</summary>
    private static async Task<PacketPayloadEvidence[]> CompareAllAsync(RemuxPlan plan, MediaInspection output)
    {
        Assert.Equal(plan.RetainedTracks.Length, output.Tracks.Length);
        var payloads = new List<PacketPayloadEvidence>();

        for (var index = 0; index < output.Tracks.Length; index++)
        {
            payloads.Add(await Mp4PayloadEvidence.CompareAsync(new(plan.Inspection.Source.FullPath,
                plan.RetainedTracks[index].Id, plan.DestinationPath, output.Tracks[index].Id)));
        }

        return payloads.ToArray();
    }

    /// <summary>Checks all owned storage after awaited work without deleting final media or ordinary directories.</summary>
    private static void AssertClean(string workspace)
    {
        var dataDirectory = Path.Combine(workspace, "data");
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(dataDirectory, "ownership")));
        var scratch = Path.Combine(dataDirectory, "inspection-scratch");

        if (Directory.Exists(scratch))
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
        }

        Assert.DoesNotContain(Directory.EnumerateDirectories(workspace),
            path => Path.GetFileName(path).StartsWith(".moviesift-", StringComparison.Ordinal));
    }

    /// <summary>Hashes complete read-only source/container bytes independently of the backend evidence.</summary>
    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, TestContext.Current.CancellationToken));
    }
}
