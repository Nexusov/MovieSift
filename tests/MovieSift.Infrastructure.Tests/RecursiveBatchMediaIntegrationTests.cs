using System.Diagnostics;
using System.Globalization;
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

/// <summary>Publishes independently planned season episodes into safely prepared relative output directories.</summary>
public sealed class RecursiveBatchMediaIntegrationTests(ITestOutputHelper evidence)
{
    private const int ExpectedFiles = 2;
    private const int ExpectedSourceAudioTracks = 2;
    private const int ExpectedRetainedTrackPayloads = 5;
    private const int MaximumDiagnosticsCharacters = 32768;
    private static readonly TimeSpan FixtureCreationTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Preserves different file-local choices, original bytes and all owned cleanup across two nested destinations.</summary>
    [MediaFact]
    public async Task SameNamedEpisodesPublishIndependentPayloadsUnderTheirOriginalSeasonDirectories()
    {
        using var workspace = new TestWorkspace();
        using var workers = new ObservedRemuxWorkers();
        var sourceRoot = Path.Combine(workspace.Path, "Series с пробелами");
        var firstSource = Path.Combine(sourceRoot, "Season 1", "episode.mkv");
        var secondSource = Path.Combine(sourceRoot, "Season 2", "episode.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(firstSource)!);
        Directory.CreateDirectory(Path.GetDirectoryName(secondSource)!);
        File.Copy(Path.Combine(MediaTestEnvironment.FixtureDirectory, "ordinary.mkv"), firstSource);
        var access = new FileSourceAccess();
        var tools = MkvToolPaths.FromDirectory(MediaTestEnvironment.ToolDirectory) with { IsDevelopmentOverride = true };
        var scratch = Path.Combine(workspace.Path, "scratch");
        var backend = new MkvBackend(new MkvBackendOptions(access, tools, scratch) { RemuxProcessStarted = workers.Observe });
        var firstInspection = await backend.InspectAsync(new(firstSource, TestContext.Current.CancellationToken));
        Assert.True(firstInspection.HasChapters);
        Assert.True(firstInspection.HasTags);
        var originalAudio = firstInspection.Tracks.Where(track => track.Kind == TrackKind.Audio).ToArray();
        Assert.Equal(ExpectedSourceAudioTracks, originalAudio.Length);
        Assert.Single(firstInspection.Attachments);
        var originalVideo = Assert.Single(firstInspection.Tracks.Where(track => track.Kind == TrackKind.Video));
        var originalSubtitle = Assert.Single(firstInspection.Tracks.Where(track => track.Kind == TrackKind.Subtitles));
        var reorderedIds = new[] { originalSubtitle.Id, originalAudio[1].Id, originalVideo.Id, originalAudio[0].Id };
        await CreateReorderedInputAsync(new(firstSource, secondSource, reorderedIds));
        var sourceHashes = new Dictionary<string, string>
        {
            [firstSource] = await HashAsync(firstSource),
            [secondSource] = await HashAsync(secondSource),
        };
        var outputRoot = Path.Combine(workspace.Path, "Output с пробелами");
        Directory.CreateDirectory(outputRoot);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outputRoot));
        var discovery = await new FileBatchInputDiscovery(access).DiscoverAsync(new(
            BatchInputKind.RecursiveFolder, [sourceRoot], TestContext.Current.CancellationToken, outputRoot));
        Assert.Null(discovery.Rejection);
        Assert.Empty(discovery.Issues);
        Assert.Equal([firstSource, secondSource], discovery.Sources.Select(source => source.FullPath));
        var session = new BatchSession();
        Assert.True(session.AddDiscoveryResult(discovery).Applied);
        var data = Path.Combine(workspace.Path, "data");
        var coordinator = new BatchCoordinator(session, new InspectionService(backend, access),
            new JobCoordinator(backend, access, new OwnedOutputStore(data)), access);
        var inspected = await coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        Assert.All(inspected.Items, item => Assert.Equal(BatchItemState.NeedsReview, item.State));
        var secondInspection = Assert.IsType<MediaInspection>(Assert.Single(inspected.Items, item => item.SourcePath == secondSource).Inspection);
        Assert.Equal(reorderedIds.Select(identifier => firstInspection.Tracks.Single(track => track.Id == identifier).Uid),
            secondInspection.Tracks.Select(track => track.Uid));
        Assert.NotEqual(originalVideo.Id, Assert.Single(secondInspection.Tracks.Where(track => track.Kind == TrackKind.Video)).Id);
        Assert.NotEqual(originalSubtitle.Id, Assert.Single(secondInspection.Tracks.Where(track => track.Kind == TrackKind.Subtitles)).Id);
        var plans = new Dictionary<Guid, RemuxPlan>();

