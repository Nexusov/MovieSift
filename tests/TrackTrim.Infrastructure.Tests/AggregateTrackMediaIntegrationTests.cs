using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using TrackTrim.Application.Batches;
using TrackTrim.Application.Inspection;
using TrackTrim.Application.Jobs;
using TrackTrim.Core.Media;
using TrackTrim.Infrastructure.Files;
using TrackTrim.Infrastructure.MediaTools;
using TrackTrim.Infrastructure.Processes;
using Xunit;

namespace TrackTrim.Infrastructure.Tests;

/// <summary>Exercises aggregate editing and one explicit review against real differently ordered MKV inputs.</summary>
public sealed class AggregateTrackMediaIntegrationTests(ITestOutputHelper evidence)
{
    private const string SpanishTitle = "Español — extra episode audio";
    private const int MaximumDiagnosticsCharacters = 32768;
    private static readonly TimeSpan FixtureCreationTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Removes a last-file-only group while preserving the other input as a genuine no-change job.</summary>
    [MediaFact]
    public async Task ExtraSpanishGroupUsesItsOwnOccurrencesAndOneReviewPreservesNoChange()
    {
        using var workspace = new TestWorkspace();
        using var workers = new ObservedRemuxWorkers();
        var sourceAccess = new FileSourceAccess();
        var scratch = Path.Combine(workspace.Path, "scratch");
        var tools = MkvToolPaths.FromDirectory(MediaTestEnvironment.ToolDirectory) with { IsDevelopmentOverride = true };
        var backend = new MkvBackend(new MkvBackendOptions(sourceAccess, tools, scratch) { RemuxProcessStarted = workers.Observe });
        var firstSource = Path.Combine(workspace.Path, "Episode 1 исходник.mkv");
        var secondSource = Path.Combine(workspace.Path, "Episode 2 Español.mkv");
        File.Copy(Path.Combine(MediaTestEnvironment.FixtureDirectory, "ordinary.mkv"), firstSource);
        var template = await backend.InspectAsync(new(firstSource, TestContext.Current.CancellationToken));
        await CreateReorderedSpanishInputAsync(new(firstSource, secondSource, template));
        var sourceHashes = new Dictionary<string, string>
        {
            [firstSource] = await HashAsync(firstSource),
            [secondSource] = await HashAsync(secondSource)
        };
        var outputs = Path.Combine(workspace.Path, "outputs с пробелами");
        var data = Path.Combine(workspace.Path, "data");
        Directory.CreateDirectory(outputs);
        var session = new BatchSession([
            await sourceAccess.ReadRevisionAsync(new(firstSource, TestContext.Current.CancellationToken)),
            await sourceAccess.ReadRevisionAsync(new(secondSource, TestContext.Current.CancellationToken))]);
        var coordinator = new BatchCoordinator(session, new InspectionService(backend, sourceAccess),
            new JobCoordinator(backend, sourceAccess, new OwnedOutputStore(data)), sourceAccess);
        var inspected = await coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        Assert.All(inspected.Items, item => Assert.Equal(BatchItemState.NeedsReview, item.State));
        var first = Assert.Single(inspected.Items, item => item.SourcePath == firstSource);
        var second = Assert.Single(inspected.Items, item => item.SourcePath == secondSource);
        var firstInspection = Assert.IsType<MediaInspection>(first.Inspection);
        var secondInspection = Assert.IsType<MediaInspection>(second.Inspection);
        var spanish = Assert.Single(secondInspection.Tracks, track => track.Kind == TrackKind.Audio && track.Language == "spa");
        var firstVideo = Assert.Single(firstInspection.Tracks, track => track.Kind == TrackKind.Video);
        Assert.Equal(firstVideo.Id, spanish.Id);
        Assert.Equal(SpanishTitle, spanish.Title);
        Assert.NotEqual(firstInspection.Tracks.Select(track => track.Uid), secondInspection.Tracks.Select(track => track.Uid));
        Assert.Equal(5, secondInspection.Tracks.Length);

        foreach (var item in inspected.Items)
        {
            var destination = Path.Combine(outputs, Path.GetFileNameWithoutExtension(item.SourcePath) + ".trimmed.mkv");
            Assert.True(session.SetDestination(new(item.Id, destination)).Applied);
        }

        var projection = session.CreateTrackProjection();
        Assert.Equal(2, projection.IncludedFileCount);
        Assert.Equal(0, projection.UnresolvedFileCount);
        var spanishGroup = Assert.Single(projection.Groups, group =>
            group.Members.Any(member => member.ItemId == second.Id && member.Track.Id == spanish.Id));
        Assert.Equal(1, spanishGroup.FileCoverage);
        Assert.Equal(1, spanishGroup.OccurrenceCount);
        Assert.True(spanishGroup.Keep);
        Assert.DoesNotContain(spanishGroup.Members, member => member.ItemId == first.Id);
        var changed = session.SetTrackSelection(new(projection,
            spanishGroup.Members.Where(member => member.IsEditable).ToImmutableArray(), false));
        Assert.True(changed.Applied, changed.Failure?.ToString());
        var afterSelection = session.Snapshot;
        Assert.Equal(firstInspection.Tracks.Select(track => track.Id),
            Assert.Single(afterSelection.Items, item => item.Id == first.Id).CurrentRetainedTrackIds);
        Assert.Equal(secondInspection.Tracks.Where(track => track.Id != spanish.Id).Select(track => track.Id),
            Assert.Single(afterSelection.Items, item => item.Id == second.Id).CurrentRetainedTrackIds);
        Assert.All(afterSelection.Items, item => Assert.Null(item.ApprovedPlan));
        Assert.Null(afterSelection.Review);

        var reviewed = await coordinator.ReviewSelectionsAsync(new(afterSelection.Revision, [], TestContext.Current.CancellationToken));
        Assert.Null(reviewed.Failure);
        Assert.Empty(reviewed.ItemFailures);
        var review = Assert.IsType<BatchReview>(reviewed.Review);
        Assert.Equal(2, review.Items.Length);
        var unchangedPlan = Assert.Single(review.Items, item => item.ItemId == first.Id).Plan;
        var changedPlan = Assert.Single(review.Items, item => item.ItemId == second.Id).Plan;
        Assert.True(unchangedPlan.IsNoChange);
        Assert.False(changedPlan.IsNoChange);
        Assert.Equal(secondInspection.Tracks.Where(track => track.Id != spanish.Id).Select(track => track.Uid),
            changedPlan.RetainedTracks.Select(track => track.Uid));
        var completed = await coordinator.RunAsync(new(review.Id, CancellationToken: TestContext.Current.CancellationToken));
        Assert.True(completed.Phase == BatchPhase.Completed,
            string.Join(Environment.NewLine, completed.Items.Select(item => $"{item.State}: {item.Failure}; {item.Result?.Verification}")));
        var unchangedItem = Assert.Single(completed.Items, item => item.Id == first.Id);
        Assert.Equal(BatchItemState.NoChange, unchangedItem.State);
        Assert.Equal(JobPhase.NoChange, unchangedItem.Result?.Phase);
        Assert.Null(unchangedItem.Result?.Tool);
        Assert.False(File.Exists(unchangedPlan.DestinationPath));
        var published = Assert.Single(completed.Items, item => item.Id == second.Id);
        Assert.Equal(BatchItemState.Completed, published.State);
        var result = Assert.IsType<JobResult>(published.Result);
        Assert.Null(result.Failure);
        Assert.Empty(result.CleanupIssues);
        Assert.Equal(changedPlan.DestinationPath, result.PublishedPath);
        var verified = await backend.VerifyAsync(new(changedPlan, changedPlan.DestinationPath, TestContext.Current.CancellationToken));
        Assert.True(verified.Passed, verified.Details);
        var output = await backend.InspectAsync(new(changedPlan.DestinationPath, TestContext.Current.CancellationToken));
        Assert.Equal(changedPlan.RetainedTracks.Select(track => track.Uid), output.Tracks.Select(track => track.Uid));
        var payloads = new List<ComparedPayload>();

        foreach (var retained in changedPlan.RetainedTracks)
        {
            var outputTrack = Assert.Single(output.Tracks, track => track.Uid == retained.Uid);
            payloads.Add(await MediaPayloadEvidence.AssertIdenticalAsync(new(secondSource, retained.Id,
                changedPlan.DestinationPath, outputTrack.Id, workspace.Path,
                FormattableString.Invariant($"aggregate-{retained.Id}"))));
        }

        Assert.Equal(4, payloads.Count);
        Assert.Equal(secondInspection.Attachments.ToArray(), output.Attachments.ToArray());

        foreach (var attachment in secondInspection.Attachments)
        {
            var outputAttachment = Assert.Single(output.Attachments, candidate => candidate.Uid == attachment.Uid);
            await MediaPayloadEvidence.AssertIdenticalAsync(new(secondSource, attachment.Id, changedPlan.DestinationPath,
                outputAttachment.Id, workspace.Path, FormattableString.Invariant($"attachment-{attachment.Id}"), "attachments"));
        }

        foreach (var source in sourceHashes)
        {
            Assert.Equal(source.Value, await HashAsync(source.Key));
        }

        Assert.Single(Directory.GetFiles(outputs));
        Assert.Empty(Directory.GetDirectories(outputs));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(data, "ownership")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
        var exitedWorker = workers.AssertSingleExited();
        evidence.WriteLine(JsonSerializer.Serialize(new
        {
            kind = "real-unified-aggregate-selection",
            groupCoverage = spanishGroup.FileCoverage,
            includedFiles = projection.IncludedFileCount,
            groupOccurrences = spanishGroup.OccurrenceCount,
            removedSourceTrackId = spanish.Id,
            protectedOtherFileVideoId = firstVideo.Id,
            aggregateReviewId = review.Id,
            immutablePlans = review.Items.Length,
            publishedOutputs = 1,
            unchangedJobs = 1,
            retainedTrackPayloads = payloads.Count,
            retainedAttachmentPayloads = secondInspection.Attachments.Length,
            preservationVerified = verified.Passed,
            sourceHashes = sourceHashes.ToDictionary(source => Path.GetFileName(source.Key), source => source.Value),
            sourcesUnchanged = true,
            ownedRemuxWorker = exitedWorker,
            sameWorkerHandleExited = true,
            candidateDirectoriesRemaining = 0,
            ownershipRecordsRemaining = 0,
            scratchEntriesRemaining = 0
        }));
    }

