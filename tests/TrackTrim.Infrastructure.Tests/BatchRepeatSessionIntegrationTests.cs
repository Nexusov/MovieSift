using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using TrackTrim.Application.Batches;
using TrackTrim.Application.Inspection;
using TrackTrim.Application.Jobs;
using TrackTrim.Application.Ports;
using TrackTrim.Core.Media;
using TrackTrim.Core.Planning;
using TrackTrim.Infrastructure.Files;
using Xunit;

namespace TrackTrim.Infrastructure.Tests;

/// <summary>Reuses the same batch owner for independently reviewed real outputs from one unchanged source.</summary>
public sealed class BatchRepeatSessionIntegrationTests(ITestOutputHelper evidence)
{
    private const int SessionCount = 2;

    /// <summary>Starts a fresh batch after publication and keeps the earlier output while approving the same source again.</summary>
    [MediaFact]
    public async Task SameSourceCanPublishAgainAfterExplicitNewBatch()
    {
        using var workspace = new TestWorkspace();
        var sourcePath = Path.Combine(workspace.Path, "Повторная серия с пробелами.mkv");
        File.Copy(Path.Combine(MediaTestEnvironment.FixtureDirectory, "ordinary.mkv"), sourcePath);
        var sourceHashBefore = await HashAsync(sourcePath);
        var scratchDirectory = Path.Combine(workspace.Path, "scratch");
        var backend = MediaTestEnvironment.CreateBackend(scratchDirectory);
        var sourceAccess = new FileSourceAccess();
        var discovery = new FileBatchInputDiscovery(sourceAccess);
        var session = new BatchSession();
        var dataDirectory = Path.Combine(workspace.Path, "data");
        var outputDirectory = Path.Combine(workspace.Path, "results");
        Directory.CreateDirectory(outputDirectory);
        var coordinator = new BatchCoordinator(session, new InspectionService(backend, sourceAccess),
            new JobCoordinator(backend, sourceAccess, new OwnedOutputStore(dataDirectory)), sourceAccess);
        CompletedSessionCheckpoint? previous = null;

        foreach (var sessionNumber in Enumerable.Range(1, SessionCount))
        {
            if (previous is not null)
            {
                var reset = coordinator.StartNewBatch();
                Assert.True(reset.Applied, reset.Failure?.ToString());
                Assert.Null(reset.Failure);
                Assert.Empty(session.Snapshot.Items);
                Assert.Equal(BatchPhase.Preparing, session.Snapshot.Phase);
                Assert.Null(session.Snapshot.Review);
                Assert.Null(session.Snapshot.Failure);
                Assert.False(session.Snapshot.HasSafetyBlock);
                Assert.Equal(previous.OutputHash, await HashAsync(previous.OutputPath));
            }

            var discovered = await discovery.DiscoverAsync(new(BatchInputKind.ExplicitFiles,
                [sourcePath], TestContext.Current.CancellationToken));
            Assert.Null(discovered.Rejection);
            Assert.Empty(discovered.Issues);
            Assert.Equal(sourcePath, Assert.Single(discovered.Sources).FullPath);
            var addition = session.AddDiscoveryResult(discovered);
            Assert.True(addition.Applied, addition.Failure?.ToString());
            Assert.Equal(BatchItemState.PendingInspection, Assert.Single(session.Snapshot.Items).State);
            var inspected = await coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
            var item = Assert.Single(inspected.Items);
            Assert.Equal(BatchItemState.NeedsReview, item.State);
            var inspection = Assert.IsType<MediaInspection>(item.Inspection);

            if (previous is not null)
            {
                Assert.NotEqual(previous.ItemId, item.Id);
                Assert.NotSame(previous.Inspection, inspection);
                Assert.Equal(previous.Inspection.Source, inspection.Source);
                Assert.Null(item.ApprovedPlan);
                Assert.Null(item.Result);
            }

            var audioTracks = inspection.Tracks.Where(track => track.Kind == TrackKind.Audio).ToArray();
            Assert.Equal(2, audioTracks.Length);
            var isFirstSession = previous is null;
            var approvedAudio = audioTracks[isFirstSession ? 1 : 0];
            var retainedIds = inspection.Tracks.Where(track =>
            {
                var keepSubtitles = isFirstSession && track.Kind == TrackKind.Subtitles;
                var keepVideoOrApprovedAudio = track.Kind == TrackKind.Video || track.Id == approvedAudio.Id;
                return keepVideoOrApprovedAudio || keepSubtitles;
            }).Select(track => track.Id).ToArray();
            var destination = Path.Combine(outputDirectory, FormattableString.Invariant($"session-{sessionNumber}.trimmed.mkv"));
            var planResult = PlanBuilder.Build(new(inspection, retainedIds, destination, false));
            Assert.Null(planResult.Failure);
            var plan = Assert.IsType<RemuxPlan>(planResult.Plan);
            Assert.True(session.ApprovePlan(new(item.Id, plan)).Applied);
            var reviewed = await coordinator.ReviewAsync(TestContext.Current.CancellationToken);
            Assert.Null(reviewed.Failure);
            Assert.Empty(reviewed.ItemFailures);
            var review = Assert.IsType<BatchReview>(reviewed.Review);

            if (previous is not null)
            {
                Assert.NotEqual(previous.PlanId, plan.Id);
                Assert.NotEqual(previous.ReviewId, review.Id);
                Assert.NotEqual(previous.OutputPath, destination);
            }

            var completed = await coordinator.RunAsync(new(review.Id, CancellationToken: TestContext.Current.CancellationToken));
            var completedItem = Assert.Single(completed.Items);
            var result = Assert.IsType<JobResult>(completedItem.Result);
            Assert.True(completed.Phase == BatchPhase.Completed, $"{completed.PauseReason}: {result.Failure}; {result.Tool}; {result.Verification}");
            Assert.Equal(BatchItemState.Completed, completedItem.State);
            Assert.Equal(JobPhase.Completed, result.Phase);
            Assert.Equal(destination, result.PublishedPath);
            Assert.Null(result.Failure);
            Assert.Empty(result.CleanupIssues);
            var tool = Assert.IsType<ToolOutcome>(result.Tool);
            Assert.Equal(0, tool.ExitCode);
            var initialVerification = Assert.IsType<VerificationOutcome>(result.Verification);
            Assert.True(initialVerification.Passed, initialVerification.Details);
            var verification = await backend.VerifyAsync(new(plan, destination, TestContext.Current.CancellationToken));
            Assert.True(verification.Passed, verification.Details);
            var output = await backend.InspectAsync(new(destination, TestContext.Current.CancellationToken));
            Assert.Equal(plan.RetainedTracks.Select(track => track.Uid), output.Tracks.Select(track => track.Uid));

            foreach (var retained in plan.RetainedTracks)
            {
                var outputTrack = Assert.Single(output.Tracks, track => track.Uid == retained.Uid);
                await MediaPayloadEvidence.AssertIdenticalAsync(new(sourcePath, retained.Id, destination,
                    outputTrack.Id, workspace.Path, FormattableString.Invariant($"session-{sessionNumber}-track-{retained.Id}")));
            }

            var sourceHashAfter = await HashAsync(sourcePath);
            Assert.Equal(sourceHashBefore, sourceHashAfter);
            var outputHash = await HashAsync(destination);
            var previousOutputHashAfter = previous is null ? null : await HashAsync(previous.OutputPath);

            if (previous is not null)
            {
                Assert.Equal(previous.OutputHash, previousOutputHashAfter);
            }

            Assert.Empty(Directory.GetDirectories(outputDirectory));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(dataDirectory, "ownership")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(scratchDirectory));
            evidence.WriteLine(JsonSerializer.Serialize(new
            {
                kind = "real-repeat-batch-session",
                sessionNumber,
                resetApplied = previous is not null,
                source = Path.GetFileName(sourcePath),
                sourceHashBefore,
                sourceHashAfter,
                output = Path.GetFileName(destination),
                outputHash,
                previousOutputHashBefore = previous?.OutputHash,
                previousOutputHashAfter,
                retained = plan.RetainedTracks.Select(track => new
                {
                    uid = track.Uid!.Value.ToString(CultureInfo.InvariantCulture),
                    sourceTrackId = track.Id,
                    outputTrackId = output.Tracks.Single(candidate => candidate.Uid == track.Uid).Id,
                    kind = track.Kind.ToString(),
                    payloadCompared = true
                }),
                toolVersion = inspection.Backend.Version,
                toolExitCode = tool.ExitCode,
                metadataVerifiedAfterPublication = verification.Passed,
                cleanupIssues = result.CleanupIssues.Length,
                ownershipAndScratchEmpty = true
            }));
            previous = new(item.Id, plan.Id, review.Id, inspection, destination, outputHash);
        }

        Assert.Equal(SessionCount, Directory.GetFiles(outputDirectory, "*.mkv").Length);
        evidence.WriteLine("Real MKVToolNix repeat batch: the same source was freshly inspected and independently approved twice; both published payloads verified, the first output remained unchanged, and owned candidates/records/scratch were absent.");
    }

    /// <summary>Hashes the complete source or published output without modifying its bytes or path.</summary>
    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, TestContext.Current.CancellationToken));
    }

    private sealed record CompletedSessionCheckpoint(Guid ItemId, Guid PlanId, Guid ReviewId,
        MediaInspection Inspection, string OutputPath, string OutputHash);
}