        foreach (var item in inspected.Items)
        {
            var origin = Assert.IsType<FolderSourceOrigin>(item.FolderOrigin);
            Assert.Equal(sourceRoot, origin.RootPath);
            Assert.Equal(Path.GetRelativePath(sourceRoot, item.SourcePath), origin.RelativePath);
            var derived = OutputPathPolicy.DeriveFolderDestination(new(outputRoot, origin.RelativePath));
            Assert.Null(derived.Failure);
            var destination = Assert.IsType<string>(derived.DestinationPath);
            var expectedSeason = item.SourcePath == firstSource ? "Season 1" : "Season 2";
            Assert.Equal(Path.Combine(outputRoot, expectedSeason, "episode.trimmed.mkv"), destination);
            var inspection = item.Inspection!;
            var isFirstSource = item.SourcePath == firstSource;
            var chosenAudioUid = isFirstSource ? originalAudio[1].Uid : originalAudio[0].Uid;
            var retainedIds = inspection.Tracks.Where(track => track.Kind == TrackKind.Video
                || track.Uid == chosenAudioUid || isFirstSource && track.Kind == TrackKind.Subtitles)
                .Select(track => track.Id).ToArray();
            var planned = PlanBuilder.Build(new(inspection, retainedIds, destination, false, outputRoot));
            Assert.Null(planned.Failure);
            var plan = Assert.IsType<RemuxPlan>(planned.Plan);
            Assert.False(plan.IsNoChange);
            Assert.Equal(outputRoot, plan.OutputDirectoryRoot);
            Assert.True(session.ApprovePlan(new(item.Id, plan)).Applied);
            plans.Add(item.Id, plan);
        }

