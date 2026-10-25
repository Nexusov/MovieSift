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
using MovieSift.Infrastructure.Processes;
using Xunit;

namespace MovieSift.Infrastructure.Tests;

/// <summary>Exercises language-rule preview and application with real reordered, independently identified media.</summary>
public sealed class BatchLanguageRuleIntegrationTests(ITestOutputHelper evidence)
{
    private const int MaximumDiagnosticsCharacters = 32768;
    private const int ExpectedRetainedPayloadCount = 5;
    private static readonly TimeSpan FixtureCreationTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Keeps every English match per file after applying a preview, then verifies both real publications.</summary>
    [MediaFact]
    public async Task AppliedLanguagePreviewRetainsAllLocalEnglishMatchesAcrossReorderedInputs()
    {
        using var workspace = new TestWorkspace();
        var firstSource = Path.Combine(workspace.Path, "A English audio русские subtitles.mkv");
        var secondSource = Path.Combine(workspace.Path, "B перестановка English French audio.mkv");
        File.Copy(Path.Combine(MediaTestEnvironment.FixtureDirectory, "ordinary.mkv"), firstSource);
        var scratchDirectory = Path.Combine(workspace.Path, "scratch");
        var backend = MediaTestEnvironment.CreateBackend(scratchDirectory);
        var fixtureInspection = await backend.InspectAsync(new(firstSource, TestContext.Current.CancellationToken));
        var originalAudio = fixtureInspection.Tracks.Where(track => track.Kind == TrackKind.Audio).ToArray();
        Assert.Equal(2, originalAudio.Length);
        Assert.All(originalAudio, track => Assert.Equal("eng", track.Language));
        var originalVideo = Assert.Single(fixtureInspection.Tracks.Where(track => track.Kind == TrackKind.Video));
        var originalSubtitles = Assert.Single(fixtureInspection.Tracks.Where(track => track.Kind == TrackKind.Subtitles));
        Assert.Equal("rus", originalSubtitles.Language);
        var reorderedIds = new[] { originalSubtitles.Id, originalAudio[1].Id, originalVideo.Id, originalAudio[0].Id };
        await CreateReorderedFrenchInputAsync(new(firstSource, secondSource, reorderedIds, originalAudio[1].Id));
        var sourceHashes = new Dictionary<string, string>
        {
            [firstSource] = await HashAsync(firstSource),
            [secondSource] = await HashAsync(secondSource),
        };
        var sourceAccess = new FileSourceAccess();
        var discovered = await new FileBatchInputDiscovery(sourceAccess).DiscoverAsync(new(
            BatchInputKind.ExplicitFiles, [secondSource, firstSource], TestContext.Current.CancellationToken));
        Assert.Null(discovered.Rejection);
        Assert.Empty(discovered.Issues);
        Assert.Equal(2, discovered.Sources.Length);
        var session = new BatchSession(discovered.Sources);
        var dataDirectory = Path.Combine(workspace.Path, "data");
        var outputDirectory = Path.Combine(workspace.Path, "results");
        Directory.CreateDirectory(outputDirectory);
        var coordinator = new BatchCoordinator(session, new InspectionService(backend, sourceAccess),
            new JobCoordinator(backend, sourceAccess, new OwnedOutputStore(dataDirectory)), sourceAccess);
        var inspected = await coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        Assert.All(inspected.Items, item => Assert.Equal(BatchItemState.NeedsReview, item.State));
        var secondInspection = Assert.IsType<MediaInspection>(Assert.Single(inspected.Items, item => item.SourcePath == secondSource).Inspection);
        var expectedReorderedUids = reorderedIds.Select(identifier => fixtureInspection.Tracks.Single(track => track.Id == identifier).Uid);
        Assert.Equal(expectedReorderedUids, secondInspection.Tracks.Select(track => track.Uid));
        var changedFrenchAudio = Assert.Single(secondInspection.Tracks, track => track.Uid == originalAudio[1].Uid);
        Assert.Equal("fre", changedFrenchAudio.Language);
        Assert.Equal("fr", changedFrenchAudio.LanguageIetf);
        var remainingEnglishAudio = Assert.Single(secondInspection.Tracks, track => track.Uid == originalAudio[0].Uid);
        Assert.Equal("eng", remainingEnglishAudio.Language);
        Assert.NotEqual(originalAudio[0].Id, remainingEnglishAudio.Id);

        var rules = new LanguageRules(new(LanguageRuleMode.KeepMatching, ["eng"]),
            new(LanguageRuleMode.KeepMatching, ["eng"]));
        var previewResult = coordinator.PreviewLanguageRules(rules);
        Assert.Null(previewResult.Failure);
        var preview = Assert.IsType<BatchLanguagePreview>(previewResult.Preview);
        Assert.Equal(2, preview.Items.Length);
        Assert.All(preview.Items, item => Assert.True(item.NoSubtitleMatches));
        Assert.All(session.Snapshot.Items, item =>
            Assert.Equal(item.Inspection!.Tracks.Select(track => track.Id), item.CurrentRetainedTrackIds));
        var application = coordinator.ApplyLanguageRules(preview);
        Assert.True(application.Applied, application.Failure?.ToString());
        Assert.Null(application.Failure);
        Assert.Equal(2, application.ChangedItemIds.Length);
        var plans = new Dictionary<Guid, RemuxPlan>();

        foreach (var item in session.Snapshot.Items)
        {
            var inspection = Assert.IsType<MediaInspection>(item.Inspection);
            var expectedAudioUids = item.SourcePath == firstSource
                ? originalAudio.Select(track => track.Uid).ToArray() : [remainingEnglishAudio.Uid];
            var expectedRetained = inspection.Tracks.Where(track => track.Kind == TrackKind.Video
                || expectedAudioUids.Contains(track.Uid)).ToArray();
            Assert.Equal(expectedRetained.Select(track => track.Id), item.CurrentRetainedTrackIds);
            var previewItem = Assert.Single(preview.Items, candidate => candidate.ItemId == item.Id);
            Assert.Equal(previewItem.ProposedRetainedTrackIds, item.CurrentRetainedTrackIds);
            Assert.Null(item.ApprovedPlan);
            Assert.Null(item.Result);
            var destination = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(item.SourcePath) + ".trimmed.mkv");
            // The production-applied selection is the plan input; fixture expectations only assert its correctness.
            var planned = PlanBuilder.Build(new(inspection, item.CurrentRetainedTrackIds, destination, false));
            Assert.Null(planned.Failure);
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
        var completionEvidence = string.Join(Environment.NewLine, completed.Items.Select(item =>
            $"{Path.GetFileName(item.SourcePath)}: {item.State}; {item.Failure}; {item.Result?.Tool}; {item.Result?.Verification}"));
        Assert.True(completed.Phase == BatchPhase.Completed, completionEvidence);
        var retainedPayloadCount = 0;

        foreach (var item in completed.Items)
        {
            var result = Assert.IsType<JobResult>(item.Result);
            Assert.Equal(BatchItemState.Completed, item.State);
            Assert.Equal(JobPhase.Completed, result.Phase);
            Assert.Null(result.Failure);
            Assert.Empty(result.CleanupIssues);
            var tool = Assert.IsType<ToolOutcome>(result.Tool);
            Assert.Equal(0, tool.ExitCode);
            var plan = plans[item.Id];
            Assert.Equal(plan.DestinationPath, result.PublishedPath);
            var verification = await backend.VerifyAsync(new(plan, plan.DestinationPath, TestContext.Current.CancellationToken));
            Assert.True(verification.Passed, verification.Details);
            var output = await backend.InspectAsync(new(plan.DestinationPath, TestContext.Current.CancellationToken));
            Assert.Equal(plan.RetainedTracks.Select(track => track.Uid), output.Tracks.Select(track => track.Uid));
            Assert.DoesNotContain(output.Tracks, track => track.Kind == TrackKind.Subtitles);
            Assert.Equal(plan.Inspection.Attachments.ToArray(), output.Attachments.ToArray());

            foreach (var retained in plan.RetainedTracks)
            {
                var outputTrack = Assert.Single(output.Tracks, track => track.Uid == retained.Uid);
                await MediaPayloadEvidence.AssertIdenticalAsync(new(item.SourcePath, retained.Id, plan.DestinationPath,
                    outputTrack.Id, workspace.Path, FormattableString.Invariant($"language-rule-{item.Id:N}-{retained.Id}")));
                retainedPayloadCount++;
            }

            var sourceHashAfter = await HashAsync(item.SourcePath);
            Assert.Equal(sourceHashes[item.SourcePath], sourceHashAfter);
            var appliedPreviewItem = Assert.Single(preview.Items, candidate => candidate.ItemId == item.Id);
            evidence.WriteLine(JsonSerializer.Serialize(new
            {
                kind = "real-language-rule-batch-file",
                fixture = "MovieSift original synthetic ordinary.mkv with test-only reordered/French-language variant",
                toolVersion = plan.Inspection.Backend.Version,
                source = Path.GetFileName(item.SourcePath),
                sourceHashBefore = sourceHashes[item.SourcePath],
                sourceHashAfter,
                output = Path.GetFileName(plan.DestinationPath),
                outputHash = await HashAsync(plan.DestinationPath),
                rules = new { audio = "KeepMatching eng", subtitles = "KeepMatching eng" },
                previewId = preview.Id,
                previewRetainedIds = appliedPreviewItem.ProposedRetainedTrackIds,
                rawInputTracks = plan.Inspection.Tracks.Select(track => new
                {
                    track.Id,
                    uid = track.Uid!.Value.ToString(CultureInfo.InvariantCulture),
                    kind = track.Kind.ToString(),
                    track.Language,
                    track.LanguageIetf
                }),
                retained = plan.RetainedTracks.Select(track => new
                {
                    sourceTrackId = track.Id,
                    outputTrackId = output.Tracks.Single(candidate => candidate.Uid == track.Uid).Id,
                    uid = track.Uid!.Value.ToString(CultureInfo.InvariantCulture),
                    payloadCompared = true
                }),
                metadataAndTimestampsVerified = verification.Passed,
                attachmentMetadataCompared = true,
                toolExitCode = tool.ExitCode,
                cleanupIssues = result.CleanupIssues.Length
            }));
        }

        Assert.Equal(ExpectedRetainedPayloadCount, retainedPayloadCount);
        Assert.Equal(2, Directory.GetFiles(outputDirectory, "*.mkv").Length);
        Assert.Empty(Directory.GetDirectories(outputDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(dataDirectory, "ownership")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratchDirectory));
        evidence.WriteLine("Real language-rule preview/apply: two independently resolved publications, five retained payloads compared, unchanged originals and no owned candidate/record/scratch leftovers.");
    }

    /// <summary>Creates a pinned test-only permutation and changes the original second audio's language before input indexing changes.</summary>
    private static async Task CreateReorderedFrenchInputAsync(ReorderedFrenchInputRequest request)
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
        var frenchLanguage = FormattableString.Invariant($"{request.FrenchAudioSourceId}:fre");
        var result = await new OwnedProcessRunner().RunAsync(new(tools.MkvMerge,
            ["--disable-track-statistics-tags", "--normalize-language-ietf", "off", "--output", request.DestinationPath,
                "--track-order", order, "--language", frenchLanguage, request.SourcePath],
            MaximumDiagnosticsCharacters, FixtureCreationTimeout, null, TestContext.Current.CancellationToken, environment));
        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
    }

    /// <summary>Hashes the complete source or output using a read-only handle.</summary>
    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, TestContext.Current.CancellationToken));
    }

    private sealed record ReorderedFrenchInputRequest(string SourcePath, string DestinationPath,
        IReadOnlyList<int> OrderedIds, int FrenchAudioSourceId);
}
