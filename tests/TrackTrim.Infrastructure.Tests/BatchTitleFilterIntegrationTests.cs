using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using TrackTrim.Application.Batches;
using TrackTrim.Application.Inspection;
using TrackTrim.Application.Jobs;
using TrackTrim.Application.Ports;
using TrackTrim.Core.Media;
using TrackTrim.Core.Planning;
using TrackTrim.Core.Rules;
using TrackTrim.Infrastructure.Files;
using TrackTrim.Infrastructure.MediaTools;
using TrackTrim.Infrastructure.Processes;
using Xunit;

namespace TrackTrim.Infrastructure.Tests;

/// <summary>Checks literal title filtering and language conjunction against independently resolved real media.</summary>
public sealed class BatchTitleFilterIntegrationTests(ITestOutputHelper evidence)
{
    private const string FilterText = "main [cut].*";
    private const string LiteralTitle = "Main [cut].*";
    private const string RegexOnlyTitle = "Main cut anything";
    private const string EnglishTitle = "Main [cut].* English";
    private const string FrenchTitle = "Main [cut].* Français";
    private const int MaximumDiagnosticsCharacters = 32768;
    private static readonly TimeSpan FixtureCreationTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Applies actual preview choices so title metacharacters remain literal and language/title must both match.</summary>
    [MediaFact]
    public async Task LiteralTitleAndLanguagePreviewPublishesTheIntendedReorderedAudio()
    {
        using var workspace = new TestWorkspace();
        var scratch = Path.Combine(workspace.Path, "scratch");
        var backend = MediaTestEnvironment.CreateBackend(scratch);
        var templatePath = Path.Combine(MediaTestEnvironment.FixtureDirectory, "ordinary.mkv");
        var template = await backend.InspectAsync(new(templatePath, TestContext.Current.CancellationToken));
        var originalAudio = template.Tracks.Where(track => track.Kind == TrackKind.Audio).ToArray();
        Assert.Equal(2, originalAudio.Length);
        var first = Path.Combine(workspace.Path, "A literal название.mkv");
        var second = Path.Combine(workspace.Path, "B reordered название.mkv");
        await CreateFixtureAsync(new(templatePath, first, template, false));
        await CreateFixtureAsync(new(templatePath, second, template, true));
        var hashes = new Dictionary<string, string> { [first] = await HashAsync(first), [second] = await HashAsync(second) };
        var access = new FileSourceAccess();
        var session = new BatchSession([await access.ReadRevisionAsync(new(first, TestContext.Current.CancellationToken)),
            await access.ReadRevisionAsync(new(second, TestContext.Current.CancellationToken))]);
        var data = Path.Combine(workspace.Path, "data");
        var outputs = Path.Combine(workspace.Path, "results");
        Directory.CreateDirectory(outputs);
        var coordinator = new BatchCoordinator(session, new InspectionService(backend, access),
            new JobCoordinator(backend, access, new OwnedOutputStore(data)), access);
        var inspected = await coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        var firstInspection = Assert.IsType<MediaInspection>(Assert.Single(inspected.Items, item => item.SourcePath == first).Inspection);
        Assert.All(firstInspection.Tracks.Where(track => track.Kind == TrackKind.Audio), track => Assert.Equal("eng", track.Language));
        Assert.Equal([RegexOnlyTitle, LiteralTitle], firstInspection.Tracks.Where(track => track.Kind == TrackKind.Audio).Select(track => track.Title));
        var secondInspection = Assert.IsType<MediaInspection>(Assert.Single(inspected.Items, item => item.SourcePath == second).Inspection);
        Assert.Equal([TrackKind.Subtitles, TrackKind.Audio, TrackKind.Video, TrackKind.Audio], secondInspection.Tracks.Select(track => track.Kind));
        var french = Assert.Single(secondInspection.Tracks, track => track.Uid == originalAudio[1].Uid);
        Assert.Equal("fre", french.Language);
        Assert.Equal("fr", french.LanguageIetf);
        Assert.Equal(FrenchTitle, french.Title);
        Assert.Equal(EnglishTitle, Assert.Single(secondInspection.Tracks, track => track.Uid == originalAudio[0].Uid).Title);
        var rules = new LanguageRules(new(LanguageRuleMode.KeepMatching, ["eng"], new TrackTitleFilter(true, FilterText)),
            new(LanguageRuleMode.KeepMatching, ["eng"]));
        var previewResult = coordinator.PreviewLanguageRules(rules);
        Assert.Null(previewResult.Failure);
        var preview = Assert.IsType<BatchLanguagePreview>(previewResult.Preview);
        var applied = coordinator.ApplyLanguageRules(preview);
        Assert.True(applied.Applied, applied.Failure?.ToString());
        Assert.Equal(2, applied.ChangedItemIds.Length);
        var plans = new Dictionary<Guid, RemuxPlan>();

        foreach (var item in session.Snapshot.Items)
        {
            var inspection = Assert.IsType<MediaInspection>(item.Inspection);
            var intendedAudioUid = item.SourcePath == first ? originalAudio[1].Uid : originalAudio[0].Uid;
            var expected = inspection.Tracks.Where(track => track.Kind == TrackKind.Video || track.Uid == intendedAudioUid);
            Assert.Equal(expected.Select(track => track.Id), item.CurrentRetainedTrackIds);
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

            foreach (var retained in plan.RetainedTracks)
            {
                var outputTrack = Assert.Single(output.Tracks, track => track.Uid == retained.Uid);
                await MediaPayloadEvidence.AssertIdenticalAsync(new(item.SourcePath, retained.Id, plan.DestinationPath,
                    outputTrack.Id, workspace.Path, FormattableString.Invariant($"title-rule-{item.Id:N}-{retained.Id}")));
            }

            var sourceHashAfter = await HashAsync(item.SourcePath);
            Assert.Equal(hashes[item.SourcePath], sourceHashAfter);
            evidence.WriteLine(JsonSerializer.Serialize(new
            {
                kind = "real-language-and-literal-title-rule",
                source = Path.GetFileName(item.SourcePath),
                sourceHashBefore = hashes[item.SourcePath],
                sourceHashAfter,
                output = Path.GetFileName(plan.DestinationPath),
                outputHash = await HashAsync(plan.DestinationPath),
                titleFilter = FilterText,
                audioLanguage = "eng",
                previewId = preview.Id,
                appliedIds = Assert.Single(preview.Items, proposal => proposal.ItemId == item.Id).ProposedRetainedTrackIds,
                originalTracks = plan.Inspection.Tracks.Select(track => new
                {
                    track.Id,
                    uid = track.Uid!.Value.ToString(CultureInfo.InvariantCulture),
                    track.Title,
                    track.Language,
                    track.LanguageIetf
                }),
                retained = output.Tracks.Select(track => new { outputId = track.Id, uid = track.Uid!.Value.ToString(CultureInfo.InvariantCulture), payloadCompared = true }),
                metadataAndTimestampsVerified = verified.Passed,
                cleanupIssues = result.CleanupIssues.Length
            }));
        }

        Assert.Equal(2, completed.Items.Length);
        Assert.Equal(4, plans.Values.Sum(plan => plan.RetainedTracks.Length));
        Assert.Equal(2, Directory.GetFiles(outputs, "*.mkv").Length);
        Assert.Empty(Directory.GetDirectories(outputs));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(data, "ownership")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
    }