        var reviewed = await coordinator.ReviewAsync(TestContext.Current.CancellationToken);
        Assert.Null(reviewed.Failure);
        Assert.Empty(reviewed.ItemFailures);
        var review = Assert.IsType<BatchReview>(reviewed.Review);
        Assert.Equal(ExpectedFiles, review.Items.Length);
        Assert.Equal(ExpectedFiles, plans.Values.Select(plan => plan.DestinationPath).Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(Directory.EnumerateFileSystemEntries(outputRoot));
        Assert.Empty(workers.Identifiers);
        var completed = await coordinator.RunAsync(new(review.Id, CancellationToken: TestContext.Current.CancellationToken));
        Assert.True(completed.Phase == BatchPhase.Completed,
            string.Join(Environment.NewLine, completed.Items.Select(item => $"{item.SourcePath}: {item.State}; {item.Failure}; {item.Result?.Verification}")));
        Assert.Equal(BatchPauseReason.None, completed.PauseReason);
        Assert.Null(completed.Failure);
        var comparedTracks = 0;
        var audioPayloads = new List<string>();

        foreach (var item in completed.Items)
        {
            Assert.Equal(BatchItemState.Completed, item.State);
            var result = Assert.IsType<JobResult>(item.Result);
            Assert.Equal(JobPhase.Completed, result.Phase);
            Assert.Null(result.Failure);
            Assert.Empty(result.CleanupIssues);
            Assert.Equal(0, result.Tool!.ExitCode);
            var plan = plans[item.Id];
            Assert.Equal(plan.DestinationPath, result.PublishedPath);
            Assert.True(File.Exists(plan.DestinationPath));
            var verified = await backend.VerifyAsync(new(plan, plan.DestinationPath, TestContext.Current.CancellationToken));
            Assert.True(verified.Passed, verified.Details);
            var output = await backend.InspectAsync(new(plan.DestinationPath, TestContext.Current.CancellationToken));
            Assert.Equal(plan.RetainedTracks.Select(track => track.Uid), output.Tracks.Select(track => track.Uid));
            Assert.Equal(plan.Inspection.Title, output.Title);
            Assert.Equal(plan.Inspection.HasChapters, output.HasChapters);
            Assert.Equal(plan.Inspection.HasTags, output.HasTags);
            var payloads = new List<TrackPayloadEvidence>();

            foreach (var retained in plan.RetainedTracks)
            {
                var outputTrack = Assert.Single(output.Tracks, track => track.Uid == retained.Uid);
                Assert.Equal(retained.IsDefault, outputTrack.IsDefault);
                Assert.Equal(retained.IsForced, outputTrack.IsForced);
                Assert.Equal(retained.Title, outputTrack.Title);
                var compared = await MediaPayloadEvidence.AssertIdenticalAsync(new(item.SourcePath, retained.Id,
                    plan.DestinationPath, outputTrack.Id, workspace.Path,
                    FormattableString.Invariant($"recursive-{item.Id:N}-{retained.Id}")));
                payloads.Add(new(retained.Id, outputTrack.Id, retained.Uid!.Value.ToString(CultureInfo.InvariantCulture), retained.Kind.ToString(), compared));
                comparedTracks++;

                if (retained.Kind == TrackKind.Audio)
                {
                    audioPayloads.Add(compared.SourceSha256);
                }
            }

            Assert.Equal(plan.Inspection.Attachments.ToArray(), output.Attachments.ToArray());
            var attachments = new List<ComparedPayload>();

            foreach (var attachment in plan.Inspection.Attachments)
            {
                var outputAttachment = Assert.Single(output.Attachments, candidate => candidate.Uid == attachment.Uid);
                attachments.Add(await MediaPayloadEvidence.AssertIdenticalAsync(new(item.SourcePath, attachment.Id,
                    plan.DestinationPath, outputAttachment.Id, workspace.Path, $"recursive-attachment-{item.Id:N}", "attachments")));
            }

            var sourceAfter = await HashAsync(item.SourcePath);
            Assert.Equal(sourceHashes[item.SourcePath], sourceAfter);
            evidence.WriteLine(JsonSerializer.Serialize(new
            {
                kind = "real-recursive-batch-file",
                source = RelativeEvidencePath(workspace.Path, item.SourcePath),
                folderRoot = RelativeEvidencePath(workspace.Path, item.FolderOrigin!.RootPath),
                relativeSource = RelativeEvidencePath(sourceRoot, item.SourcePath),
                sourceHashBefore = sourceHashes[item.SourcePath],
                sourceHashAfter = sourceAfter,
                output = RelativeEvidencePath(outputRoot, plan.DestinationPath),
                outputHash = await HashAsync(plan.DestinationPath),
                payloads,
                attachments,
                metadataFlagsAndTimestampsVerified = verified.Passed,
                toolExitCode = result.Tool.ExitCode,
                cleanupIssues = result.CleanupIssues.Length,
            }));
        }

        Assert.Equal(ExpectedRetainedTrackPayloads, comparedTracks);
        Assert.Equal(ExpectedFiles, audioPayloads.Count);
        Assert.NotEqual(audioPayloads[0], audioPayloads[1]);
        Assert.Equal(plans.Values.Select(plan => plan.DestinationPath).Order(StringComparer.Ordinal),
            Directory.EnumerateFiles(outputRoot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
        Assert.Equal(["Season 1", "Season 2"], Directory.GetDirectories(outputRoot).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(ExpectedFiles, Directory.GetDirectories(outputRoot, "*", SearchOption.AllDirectories).Length);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(data, "ownership")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
        var workerIdentifiers = workers.AssertExited();
        evidence.WriteLine(JsonSerializer.Serialize(new
        {
            kind = "real-recursive-batch-summary",
            outputs = completed.Items.Length,
            retainedTrackPayloads = comparedTracks,
            retainedAttachmentPayloads = completed.Items.Sum(item => plans[item.Id].Inspection.Attachments.Length),
            sourceHashesUnchanged = true,
            directoriesAbsentBeforeExecution = true,
            retainedOutputDirectories = new[] { "Season 1", "Season 2" },
            ownedWorkerIds = workerIdentifiers,
            safeHandlesRetained = true,
            allOwnedWorkersExited = true,
            candidateDirectoriesRemaining = 0,
            ownershipRecordsRemaining = 0,
            scratchEntriesRemaining = 0,
        }));
    }

    /// <summary>Generates only the second source's deliberate track-order permutation with the pinned development backend.</summary>
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

    /// <summary>Hashes complete media through a read-only handle for source and output evidence.</summary>
    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, TestContext.Current.CancellationToken));
    }

    /// <summary>Removes task-specific absolute paths from the compact published test evidence.</summary>
    private static string RelativeEvidencePath(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>Retains actual owned remux handles until queue completion without acting on the workers.</summary>
    private sealed class ObservedRemuxWorkers : IDisposable
    {
        private readonly List<Process> processes = [];
        public IEnumerable<int> Identifiers => processes.Select(process => process.Id);

        /// <summary>Retains a handle to the same native worker when the production runner announces its start.</summary>
        public void Observe(int identifier)
        {
            var process = Process.GetProcessById(identifier);
            _ = process.SafeHandle;
            processes.Add(process);
        }

        /// <summary>Checks that both owned remux processes have exited after the existing awaited lifecycle completes.</summary>
        public int[] AssertExited()
        {
            Assert.Equal(ExpectedFiles, processes.Count);
            Assert.All(processes, process => Assert.True(process.HasExited));
            return processes.Select(process => process.Id).ToArray();
        }

        /// <summary>Disposes observation handles without requesting termination or changing process state.</summary>
        public void Dispose()
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private sealed record ReorderedInputRequest(string SourcePath, string DestinationPath, IReadOnlyList<int> OrderedIds);
    private sealed record TrackPayloadEvidence(int SourceId, int OutputId, string Uid, string Kind, ComparedPayload Payload);
}
