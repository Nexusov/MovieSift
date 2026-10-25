using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MovieSift.Application.Batches;
using MovieSift.Application.Inspection;
using MovieSift.Application.Jobs;
using MovieSift.Application.QueueLists;
using MovieSift.Core.Media;
using MovieSift.Core.Planning;
using MovieSift.Infrastructure.Files;
using MovieSift.Infrastructure.MediaTools;
using MovieSift.Infrastructure.Persistence;
using Xunit;

namespace MovieSift.Infrastructure.Tests;

/// <summary>Restores listing metadata from a real portable record before fresh inspection and the existing verified queue lifecycle.</summary>
public sealed class QueueListMediaIntegrationTests(ITestOutputHelper evidence)
{
    private const int ExpectedFiles = 2;
    private const int ExpectedTrackPayloads = 6;

    /// <summary>Recovers unfinished season paths without authority and independently publishes fresh explicitly reviewed plans.</summary>
    [MediaFact]
    public async Task FreshInstanceRestoresListingThenVerifiesIndependentMediaAndNestedOutputs()
    {
        using var workspace = new TestWorkspace();
        using var workers = new ObservedWorkers();
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixtureDirectory = Path.Combine(workspace.Path, "prepared fixtures");
        Directory.CreateDirectory(fixtureDirectory);
        var templatePath = Path.Combine(MediaTestEnvironment.FixtureDirectory, "ordinary.mkv");
        var templateHash = await HashAsync(templatePath);
        var fixtures = await ForcedSubtitleFixture.CreateAsync(new(templatePath, fixtureDirectory));
        var sourceRoot = Path.Combine(workspace.Path, "Series Кириллица");
        var firstSource = Path.Combine(sourceRoot, "Season 1", "episode.mkv");
        var secondSource = Path.Combine(sourceRoot, "Season 2", "episode.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(firstSource)!);
        Directory.CreateDirectory(Path.GetDirectoryName(secondSource)!);
        File.Copy(fixtures.First, firstSource);
        File.Copy(fixtures.Second, secondSource);
        var sourceHashes = new Dictionary<string, string>
        {
            [firstSource] = await HashAsync(firstSource),
            [secondSource] = await HashAsync(secondSource)
        };
        var outputRoot = Path.Combine(workspace.Path, "Output с пробелами");
        Directory.CreateDirectory(outputRoot);
        var sourceAccess = new FileSourceAccess();
        var discovery = await new FileBatchInputDiscovery(sourceAccess).DiscoverAsync(new(
            BatchInputKind.RecursiveFolder, [sourceRoot], cancellationToken, outputRoot));
        Assert.Null(discovery.Rejection);
        Assert.Empty(discovery.Issues);
        var originalSession = new BatchSession();
        Assert.True(originalSession.AddDiscoveryResult(discovery).Applied);

        foreach (var item in originalSession.Snapshot.Items)
        {
            var destination = OutputPathPolicy.DeriveFolderDestination(new(outputRoot, item.FolderOrigin!.RelativePath));
            Assert.Null(destination.Failure);
            Assert.True(originalSession.SetDestination(new(item.Id, destination.DestinationPath!, outputRoot)).Applied);
        }

        var savedList = originalSession.CaptureQueueList();
        var dataDirectory = Path.Combine(workspace.Path, "portable data");
        var writer = new PortableQueueListStore(dataDirectory);
        var initial = await writer.ReadAsync(cancellationToken);
        Assert.Null(initial.Failure);
        Assert.Null(initial.Revision);
        var saved = await writer.WriteAsync(new(savedList, initial.Revision, cancellationToken));
        Assert.Null(saved.Failure);
        Assert.True(saved.Committed);
        Assert.True(saved.CleanupIssues.IsDefaultOrEmpty);
        Assert.NotNull(saved.Revision);
        var reader = new PortableQueueListStore(dataDirectory);
        var recovered = await reader.ReadAsync(cancellationToken);
        Assert.Null(recovered.Failure);
        Assert.True(QueueListPolicy.ContentEquals(savedList, recovered.List));
        Assert.Equal(saved.Revision, recovered.Revision);
        var tools = MkvToolPaths.FromDirectory(MediaTestEnvironment.ToolDirectory) with { IsDevelopmentOverride = true };
        var scratch = Path.Combine(workspace.Path, "inspection scratch");
        var backend = new MkvBackend(new MkvBackendOptions(sourceAccess, tools, scratch) { RemuxProcessStarted = workers.Observe });
        var outputStore = new OwnedOutputStore(dataDirectory);
        var session = new BatchSession();
        var coordinator = new BatchCoordinator(session, new InspectionService(backend, sourceAccess),
            new JobCoordinator(backend, sourceAccess, outputStore), sourceAccess);
        Assert.True(coordinator.RestoreQueueList(recovered.List!, session.Snapshot.Revision).Applied);
        var restored = session.Snapshot;
        Assert.Equal(ExpectedFiles, restored.Items.Length);
        Assert.All(restored.Items, item =>
        {
            Assert.Equal(BatchItemState.PendingInspection, item.State);
            Assert.Null(item.DiscoveredSource);
            Assert.Null(item.Inspection);
            Assert.Null(item.ApprovedPlan);
            Assert.Null(item.Result);
            Assert.Empty(item.CurrentRetainedTrackIds);
            Assert.DoesNotContain(item.Id, originalSession.Snapshot.Items.Select(previous => previous.Id));
        });
        Assert.Empty(Directory.EnumerateFileSystemEntries(outputRoot));
        Assert.Empty(workers.Identifiers);
        Assert.Null((await coordinator.ReviewSelectionsAsync(new(session.Snapshot.Revision, [], cancellationToken))).Review);
        Assert.Empty(workers.Identifiers);
        var inspected = await coordinator.InspectPendingAsync(cancellationToken);
        Assert.All(inspected.Items, item => Assert.Equal(BatchItemState.NeedsReview, item.State));
        var first = Assert.Single(inspected.Items, item => item.SourcePath == firstSource);
        var second = Assert.Single(inspected.Items, item => item.SourcePath == secondSource);
        Assert.NotEqual(first.Inspection!.Tracks.Select(track => track.Uid).ToArray(),
            second.Inspection!.Tracks.Select(track => track.Uid).ToArray());
        Assert.NotEqual(Assert.Single(first.Inspection.Tracks, track => track.Kind == TrackKind.Video).Id,
            Assert.Single(second.Inspection.Tracks, track => track.Kind == TrackKind.Video).Id);
        var firstAudio = first.Inspection.Tracks.Where(track => track.Kind == TrackKind.Audio).ToArray();
        var chosenFirstAudio = firstAudio[1].Uid;
        var chosenSecondAudio = firstAudio[0].Uid;

        foreach (var item in inspected.Items)
        {
            var inspection = item.Inspection!;
            var chosenAudio = item.SourcePath == firstSource ? chosenFirstAudio : chosenSecondAudio;
            var retained = inspection.Tracks.Where(track => track.Kind == TrackKind.Video ||
                track.Uid == chosenAudio || track.Kind == TrackKind.Subtitles && track.IsForced)
                .Select(track => track.Id).ToArray();
            Assert.True(session.SetSelection(new(item.Id, retained.ToImmutableArray())).Applied);
        }

        var review = await coordinator.ReviewSelectionsAsync(new(session.Snapshot.Revision, [], cancellationToken));
        Assert.Null(review.Failure);
        Assert.Empty(review.ItemFailures);
        Assert.NotNull(review.Review);
        var plans = review.Review.Items.ToDictionary(item => item.ItemId, item => item.Plan);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outputRoot));
        var completed = await coordinator.RunAsync(new(review.Review.Id, CancellationToken: cancellationToken));
        Assert.True(completed.Phase == BatchPhase.Completed,
            string.Join(Environment.NewLine, completed.Items.Select(item => $"{item.SourcePath}: {item.State}; {item.Failure}")));
        var payloadCount = 0;
        var audioHashes = new List<string>();
        var subtitleHashes = new List<string>();

