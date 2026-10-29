using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using MovieSift.Application.Inspection;
using MovieSift.Application.Jobs;
using MovieSift.Application.Ports;
using MovieSift.Core.Media;
using MovieSift.Core.Planning;
using MovieSift.Infrastructure.Files;
using MovieSift.Infrastructure.MediaTools;
using MovieSift.Infrastructure.Processes;
using Xunit;

namespace MovieSift.Infrastructure.Tests;

/// <summary>Exercises real MKVToolNix identification, remux, verification, and retained encoded payloads.</summary>
public sealed class MkvBackendIntegrationTests
{
    /// <summary>Exposes the structural scan separately and records bounded timings without adding packet extraction.</summary>
    [MediaFact]
    public async Task InspectionReportsItsRealBoundariesAndStageTimings()
    {
        using var workspace = new TestWorkspace();
        var stages = new List<InspectionStage>();
        var diagnostics = new List<string>();
        var backend = new MkvBackend(new(new FileSourceAccess(),
            MkvToolPaths.FromDirectory(MediaTestEnvironment.ToolDirectory) with { IsDevelopmentOverride = true },
            Path.Combine(workspace.Path, "scratch"), diagnostics.Add));
        var fixture = Path.Combine(MediaTestEnvironment.FixtureDirectory, "ordinary.mkv");

        var result = await backend.InspectAsync(new(fixture, TestContext.Current.CancellationToken,
            new ImmediateInspectionProgress(stages)));

        Assert.NotEmpty(result.Tracks);
        Assert.Equal([InspectionStage.CheckingTools, InspectionStage.ReadingContainer,
            InspectionStage.CheckingStructure, InspectionStage.ReadingMetadata], stages);
        Assert.Equal(4, diagnostics.Count);
        Assert.All(diagnostics, message => Assert.Matches("^Inspection timing: (tools|identification|structure|metadata) [0-9]+ ms$", message));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(workspace.Path, "scratch")));
    }

    /// <summary>Records backend observations synchronously without introducing UI scheduling into media tests.</summary>
    private sealed class ImmediateInspectionProgress(List<InspectionStage> stages) : IProgress<InspectionStage>
    {
        public void Report(InspectionStage value) => stages.Add(value);
    }

    /// <summary>Replaces one original through the real job owner and verifies published media against the untouched fixture.</summary>
    [MediaFact]
    public async Task ReplaceSourcePreservesRetainedMediaAndCleansOwnedBackupAndJournal()
    {
        using var workspace = new TestWorkspace();
        var fixturePath = Path.Combine(MediaTestEnvironment.FixtureDirectory, "ordinary.mkv");
        var sourceDirectory = Path.Combine(workspace.Path, "source files");
        Directory.CreateDirectory(sourceDirectory);
        var sourcePath = Path.Combine(sourceDirectory, "-исходник ; & replace.mkv");
        File.Copy(fixturePath, sourcePath);
        var originalHash = await HashAsync(sourcePath);
        Assert.Equal(originalHash, await HashAsync(fixturePath));
        var scratch = Path.Combine(workspace.Path, "scratch");
        var backend = MediaTestEnvironment.CreateBackend(scratch);
        var inspection = await backend.InspectAsync(new(sourcePath, TestContext.Current.CancellationToken));
        Assert.True(inspection.HasChapters);
        Assert.True(inspection.HasTags);
        var audioTracks = inspection.Tracks.Where(track => track.Kind == TrackKind.Audio).ToArray();
        Assert.Equal(2, audioTracks.Length);
        Assert.Contains(inspection.Tracks, track => track.Kind == TrackKind.Subtitles);
        var retained = inspection.Tracks.Where(track => track.Kind == TrackKind.Video || track.Id == audioTracks[1].Id).ToArray();
        var retainedIds = retained.Select(track => track.Id).ToArray();
        var plan = Assert.IsType<RemuxPlan>(PlanBuilder.Build(new(inspection, retainedIds, sourcePath, false,
            OutputMode: OutputMode.ReplaceSource)).Plan);
        var data = Path.Combine(workspace.Path, "data");
        var coordinator = new JobCoordinator(backend, new FileSourceAccess(), new OwnedOutputStore(data));

        var result = await coordinator.RunAsync(new(plan, CancellationToken: TestContext.Current.CancellationToken));

        Assert.True(result.Phase == JobPhase.Completed, $"{result.Phase}: {result.Failure}; {result.Tool}; {result.Verification}");
        Assert.Null(result.Failure);
        Assert.Empty(result.CleanupIssues);
        Assert.Equal(0, result.Tool?.ExitCode);
        var verification = Assert.IsType<VerificationOutcome>(result.Verification);
        Assert.True(verification.Passed, verification.Details);
        Assert.Equal(sourcePath, result.PublishedPath);
        Assert.Equal(new FileInfo(sourcePath).Length, result.OutputLength);
        Assert.NotEqual(originalHash, await HashAsync(sourcePath));
        var output = await backend.InspectAsync(new(sourcePath, TestContext.Current.CancellationToken));
        Assert.Equal(retained.Select(track => track.Uid), output.Tracks.Select(track => track.Uid));
        Assert.DoesNotContain(output.Tracks, track => track.Kind == TrackKind.Subtitles);
        Assert.False(Assert.Single(output.Tracks.Where(track => track.Kind == TrackKind.Audio)).IsDefault);
        Assert.Equal(inspection.Title, output.Title);
        Assert.True(output.HasChapters);
        Assert.True(output.HasTags);
        Assert.Equal(inspection.Attachments.ToArray(), output.Attachments.ToArray());

        // The original path now contains the result; use the unchanged fixture as the independent verification source.
        var reference = await backend.InspectAsync(new(fixturePath, TestContext.Current.CancellationToken));
        var referencePlan = Assert.IsType<RemuxPlan>(PlanBuilder.Build(new(reference, retainedIds, sourcePath, false)).Plan);
        var publishedVerification = await backend.VerifyAsync(new(referencePlan, sourcePath, TestContext.Current.CancellationToken));
        Assert.True(publishedVerification.Passed, publishedVerification.Details);

        foreach (var track in retained)
        {
            var outputTrack = Assert.Single(output.Tracks, candidate => candidate.Uid == track.Uid);
            await MediaPayloadEvidence.AssertIdenticalAsync(new(fixturePath, track.Id, sourcePath, outputTrack.Id,
                workspace.Path, FormattableString.Invariant($"replacement-track-{track.Id}")));
        }

        var attachment = Assert.Single(inspection.Attachments);
        var outputAttachment = Assert.Single(output.Attachments);
        await MediaPayloadEvidence.AssertIdenticalAsync(new(fixturePath, attachment.Id, sourcePath, outputAttachment.Id,
            workspace.Path, "replacement-attachment", "attachments"));
        Assert.Equal(originalHash, await HashAsync(fixturePath));
        Assert.Equal([sourcePath], Directory.GetFiles(sourceDirectory));
        Assert.Empty(Directory.GetDirectories(sourceDirectory));
        Assert.False(File.Exists(Path.Combine(sourceDirectory, MediaContainerPolicy.TrimmedFileName(sourcePath))));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(data, "ownership")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
    }

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
        var sourceSubtitles = Assert.Single(inspection.Tracks.Where(track => track.Kind == TrackKind.Subtitles));
        var outputSubtitles = Assert.Single(output.Tracks.Where(track => track.Kind == TrackKind.Subtitles));
        await MediaPayloadEvidence.AssertIdenticalAsync(new PayloadComparison(sourcePath, sourceSubtitles.Id,
            candidate, outputSubtitles.Id, workspace.Path, "retained-subtitles"));
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

    /// <summary>Cancels an observed live remux worker and checks that the same process handle exits before cleanup.</summary>
    [MediaFact]
    public async Task CancellationDuringRealWorkerOutputReapsWorkerAndPreservesOriginal()
    {
        using var workspace = new TestWorkspace();
        var sourcePath = Path.Combine(MediaTestEnvironment.FixtureDirectory, "cancellation.mkv");
        var originalHash = await HashAsync(sourcePath);
        Process? worker = null;
        var toolPaths = MkvToolPaths.FromDirectory(MediaTestEnvironment.ToolDirectory) with { IsDevelopmentOverride = true };
        var backend = new MkvBackend(new MkvBackendOptions(new FileSourceAccess(), toolPaths,
            Path.Combine(workspace.Path, "scratch"))
        {
            RemuxProcessStarted = identifier =>
            {
                worker = Process.GetProcessById(identifier);
                _ = worker.SafeHandle;
                Assert.False(worker.HasExited, "The observed remux worker must still be alive when its handle is retained.");
            },
        });
        var inspection = await backend.InspectAsync(new InspectRequest(sourcePath));
        var candidate = Path.Combine(workspace.Path, "cancel-during-work.mkv");
        var retainedIds = inspection.Tracks.Where(track => track.Kind != TrackKind.Subtitles).Select(track => track.Id).ToArray();
        var plan = PlanBuilder.Build(new PlanRequest(inspection, retainedIds, candidate, false)).Plan!;
        using var cancellationSource = new CancellationTokenSource();
        var activeWorkerAtCancellation = false;
        var progress = new SynchronousProgress(percentage =>
        {
            var workerIsActive = worker is not null && !worker.HasExited && percentage < 100;

            if (!workerIsActive)
            {
                return;
            }

            activeWorkerAtCancellation = true;
            cancellationSource.Cancel();
        });

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.RemuxAsync(
                new RemuxRequest(plan, candidate, progress, cancellationSource.Token)));
            Assert.True(activeWorkerAtCancellation, "Buffered progress from an already-exited worker cannot satisfy this regression.");
            Assert.NotNull(worker);
            await worker.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.True(worker.HasExited, "The same retained worker handle must report exit after cancellation completes.");
            Assert.Equal(originalHash, await HashAsync(sourcePath));
        }
        finally
        {
            worker?.Dispose();
        }
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
    internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MovieSift tests", Guid.NewGuid().ToString("N"));

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
