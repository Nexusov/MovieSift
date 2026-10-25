using System.Security.Cryptography;
using System.Text.Json;
using MovieSift.Application.Batches;
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

/// <summary>Runs independently approved real files through the complete sequential batch owner.</summary>
public sealed class BatchMediaIntegrationTests(ITestOutputHelper evidence)
{
    private const int MaximumDiagnosticsCharacters = 32768;
    private static readonly TimeSpan FixtureCreationTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Preserves independently selected payloads despite different input track order and identifiers.</summary>
    [MediaFact]
    public async Task ReorderedInputsPublishTheirOwnSelectionsAndCleanEveryOwnedCandidate()
    {
        using var workspace = new TestWorkspace();
        var firstSource = Path.Combine(workspace.Path, "a исходник.mkv");
        var secondSource = Path.Combine(workspace.Path, "b reordered.mkv");
        File.Copy(Path.Combine(MediaTestEnvironment.FixtureDirectory, "ordinary.mkv"), firstSource);
        var backend = MediaTestEnvironment.CreateBackend(Path.Combine(workspace.Path, "scratch"));
        var firstInspection = await backend.InspectAsync(new InspectRequest(firstSource, TestContext.Current.CancellationToken));
        var originalAudio = firstInspection.Tracks.Where(track => track.Kind == TrackKind.Audio).ToArray();
        Assert.Equal(2, originalAudio.Length);
        var originalVideo = Assert.Single(firstInspection.Tracks.Where(track => track.Kind == TrackKind.Video));
        var originalSubtitles = Assert.Single(firstInspection.Tracks.Where(track => track.Kind == TrackKind.Subtitles));
        var reorderedIds = new[] { originalSubtitles.Id, originalAudio[1].Id, originalVideo.Id, originalAudio[0].Id };
        await CreateReorderedInputAsync(new(firstSource, secondSource, reorderedIds));
        var sourceHashes = new Dictionary<string, string>
        {
            [firstSource] = await HashAsync(firstSource),
            [secondSource] = await HashAsync(secondSource),
        };
        var sourceAccess = new FileSourceAccess();
        var discovery = await new FileBatchInputDiscovery(sourceAccess).DiscoverAsync(new(
            BatchInputKind.ExplicitFiles, [secondSource, firstSource], TestContext.Current.CancellationToken));
        Assert.Null(discovery.Rejection);
        Assert.Empty(discovery.Issues);
        Assert.Equal(2, discovery.Sources.Length);
        var session = new BatchSession(discovery.Sources);
        var dataDirectory = Path.Combine(workspace.Path, "data");
        var outputDirectory = Path.Combine(workspace.Path, "results");
        Directory.CreateDirectory(outputDirectory);
        var coordinator = new BatchCoordinator(session, new InspectionService(backend, sourceAccess),
            new JobCoordinator(backend, sourceAccess, new OwnedOutputStore(dataDirectory)), sourceAccess);
        var inspected = await coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        Assert.All(inspected.Items, item => Assert.Equal(BatchItemState.NeedsReview, item.State));
        var secondInspection = Assert.Single(inspected.Items, item => item.SourcePath == secondSource).Inspection!;
        var expectedReorderedUids = reorderedIds.Select(identifier => firstInspection.Tracks.Single(track => track.Id == identifier).Uid);
        Assert.Equal(expectedReorderedUids, secondInspection.Tracks.Select(track => track.Uid));
        Assert.NotEqual(originalVideo.Id, Assert.Single(secondInspection.Tracks.Where(track => track.Kind == TrackKind.Video)).Id);
        Assert.NotEqual(originalSubtitles.Id, Assert.Single(secondInspection.Tracks.Where(track => track.Kind == TrackKind.Subtitles)).Id);
        var plans = new Dictionary<Guid, RemuxPlan>();

        foreach (var item in inspected.Items)
        {
            var inspection = item.Inspection!;
            var isFirstSource = item.SourcePath == firstSource;
            var approvedAudioUid = isFirstSource ? originalAudio[1].Uid : originalAudio[0].Uid;
            var retainedIds = inspection.Tracks.Where(track => track.Kind == TrackKind.Video
                || track.Uid == approvedAudioUid || isFirstSource && track.Kind == TrackKind.Subtitles)
                .Select(track => track.Id).ToArray();
            var destination = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(item.SourcePath) + ".trimmed.mkv");
            var planResult = PlanBuilder.Build(new(inspection, retainedIds, destination, false));
            Assert.Null(planResult.Failure);
            var plan = Assert.IsType<RemuxPlan>(planResult.Plan);
            Assert.True(session.ApprovePlan(new(item.Id, plan)).Applied);
            plans.Add(item.Id, plan);
        }

