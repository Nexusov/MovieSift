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
using MovieSift.Core.Rules;
using MovieSift.Infrastructure.Files;
using MovieSift.Infrastructure.MediaTools;
using Xunit;

namespace MovieSift.Infrastructure.Tests;

/// <summary>Uses actual forced flags to retain different subtitle content under reordered source-local identities.</summary>
public sealed class BatchForcedSubtitleIntegrationTests(ITestOutputHelper evidence)
{
    /// <summary>Applies language/title/forced conjunction and verifies eight retained payloads plus attachments and cleanup.</summary>
    [MediaFact]
    public async Task ForcedSubtitlePreviewKeepsDifferentPayloadsAcrossReorderedInputs()
    {
        using var workspace = new TestWorkspace();
        using var workers = new ObservedRemuxWorkers();
        var access = new FileSourceAccess();
        var tools = MkvToolPaths.FromDirectory(MediaTestEnvironment.ToolDirectory) with { IsDevelopmentOverride = true };
        var scratch = Path.Combine(workspace.Path, "scratch");
        var backend = new MkvBackend(new MkvBackendOptions(access, tools, scratch) { RemuxProcessStarted = workers.Observe });
        var templatePath = Path.Combine(MediaTestEnvironment.FixtureDirectory, "ordinary.mkv");
        var template = await backend.InspectAsync(new(templatePath, TestContext.Current.CancellationToken));
        Assert.Single(template.Attachments);
        var originalSubtitleUid = Assert.Single(template.Tracks.Where(track => track.Kind == TrackKind.Subtitles)).Uid;
        var fixtures = await ForcedSubtitleFixture.CreateAsync(new(templatePath, workspace.Path));
        var hashes = new Dictionary<string, string> { [fixtures.First] = await HashAsync(fixtures.First), [fixtures.Second] = await HashAsync(fixtures.Second) };
        var session = new BatchSession([await access.ReadRevisionAsync(new(fixtures.First, TestContext.Current.CancellationToken)),
            await access.ReadRevisionAsync(new(fixtures.Second, TestContext.Current.CancellationToken))]);
        var data = Path.Combine(workspace.Path, "data");
        var outputs = Path.Combine(workspace.Path, "results");
        Directory.CreateDirectory(outputs);
        var coordinator = new BatchCoordinator(session, new InspectionService(backend, access),
            new JobCoordinator(backend, access, new OwnedOutputStore(data)), access);
        var inspected = await coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        var firstInspection = Assert.IsType<MediaInspection>(Assert.Single(inspected.Items, item => item.SourcePath == fixtures.First).Inspection);
        var firstSubtitles = firstInspection.Tracks.Where(track => track.Kind == TrackKind.Subtitles).ToArray();
        Assert.Equal(2, firstSubtitles.Length);
        Assert.Equal([3, 4], firstSubtitles.Select(track => track.Id));
        var addedSubtitleUid = Assert.Single(firstSubtitles, track => track.Uid != originalSubtitleUid).Uid;

        foreach (var item in inspected.Items)
        {
            var subtitles = item.Inspection!.Tracks.Where(track => track.Kind == TrackKind.Subtitles).ToArray();
            Assert.Equal(2, subtitles.Length);
            Assert.All(subtitles, track =>
            {
                Assert.Equal("eng", track.Language);
                Assert.Equal("en", track.LanguageIetf);
                Assert.Equal(ForcedSubtitleFixture.SubtitleTitle, track.Title);
            });
            var forced = Assert.Single(subtitles, track => track.IsForced);
            var isFirst = item.SourcePath == fixtures.First;
            Assert.Equal(isFirst ? originalSubtitleUid : addedSubtitleUid, forced.Uid);
            Assert.Equal(isFirst ? 3 : 0, forced.Id);
            Assert.Single(subtitles, track => !track.IsForced);
        }

        var rules = new LanguageRules(new(LanguageRuleMode.Unchanged, []),
            new(LanguageRuleMode.KeepMatching, ["eng"], new TrackTitleFilter(true, ForcedSubtitleFixture.SubtitleTitle)),
            KeepOnlyForcedSubtitles: true);
        var proposed = coordinator.PreviewLanguageRules(rules);
        Assert.Null(proposed.Failure);
        var preview = Assert.IsType<BatchLanguagePreview>(proposed.Preview);
        var applied = coordinator.ApplyLanguageRules(preview);
        Assert.True(applied.Applied, applied.Failure?.ToString());
        Assert.Equal(2, applied.ChangedItemIds.Length);
        var plans = new Dictionary<Guid, RemuxPlan>();

        foreach (var item in session.Snapshot.Items)
        {
            var inspection = item.Inspection!;
            Assert.Equal(inspection.Tracks.Where(track => track.Kind != TrackKind.Subtitles || track.IsForced).Select(track => track.Id), item.CurrentRetainedTrackIds);
            Assert.Equal(Assert.Single(preview.Items, proposal => proposal.ItemId == item.Id).ProposedRetainedTrackIds, item.CurrentRetainedTrackIds);
            var planned = PlanBuilder.Build(new(inspection, item.CurrentRetainedTrackIds,
                Path.Combine(outputs, Path.GetFileNameWithoutExtension(item.SourcePath) + ".trimmed.mkv"), false));
            var plan = Assert.IsType<RemuxPlan>(planned.Plan);
            Assert.False(plan.IsNoChange);
            Assert.True(session.ApprovePlan(new(item.Id, plan)).Applied);
            plans.Add(item.Id, plan);
        }

        var reviewed = await coordinator.ReviewAsync(TestContext.Current.CancellationToken);
        Assert.Null(reviewed.Failure);
        Assert.Empty(reviewed.ItemFailures);
        var review = Assert.IsType<BatchReview>(reviewed.Review);
        var completed = await coordinator.RunAsync(new(review.Id, CancellationToken: TestContext.Current.CancellationToken));
        Assert.True(completed.Phase == BatchPhase.Completed,
            string.Join(Environment.NewLine, completed.Items.Select(item => $"{item.State}: {item.Failure}; {item.Result?.Verification}")));
        var subtitlePayloads = new List<string>();
        var comparedTrackPayloads = 0;

        foreach (var item in completed.Items)
        {
            var result = Assert.IsType<JobResult>(item.Result);
            Assert.Equal(JobPhase.Completed, result.Phase);
            Assert.Null(result.Failure);
            Assert.Empty(result.CleanupIssues);
            var plan = plans[item.Id];
            Assert.Equal(plan.DestinationPath, result.PublishedPath);
            var verified = await backend.VerifyAsync(new(plan, plan.DestinationPath, TestContext.Current.CancellationToken));
            Assert.True(verified.Passed, verified.Details);
            var output = await backend.InspectAsync(new(plan.DestinationPath, TestContext.Current.CancellationToken));
            Assert.Equal(plan.RetainedTracks.Select(track => track.Uid), output.Tracks.Select(track => track.Uid));
            Assert.True(Assert.Single(output.Tracks.Where(track => track.Kind == TrackKind.Subtitles)).IsForced);
            var payloads = new List<TrackPayloadEvidence>();

            foreach (var retained in plan.RetainedTracks)
            {
                var outputTrack = Assert.Single(output.Tracks, track => track.Uid == retained.Uid);
                var compared = await MediaPayloadEvidence.AssertIdenticalAsync(new(item.SourcePath, retained.Id, plan.DestinationPath,
                    outputTrack.Id, workspace.Path, FormattableString.Invariant($"forced-{item.Id:N}-{retained.Id}")));
                payloads.Add(new(retained.Id, outputTrack.Id, retained.Uid!.Value.ToString(CultureInfo.InvariantCulture), retained.Kind.ToString(), compared));
                comparedTrackPayloads++;

                if (retained.Kind == TrackKind.Subtitles)
                {
                    subtitlePayloads.Add(compared.SourceSha256);
                }
            }

            Assert.Equal(plan.Inspection.Attachments.ToArray(), output.Attachments.ToArray());
            var attachments = new List<ComparedPayload>();

            foreach (var attachment in plan.Inspection.Attachments)
            {
                var outputAttachment = Assert.Single(output.Attachments, candidate => candidate.Uid == attachment.Uid);
                attachments.Add(await MediaPayloadEvidence.AssertIdenticalAsync(new(item.SourcePath, attachment.Id,
                    plan.DestinationPath, outputAttachment.Id, workspace.Path, $"attachment-{item.Id:N}", "attachments")));
            }

            var sourceAfter = await HashAsync(item.SourcePath);
            Assert.Equal(hashes[item.SourcePath], sourceAfter);
            evidence.WriteLine(JsonSerializer.Serialize(new
            {
                kind = "real-forced-subtitle-rule",
                source = Path.GetFileName(item.SourcePath),
                sourceHashBefore = hashes[item.SourcePath],
                sourceHashAfter = sourceAfter,
                output = Path.GetFileName(plan.DestinationPath),
                outputHash = await HashAsync(plan.DestinationPath),
                previewId = preview.Id,
                appliedIds = Assert.Single(preview.Items, proposal => proposal.ItemId == item.Id).ProposedRetainedTrackIds,
                subtitleInputs = plan.Inspection.Tracks.Where(track => track.Kind == TrackKind.Subtitles).Select(track => new
                {
                    track.Id,
                    uid = track.Uid!.Value.ToString(CultureInfo.InvariantCulture),
                    track.Language,
                    track.LanguageIetf,
                    track.Title,
                    track.IsForced
                }),
                payloads,
                attachments,
                metadataAndTimestampsVerified = verified.Passed,
                cleanupIssues = result.CleanupIssues.Length
            }));
        }

        Assert.Equal(2, completed.Items.Length);
        Assert.Equal(8, comparedTrackPayloads);
        Assert.Equal(2, subtitlePayloads.Count);
        Assert.NotEqual(subtitlePayloads[0], subtitlePayloads[1]);
        Assert.Equal(2, Directory.GetFiles(outputs, "*.mkv").Length);
        Assert.Empty(Directory.GetDirectories(outputs));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(data, "ownership")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
        var exitedWorkerIds = workers.AssertExited();
        evidence.WriteLine(JsonSerializer.Serialize(new
        {
            kind = "real-forced-subtitle-rule-summary",
            retainedTrackPayloads = comparedTrackPayloads,
            retainedAttachmentPayloads = completed.Items.Sum(item => plans[item.Id].Inspection.Attachments.Length),
            distinctRetainedSubtitlePayloads = subtitlePayloads.Distinct(StringComparer.Ordinal).Count(),
            ownedWorkerIds = exitedWorkerIds,
            safeHandlesRetained = true,
            allOwnedWorkersExited = true,
            candidateDirectoriesRemaining = 0,
            ownershipRecordsRemaining = 0,
            scratchEntriesRemaining = 0
        }));
    }

    /// <summary>Hashes complete files through read-only handles.</summary>
    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, TestContext.Current.CancellationToken));
    }

    /// <summary>Retains the same native process identities without inferring ownership from later PID lookups.</summary>
    private sealed class ObservedRemuxWorkers : IDisposable
    {
        private readonly List<Process> processes = [];

        /// <summary>Pins a remux process handle at the existing runner's start notification.</summary>
        public void Observe(int identifier)
        {
            var process = Process.GetProcessById(identifier);
            _ = process.SafeHandle;
            processes.Add(process);
        }

        /// <summary>Requires both owned remux handles to have exited after all queue work and verification returns.</summary>
        public int[] AssertExited()
        {
            Assert.Equal(2, processes.Count);
            Assert.All(processes, process => Assert.True(process.HasExited));
            return processes.Select(process => process.Id).ToArray();
        }

        /// <summary>Releases retained observation handles without killing or restarting any worker.</summary>
        public void Dispose()
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private sealed record TrackPayloadEvidence(int SourceId, int OutputId, string Uid, string Kind, ComparedPayload Payload);
}
