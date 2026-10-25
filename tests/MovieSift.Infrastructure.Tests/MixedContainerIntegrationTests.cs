using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MovieSift.Application.Batches;
using MovieSift.Application.Inspection;
using MovieSift.Application.Jobs;
using MovieSift.Core.Media;
using MovieSift.Core.Planning;
using MovieSift.Core.Rules;
using MovieSift.Infrastructure.Files;
using MovieSift.Infrastructure.MediaTools;
using MovieSift.Infrastructure.Persistence;
using Xunit;

namespace MovieSift.Infrastructure.Tests;

/// <summary>Exercises real mixed-format recovery, independent rules and sequential publication through the production owners.</summary>
public sealed class MixedContainerIntegrationTests(ITestOutputHelper evidence)
{
    /// <summary>Restores only intent, derives local MP4 rule selections and publishes MKV/MP4 without changing sources.</summary>
    [MediaFact]
    public async Task RestoredMixedListRequiresFreshPlansAndPublishesItsOwnEncodedContent()
    {
        using var workspace = new TestWorkspace();
        using var workers = new ObservedMediaWorkers();
        var cancellationToken = TestContext.Current.CancellationToken;
        var sourceRoot = Path.Combine(workspace.Path, "Сериал с пробелами");
        var outputRoot = Path.Combine(workspace.Path, "Output Кириллица");
        var dataDirectory = Path.Combine(workspace.Path, "portable data");
        var scratch = Path.Combine(dataDirectory, "inspection-scratch");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(outputRoot);
        var inputs = new[]
        {
            (Template: Path.Combine(MediaTestEnvironment.FixtureDirectory, "ordinary.mkv"), Relative: "Season 1/episode.mkv"),
            (Template: Path.Combine(MediaTestEnvironment.FixtureDirectory, "mp4", "ordinary.mp4"), Relative: "Season 2/episode.mp4"),
            (Template: Path.Combine(MediaTestEnvironment.FixtureDirectory, "mp4", "reordered.mp4"), Relative: "Season 3/episode.mp4"),
        };

        foreach (var input in inputs)
        {
            var sourcePath = Path.Combine(sourceRoot, input.Relative);
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.Copy(input.Template, sourcePath);
        }

        var access = new FileSourceAccess();
        var discovery = await new FileBatchInputDiscovery(access).DiscoverAsync(new(
            BatchInputKind.RecursiveFolder, [sourceRoot], cancellationToken, outputRoot));
        Assert.Null(discovery.Rejection);
        Assert.Empty(discovery.Issues);
        Assert.Equal(inputs.Length, discovery.Sources.Length);
        var original = new BatchSession();
        Assert.True(original.AddDiscoveryResult(discovery).Applied);
        var sourceHashes = new Dictionary<string, string>();

        foreach (var item in original.Snapshot.Items)
        {
            sourceHashes[item.SourcePath] = await HashAsync(item.SourcePath);
            var destination = OutputPathPolicy.DeriveFolderDestination(new(outputRoot, item.FolderOrigin!.RelativePath));
            Assert.Null(destination.Failure);
            Assert.True(original.SetDestination(new(item.Id, destination.DestinationPath!, outputRoot)).Applied);
        }

        var writer = new PortableQueueListStore(dataDirectory);
        var saved = await writer.WriteAsync(new(original.CaptureQueueList(), null, cancellationToken));
        Assert.True(saved.Committed, saved.Failure?.ToString());
        var recovered = await new PortableQueueListStore(dataDirectory).ReadAsync(cancellationToken);
        Assert.Null(recovered.Failure);
        var matroska = new MkvBackend(new MkvBackendOptions(access, MkvToolPaths.FromDirectory(MediaTestEnvironment.ToolDirectory), scratch)
        { RemuxProcessStarted = workers.Observe });
        var mp4 = new FfmpegBackend(new FfmpegBackendOptions(access, FfmpegToolPaths.FromDirectory(MediaTestEnvironment.ToolDirectory))
        { RemuxProcessStarted = workers.Observe });
        var backend = new ContainerMediaBackend(matroska, mp4);
        var store = new OwnedOutputStore(dataDirectory);
        var session = new BatchSession();
        var coordinator = new BatchCoordinator(session, new InspectionService(backend, access),
            new JobCoordinator(backend, access, store), access);
        Assert.True(coordinator.RestoreQueueList(recovered.List!).Applied);
        Assert.Empty(workers.Identifiers);
        Assert.All(session.Snapshot.Items, item =>
        {
            Assert.Null(item.Inspection);
            Assert.Null(item.ApprovedPlan);
            Assert.Equal(BatchItemState.PendingInspection, item.State);
            Assert.Empty(item.CurrentRetainedTrackIds);
        });
        var premature = await coordinator.ReviewAsync(cancellationToken);
        Assert.Null(premature.Review);
        Assert.NotEmpty(premature.ItemFailures);
        var inspected = await coordinator.InspectPendingAsync(cancellationToken);
        Assert.All(inspected.Items, item => Assert.Equal(BatchItemState.NeedsReview, item.State));
        var mp4Rows = inspected.Items.Where(item => item.Inspection!.Container == MediaContainer.Mp4).ToArray();
        Assert.Equal(2, mp4Rows.Length);
        Assert.All(mp4Rows.SelectMany(item => item.Inspection!.Tracks), track => Assert.Null(track.Uid));
        var independentIndexes = mp4Rows.Select(item => item.Inspection!.Tracks.Single(track => track.Title == "Alternate audio").Id).ToArray();
        Assert.NotEqual(independentIndexes[0], independentIndexes[1]);

        // Keep the MKV outside this MP4-specific rule application; its independent manual plan is created below.
        var mkvRow = Assert.Single(inspected.Items, item => item.Inspection!.Container == MediaContainer.Matroska);
        Assert.True(session.SetIncluded(mkvRow.Id, false).Applied);
        LanguageRules rules = new(new(LanguageRuleMode.KeepMatching, ["en"], new(true, "Alternate")),
            new(LanguageRuleMode.KeepMatching, ["ru"], new(true, "Captions")), true);
        var preview = Assert.IsType<BatchLanguagePreview>(coordinator.PreviewLanguageRules(rules).Preview);
        Assert.All(preview.Items.Where(item => item.Inspection?.Container == MediaContainer.Mp4), item =>
        {
            Assert.Empty(item.Issues);
            var tracks = item.Inspection!.Tracks.Where(track => item.ProposedRetainedTrackIds.Contains(track.Id)).ToArray();
            Assert.Single(tracks, track => track.Kind == TrackKind.Audio && track.Title == "Alternate audio");
            Assert.Single(tracks, track => track.Kind == TrackKind.Subtitles && track.IsForced);
            Assert.Equal(item.Inspection.Tracks.Count(track => track.Kind == TrackKind.Video), tracks.Count(track => track.Kind == TrackKind.Video));
        });
        Assert.True(coordinator.ApplyLanguageRules(preview).Applied);
        Assert.True(session.SetIncluded(mkvRow.Id, true).Applied);
        var mkvIds = mkvRow.Inspection!.Tracks.Where(track => track.Kind == TrackKind.Video ||
            track.Kind == TrackKind.Audio && !track.IsDefault).Select(track => track.Id).ToImmutableArray();
        Assert.True(session.SetSelection(new(mkvRow.Id, mkvIds)).Applied);
        var plans = new Dictionary<Guid, RemuxPlan>();

        foreach (var item in session.Snapshot.Items)
        {
            var planResult = PlanBuilder.Build(new(item.Inspection!, item.CurrentRetainedTrackIds,
                item.IntendedDestinationPath!, false, item.IntendedOutputDirectoryRoot));
            Assert.Null(planResult.Failure);
            var plan = Assert.IsType<RemuxPlan>(planResult.Plan);
            Assert.True(session.ApprovePlan(new(item.Id, plan)).Applied);
            plans[item.Id] = plan;
        }

        var review = await coordinator.ReviewAsync(cancellationToken);
        Assert.Empty(review.ItemFailures);
        var reviewed = Assert.IsType<BatchReview>(review.Review);
        var completed = await coordinator.RunAsync(new(reviewed.Id, CancellationToken: cancellationToken));
        Assert.True(completed.Phase == BatchPhase.Completed, string.Join(Environment.NewLine,
            completed.Items.Select(item => $"{item.State}: {item.Failure}; {item.Result?.Tool}; {item.Result?.Verification}")));

        foreach (var item in completed.Items)
        {
            var plan = plans[item.Id];
            Assert.Equal(plan.DestinationPath, item.Result!.PublishedPath);
            Assert.Empty(item.Result.CleanupIssues);
            var output = await backend.InspectAsync(new(plan.DestinationPath, cancellationToken));
            Assert.Equal(plan.Inspection.Container, output.Container);
            Assert.Equal(plan.RetainedTracks.Length, output.Tracks.Length);
            var payloadEvidence = new List<object>();

            for (var outputIndex = 0; outputIndex < output.Tracks.Length; outputIndex++)
            {
                var retained = plan.RetainedTracks[outputIndex];
                var actual = output.Tracks[outputIndex];
                Assert.Equal(retained.Kind, actual.Kind);
                Assert.Equal(retained.Title, actual.Title);
                Assert.Equal(retained.IsDefault, actual.IsDefault);
                Assert.Equal(retained.IsForced, actual.IsForced);

                if (output.Container == MediaContainer.Mp4)
                {
                    payloadEvidence.Add(new
                    {
                        sourceIndex = retained.Id,
                        outputIndex = actual.Id,
                        payload = await Mp4PayloadEvidence.CompareAsync(new(item.SourcePath, retained.Id, plan.DestinationPath, actual.Id))
                    });
                    continue;
                }

                payloadEvidence.Add(await MediaPayloadEvidence.AssertIdenticalAsync(new(item.SourcePath,
                    retained.Id, plan.DestinationPath, actual.Id, workspace.Path, $"mixed-{item.Id}-{retained.Id}")));
            }

            Assert.Equal(sourceHashes[item.SourcePath], await HashAsync(item.SourcePath));
            evidence.WriteLine(JsonSerializer.Serialize(new
            {
                kind = "real-mixed-restored-batch",
                source = Path.GetRelativePath(sourceRoot, item.SourcePath),
                destination = Path.GetRelativePath(outputRoot, plan.DestinationPath),
                container = output.Container.ToString(),
                sourceSha256 = sourceHashes[item.SourcePath],
                sourceUnchanged = true,
                outputSha256 = await HashAsync(plan.DestinationPath),
                payloadEvidence,
                verification = item.Result.Verification,
                ownedCleanupIssues = item.Result.CleanupIssues.Length,
            }));
        }

        workers.AssertSequentialAndExited(inputs.Length);
        Assert.Empty(session.CaptureQueueList().Entries);
        Assert.Empty(await store.FindInterruptedAsync(cancellationToken));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(dataDirectory, "ownership")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
        Assert.Equal(plans.Values.Select(plan => plan.DestinationPath).Order(StringComparer.Ordinal),
            Directory.EnumerateFiles(outputRoot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
    }

    /// <summary>Reads complete source/container bytes only for immutable evidence.</summary>
    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, TestContext.Current.CancellationToken));
    }
}

/// <summary>Retains observation handles while the actual job owner starts and reaps each media process.</summary>
internal sealed class ObservedMediaWorkers : IDisposable
{
    private readonly List<Process> workers = [];
    internal IEnumerable<int> Identifiers => workers.Select(process => process.Id);

    /// <summary>Checks previous exit at the next actual start, avoiding timing-based overlap claims.</summary>
    internal void Observe(int identifier)
    {
        Assert.All(workers, process => Assert.True(process.HasExited, "Media jobs overlapped."));
        var process = Process.GetProcessById(identifier);
        _ = process.SafeHandle;
        workers.Add(process);
    }

    /// <summary>Checks the retained exact process handles after the coordinator has returned.</summary>
    internal void AssertSequentialAndExited(int expected)
    {
        Assert.Equal(expected, workers.Count);
        Assert.All(workers, process => Assert.True(process.HasExited));
    }

    /// <summary>Closes observation handles without terminating any worker externally.</summary>
    public void Dispose()
    {
        foreach (var process in workers)
        {
            process.Dispose();
        }
    }
}