        foreach (var item in completed.Items)
        {
            Assert.Equal(BatchItemState.Completed, item.State);
            Assert.NotNull(item.Result);
            Assert.Null(item.Result.Failure);
            Assert.Empty(item.Result.CleanupIssues);
            var plan = plans[item.Id];
            Assert.Equal(plan.DestinationPath, item.Result.PublishedPath);
            var verification = await backend.VerifyAsync(new(plan, plan.DestinationPath, cancellationToken));
            Assert.True(verification.Passed, verification.Details);
            var output = await backend.InspectAsync(new(plan.DestinationPath, cancellationToken));
            Assert.Equal(plan.RetainedTracks.Select(track => track.Uid), output.Tracks.Select(track => track.Uid));
            Assert.Equal(plan.Inspection.Attachments.ToArray(), output.Attachments.ToArray());
            var payloads = new List<object>();

            foreach (var retained in plan.RetainedTracks)
            {
                var outputTrack = Assert.Single(output.Tracks, track => track.Uid == retained.Uid);
                Assert.Equal(retained.IsForced, outputTrack.IsForced);
                Assert.Equal(retained.IsDefault, outputTrack.IsDefault);
                Assert.Equal(retained.Title, outputTrack.Title);
                var compared = await MediaPayloadEvidence.AssertIdenticalAsync(new(item.SourcePath, retained.Id,
                    plan.DestinationPath, outputTrack.Id, workspace.Path, $"queue-recovery-{item.Id:N}-{retained.Id}"));
                payloadCount++;
                payloads.Add(new { sourceId = retained.Id, outputId = outputTrack.Id, retained.Kind, retained.Uid, compared });

                if (retained.Kind == TrackKind.Audio)
                {
                    audioHashes.Add(compared.SourceSha256);
                }

                if (retained.Kind == TrackKind.Subtitles)
                {
                    subtitleHashes.Add(compared.SourceSha256);
                }
            }

            var attachments = new List<ComparedPayload>();

            foreach (var attachment in plan.Inspection.Attachments)
            {
                var outputAttachment = Assert.Single(output.Attachments, candidate => candidate.Uid == attachment.Uid);
                attachments.Add(await MediaPayloadEvidence.AssertIdenticalAsync(new(item.SourcePath, attachment.Id,
                    plan.DestinationPath, outputAttachment.Id, workspace.Path, $"queue-recovery-attachment-{item.Id:N}", "attachments")));
            }

            var sourceAfter = await HashAsync(item.SourcePath);
            Assert.Equal(sourceHashes[item.SourcePath], sourceAfter);
            evidence.WriteLine(JsonSerializer.Serialize(new
            {
                kind = "real-restored-queue-file",
                source = Path.GetRelativePath(sourceRoot, item.SourcePath).Replace('\\', '/'),
                output = Path.GetRelativePath(outputRoot, plan.DestinationPath).Replace('\\', '/'),
                sourceBefore = sourceHashes[item.SourcePath],
                sourceAfter,
                outputHash = await HashAsync(plan.DestinationPath),
                payloads,
                attachments,
                verification.Passed,
                cleanupIssues = item.Result.CleanupIssues.Length
            }));
        }