    /// <summary>Creates tiny title variants with pinned native tools while retaining the established statistics/language policy.</summary>
    private static async Task CreateFixtureAsync(TitleFixtureRequest request)
    {
        var audio = request.Template.Tracks.Where(track => track.Kind == TrackKind.Audio).ToArray();
        var video = Assert.Single(request.Template.Tracks.Where(track => track.Kind == TrackKind.Video));
        var subtitles = Assert.Single(request.Template.Tracks.Where(track => track.Kind == TrackKind.Subtitles));
        var order = request.Reordered ? new[] { subtitles.Id, audio[1].Id, video.Id, audio[0].Id }
            : request.Template.Tracks.Select(track => track.Id).ToArray();
        var arguments = new List<string> { "--disable-track-statistics-tags", "--normalize-language-ietf", "off", "--output", request.Destination,
            "--track-order", string.Join(',', order.Select(identifier => FormattableString.Invariant($"0:{identifier}"))),
            "--track-name", FormattableString.Invariant($"{audio[0].Id}:{(request.Reordered ? EnglishTitle : RegexOnlyTitle)}"),
            "--track-name", FormattableString.Invariant($"{audio[1].Id}:{(request.Reordered ? FrenchTitle : LiteralTitle)}") };

        if (request.Reordered)
        {
            arguments.AddRange(["--language", FormattableString.Invariant($"{audio[1].Id}:fre")]);
        }

        arguments.Add(request.Source);
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

        var created = await new OwnedProcessRunner().RunAsync(new(tools.MkvMerge, arguments,
            MaximumDiagnosticsCharacters, FixtureCreationTimeout, null, TestContext.Current.CancellationToken, environment));
        Assert.True(created.ExitCode == 0, created.StandardOutput + created.StandardError);
    }

    /// <summary>Hashes complete sources or outputs through read-only handles.</summary>
    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, TestContext.Current.CancellationToken));
    }

    private sealed record TitleFixtureRequest(string Source, string Destination, MediaInspection Template, bool Reordered);
}