    /// <summary>Adds real Spanish audio before the permuted original tracks using the pinned MKVToolNix.</summary>
    private static async Task CreateReorderedSpanishInputAsync(SpanishFixtureRequest request)
    {
        var audio = request.Template.Tracks.Where(track => track.Kind == TrackKind.Audio).ToArray();
        var video = Assert.Single(request.Template.Tracks, track => track.Kind == TrackKind.Video);
        var subtitle = Assert.Single(request.Template.Tracks, track => track.Kind == TrackKind.Subtitles);
        Assert.Equal(2, audio.Length);
        var orderedTracks = new[] { "1:0", $"0:{subtitle.Id}", $"0:{audio[1].Id}", $"0:{video.Id}", $"0:{audio[0].Id}" };
        var tools = MkvToolPaths.FromDirectory(MediaTestEnvironment.ToolDirectory);
        var environment = new Dictionary<string, string>();

        if (tools.NativeLibraryDirectory is { } libraries)
        {
            environment["LD_LIBRARY_PATH"] = libraries;
        }

        if (OperatingSystem.IsLinux())
        {
            environment["LC_ALL"] = "C.UTF-8";
            environment["LANG"] = "C.UTF-8";
        }

        var created = await new OwnedProcessRunner().RunAsync(new(tools.MkvMerge,
            ["--disable-track-statistics-tags", "--output", request.Destination,
                "--track-order", string.Join(',', orderedTracks), request.Source,
                "--language", "0:spa", "--track-name", "0:" + SpanishTitle,
                "--default-track-flag", "0:0", Path.Combine(MediaTestEnvironment.FixtureDirectory, "low.wav")],
            MaximumDiagnosticsCharacters, FixtureCreationTimeout, null, TestContext.Current.CancellationToken, environment));
        Assert.True(created.ExitCode == 0, created.StandardOutput + created.StandardError);
    }

    /// <summary>Hashes full source containers without decoding or modifying their bytes.</summary>
    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, TestContext.Current.CancellationToken));
    }

    /// <summary>Retains actual remux handles so NoChange cannot hide an unnecessary second worker.</summary>
    private sealed class ObservedRemuxWorkers : IDisposable
    {
        private readonly List<Process> processes = [];

        /// <summary>Retains the runner's actual process identity at its start notification.</summary>
        public void Observe(int identifier)
        {
            var process = Process.GetProcessById(identifier);
            _ = process.SafeHandle;
            processes.Add(process);
        }

        /// <summary>Requires exactly one remux for the changed file, already reaped by the production owner.</summary>
        public int AssertSingleExited()
        {
            var process = Assert.Single(processes);
            Assert.True(process.HasExited);
            return process.Id;
        }

        /// <summary>Releases observation handles without controlling any process.</summary>
        public void Dispose()
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private sealed record SpanishFixtureRequest(string Source, string Destination, MediaInspection Template);
}