        Assert.Equal(ExpectedTrackPayloads, payloadCount);
        Assert.Equal(ExpectedFiles, audioHashes.Count);
        Assert.Equal(ExpectedFiles, subtitleHashes.Count);
        Assert.NotEqual(audioHashes[0], audioHashes[1]);
        Assert.NotEqual(subtitleHashes[0], subtitleHashes[1]);
        Assert.Empty(session.CaptureQueueList().Entries);
        var checkpoint = await reader.WriteAsync(new(session.CaptureQueueList(), recovered.Revision, cancellationToken));
        Assert.True(checkpoint.Committed);
        var finalList = await new PortableQueueListStore(dataDirectory).ReadAsync(cancellationToken);
        Assert.Null(finalList.Failure);
        Assert.Empty(finalList.List!.Entries);
        Assert.Equal(plans.Values.Select(plan => plan.DestinationPath).Order(StringComparer.Ordinal),
            Directory.EnumerateFiles(outputRoot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(dataDirectory, "ownership")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
        Assert.Equal(templateHash, await HashAsync(templatePath));
        var workerIds = workers.AssertExited();
        evidence.WriteLine(JsonSerializer.Serialize(new
        {
            kind = "real-restored-queue-summary",
            freshStoreAndServiceInstances = true,
            restoredFiles = ExpectedFiles,
            trackPayloads = payloadCount,
            attachmentPayloads = completed.Items.Sum(item => item.Inspection!.Attachments.Length),
            freshInspectionsAndExplicitApprovals = true,
            noAutomaticProcessing = true,
            unchangedSources = true,
            absentOutputDirectoriesPreparedAtExecution = true,
            ownedWorkerIds = workerIds,
            allOwnedWorkersExited = true,
            emptyCompletedCheckpoint = true,
            ownershipRecordsRemaining = 0,
            scratchEntriesRemaining = 0
        }));
    }

    /// <summary>Hashes the complete read-only source or output for evidence independent of status labels.</summary>
    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, TestContext.Current.CancellationToken));
    }

    /// <summary>Retains only observation handles while the production owner controls worker termination and cleanup.</summary>
    private sealed class ObservedWorkers : IDisposable
    {
        private readonly List<Process> processes = [];
        public IEnumerable<int> Identifiers => processes.Select(process => process.Id);

        /// <summary>Captures the actual process handle announced by the pinned backend runner.</summary>
        public void Observe(int identifier)
        {
            var process = Process.GetProcessById(identifier);
            _ = process.SafeHandle;
            processes.Add(process);
        }

        /// <summary>Checks awaited worker exit without inferring it from buffered progress.</summary>
        public int[] AssertExited()
        {
            Assert.Equal(ExpectedFiles, processes.Count);
            Assert.All(processes, process => Assert.True(process.HasExited));
            return processes.Select(process => process.Id).ToArray();
        }

        /// <summary>Disposes observation handles without killing or modifying the workers.</summary>
        public void Dispose()
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}