        var reviewed = await coordinator.ReviewAsync(TestContext.Current.CancellationToken);
        Assert.Null(reviewed.Failure);
        Assert.Empty(reviewed.ItemFailures);
        var review = Assert.IsType<BatchReview>(reviewed.Review);
        var completed = await coordinator.RunAsync(new(review.Id, CancellationToken: TestContext.Current.CancellationToken));
        var completionEvidence = string.Join(Environment.NewLine, completed.Items.Select(item =>
            $"{item.SourcePath}: {item.State}; failure={item.Failure}; tool={item.Result?.Tool}; verification={item.Result?.Verification}"));
        Assert.True(completed.Phase == BatchPhase.Completed, $"{completed.Phase}/{completed.PauseReason}: {completionEvidence}");
        Assert.Equal(BatchPauseReason.None, completed.PauseReason);
        Assert.Null(completed.Failure);

        foreach (var item in completed.Items)
        {
            Assert.Equal(BatchItemState.Completed, item.State);
            var result = Assert.IsType<JobResult>(item.Result);
            Assert.Equal(JobPhase.Completed, result.Phase);
            Assert.Null(result.Failure);
            Assert.Empty(result.CleanupIssues);
            var plan = plans[item.Id];
            Assert.Equal(plan.DestinationPath, result.PublishedPath);
            var output = await backend.InspectAsync(new(plan.DestinationPath, TestContext.Current.CancellationToken));
            Assert.Equal(plan.RetainedTracks.Select(track => track.Uid), output.Tracks.Select(track => track.Uid));

            foreach (var retained in plan.RetainedTracks)
            {
                var outputTrack = Assert.Single(output.Tracks, track => track.Uid == retained.Uid);
                await MediaPayloadEvidence.AssertIdenticalAsync(new(item.SourcePath, retained.Id,
                    plan.DestinationPath, outputTrack.Id, workspace.Path, $"{item.Id:N}-{retained.Id}"));
            }

            var sourceHashAfter = await HashAsync(item.SourcePath);
            Assert.Equal(sourceHashes[item.SourcePath], sourceHashAfter);
            evidence.WriteLine(JsonSerializer.Serialize(new
            {
                kind = "real-batch-file",
                source = Path.GetFileName(item.SourcePath),
                sourceHashBefore = sourceHashes[item.SourcePath],
                sourceHashAfter,
                output = Path.GetFileName(plan.DestinationPath),
                outputHash = await HashAsync(plan.DestinationPath),
                retained = plan.RetainedTracks.Select(track => new
                {
                    sourceTrackId = track.Id,
                    outputTrackId = output.Tracks.Single(candidate => candidate.Uid == track.Uid).Id,
                    kind = track.Kind.ToString(),
                    payloadCompared = true
                }),
                state = item.State.ToString(),
                toolExitCode = result.Tool!.ExitCode,
                cleanupIssues = result.CleanupIssues.Length
            }));
        }

        Assert.Equal(2, Directory.GetFiles(outputDirectory, "*.mkv").Length);
        Assert.Empty(Directory.GetDirectories(outputDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(dataDirectory, "ownership")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(workspace.Path, "scratch")));
        evidence.WriteLine("Real MKVToolNix batch: two outputs; all retained payload hashes equal; both sources unchanged; candidate, scratch and ownership entries absent.");
    }

    /// <summary>Creates a test-only local-ID permutation from the generated fixture using the pinned native remuxer.</summary>
    private static async Task CreateReorderedInputAsync(ReorderedInputRequest request)
    {
        var tools = MkvToolPaths.FromDirectory(MediaTestEnvironment.ToolDirectory);
        var environment = new Dictionary<string, string>();

        if (tools.NativeLibraryDirectory is { } nativeLibraryDirectory)
        {
            environment["LD_LIBRARY_PATH"] = nativeLibraryDirectory;
        }

        if (OperatingSystem.IsLinux())
        {
            environment["LC_ALL"] = "C.UTF-8";
            environment["LANG"] = "C.UTF-8";
        }

        var order = string.Join(',', request.OrderedIds.Select(identifier => FormattableString.Invariant($"0:{identifier}")));
        var result = await new OwnedProcessRunner().RunAsync(new(tools.MkvMerge,
            ["--disable-track-statistics-tags", "--output", request.DestinationPath, "--track-order", order, request.SourcePath],
            MaximumDiagnosticsCharacters, FixtureCreationTimeout, null, TestContext.Current.CancellationToken, environment));
        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
    }

    /// <summary>Hashes the original container without decoding, touching metadata, or replacing its path.</summary>
    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, TestContext.Current.CancellationToken));
    }

    private sealed record ReorderedInputRequest(string SourcePath, string DestinationPath, IReadOnlyList<int> OrderedIds);
}
