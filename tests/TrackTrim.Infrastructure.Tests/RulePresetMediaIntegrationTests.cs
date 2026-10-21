using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using TrackTrim.Application.Batches;
using TrackTrim.Application.Inspection;
using TrackTrim.Application.Jobs;
using TrackTrim.Application.Presets;
using TrackTrim.Core.Media;
using TrackTrim.Core.Rules;
using TrackTrim.Infrastructure.Files;
using TrackTrim.Infrastructure.MediaTools;
using TrackTrim.Infrastructure.Persistence;
using Xunit;

namespace TrackTrim.Infrastructure.Tests;

/// <summary>Loads persisted configuration into independent real inspections without persisting media identities or approvals.</summary>
public sealed class RulePresetMediaIntegrationTests(ITestOutputHelper evidence)
{
    /// <summary>Round-trips inactive values, then explicitly previews and applies the loaded forced-subtitle conjunction.</summary>
    [MediaFact]
    public async Task FreshPortablePresetLoadAppliesToDifferentFileLocalSubtitlePayloads()
    {
        using var workspace = new TestWorkspace();
        var cancellationToken = TestContext.Current.CancellationToken;
        var rules = new LanguageRules(
            new(LanguageRuleMode.Unchanged, ["fra", "deu"], new TrackTitleFilter(false, "  Dormant [literal].*  ")),
            new(LanguageRuleMode.KeepMatching, ["eng"], new TrackTitleFilter(true, "  Captions  ")),
            KeepOnlyForcedSubtitles: true);
        var catalogData = Path.Combine(workspace.Path, "portable app", "data");
        var catalogPath = Path.Combine(catalogData, "rule-presets.json");
        var writer = new RulePresetService(new PortableRulePresetStore(catalogData));
        var saved = await writer.SaveAsNewAsync(new("Forced English — Субтитры", rules, cancellationToken));
        Assert.Null(saved.Failure);
        Assert.True(saved.Committed);
        Assert.True(saved.CleanupIssues.IsDefaultOrEmpty);
        var presetId = Assert.IsType<Guid>(saved.AffectedPresetId);
        Assert.NotEqual(Guid.Empty, presetId);
        Assert.True(File.Exists(catalogPath));
        var catalogHash = await HashAsync(catalogPath);

        var reader = new RulePresetService(new PortableRulePresetStore(catalogData));
        var catalog = await reader.ReadAsync(cancellationToken);
        Assert.Null(catalog.Failure);
        Assert.NotNull(catalog.Catalog);
        var loaded = await reader.LoadAsync(new(presetId, cancellationToken));
        Assert.Null(loaded.Failure);
        Assert.NotNull(loaded.Preset);
        var loadedPreset = loaded.Preset!;
        Assert.True(LanguageRuleConfiguration.ContentEquals(rules, loadedPreset.Rules));
        Assert.Equal(rules.Audio.Languages.ToArray(), loadedPreset.Rules.Audio.Languages.ToArray());
        Assert.Equal(rules.Audio.TitleFilter, loadedPreset.Rules.Audio.TitleFilter);
        Assert.Equal(rules.Subtitles.TitleFilter, loadedPreset.Rules.Subtitles.TitleFilter);
        Assert.True(loadedPreset.Rules.KeepOnlyForcedSubtitles);
        Assert.Equal(catalogHash, await HashAsync(catalogPath));

        var access = new FileSourceAccess();
        var scratch = Path.Combine(workspace.Path, "scratch");
        var tools = MkvToolPaths.FromDirectory(MediaTestEnvironment.ToolDirectory) with { IsDevelopmentOverride = true };
        var remuxStarts = 0;
        var backend = new MkvBackend(new MkvBackendOptions(access, tools, scratch) { RemuxProcessStarted = _ => remuxStarts++ });
        var templatePath = Path.Combine(MediaTestEnvironment.FixtureDirectory, "ordinary.mkv");
        var templateHash = await HashAsync(templatePath);
        var template = await backend.InspectAsync(new(templatePath, cancellationToken));
        var originalSubtitleUid = Assert.Single(template.Tracks.Where(track => track.Kind == TrackKind.Subtitles)).Uid;
        var fixtures = await ForcedSubtitleFixture.CreateAsync(new(templatePath, workspace.Path));
        var sourceHashes = new Dictionary<string, string>
        {
            [fixtures.First] = await HashAsync(fixtures.First),
            [fixtures.Second] = await HashAsync(fixtures.Second)
        };
        var session = new BatchSession([
            await access.ReadRevisionAsync(new(fixtures.First, cancellationToken)),
            await access.ReadRevisionAsync(new(fixtures.Second, cancellationToken))]);
        var jobData = Path.Combine(workspace.Path, "unused job data");
        var coordinator = new BatchCoordinator(session, new InspectionService(backend, access),
            new JobCoordinator(backend, access, new OwnedOutputStore(jobData)), access);
        var inspected = await coordinator.InspectPendingAsync(cancellationToken);
        Assert.Equal(2, inspected.Items.Length);
        Assert.All(inspected.Items, item =>
        {
            Assert.NotNull(item.Inspection);
            Assert.Equal(item.Inspection!.Tracks.Select(track => track.Id), item.CurrentRetainedTrackIds);
            Assert.Null(item.ApprovedPlan);
        });
        var first = Assert.Single(inspected.Items, item => item.SourcePath == fixtures.First);
        var second = Assert.Single(inspected.Items, item => item.SourcePath == fixtures.Second);
        var firstSubtitles = first.Inspection!.Tracks.Where(track => track.Kind == TrackKind.Subtitles).ToArray();
        var secondSubtitles = second.Inspection!.Tracks.Where(track => track.Kind == TrackKind.Subtitles).ToArray();
        Assert.Equal([3, 4], firstSubtitles.Select(track => track.Id));
        Assert.Equal([0, 2], secondSubtitles.Select(track => track.Id));
        Assert.All(firstSubtitles.Concat(secondSubtitles), track =>
        {
            Assert.Equal("eng", track.Language);
            Assert.Equal(ForcedSubtitleFixture.SubtitleTitle, track.Title);
        });
        var firstForced = Assert.Single(firstSubtitles, track => track.IsForced);
        var secondForced = Assert.Single(secondSubtitles, track => track.IsForced);
        Assert.Equal(originalSubtitleUid, firstForced.Uid);
        Assert.NotEqual(originalSubtitleUid, secondForced.Uid);
        Assert.Equal(3, firstForced.Id);
        Assert.Equal(0, secondForced.Id);

        var proposed = coordinator.PreviewLanguageRules(loadedPreset.Rules);
        Assert.Null(proposed.Failure);
        var preview = Assert.IsType<BatchLanguagePreview>(proposed.Preview);
        Assert.Equal(inspected.Revision, session.Snapshot.Revision);
        Assert.All(session.Snapshot.Items, item =>
            Assert.Equal(item.Inspection!.Tracks.Select(track => track.Id), item.CurrentRetainedTrackIds));
        var applied = coordinator.ApplyLanguageRules(preview);
        Assert.True(applied.Applied, applied.Failure?.ToString());
        Assert.Equal(2, applied.ChangedItemIds.Length);

        foreach (var item in session.Snapshot.Items)
        {
            var inspection = item.Inspection!;
            var expected = inspection.Tracks.Where(track => track.Kind != TrackKind.Subtitles || track.IsForced);
            Assert.Equal(expected.Select(track => track.Id), item.CurrentRetainedTrackIds);
            Assert.Equal(Assert.Single(preview.Items, candidate => candidate.ItemId == item.Id).ProposedRetainedTrackIds,
                item.CurrentRetainedTrackIds);
            Assert.Null(item.ApprovedPlan);
            Assert.Null(item.Result);
            Assert.Null(item.Failure);
            Assert.Equal(BatchItemState.NeedsReview, item.State);
            Assert.Equal(sourceHashes[item.SourcePath], await HashAsync(item.SourcePath));
        }

        var payloadDirectory = Path.Combine(workspace.Path, "payload evidence");
        Directory.CreateDirectory(payloadDirectory);
        var originalInSecond = Assert.Single(secondSubtitles, track => track.Uid == originalSubtitleUid);
        var addedInFirst = Assert.Single(firstSubtitles, track => track.Uid == secondForced.Uid);
        Assert.False(originalInSecond.IsForced);
        Assert.False(addedInFirst.IsForced);
        var originalPayload = await MediaPayloadEvidence.AssertIdenticalAsync(new(fixtures.First, firstForced.Id,
            fixtures.Second, originalInSecond.Id, payloadDirectory, "original subtitle"));
        var addedPayload = await MediaPayloadEvidence.AssertIdenticalAsync(new(fixtures.Second, secondForced.Id,
            fixtures.First, addedInFirst.Id, payloadDirectory, "added subtitle"));
        Assert.NotEqual(originalPayload.SourceSha256, addedPayload.SourceSha256);
        Assert.Equal(templateHash, await HashAsync(templatePath));
        Assert.Equal(catalogHash, await HashAsync(catalogPath));
        Assert.Equal(0, remuxStarts);
        Assert.False(Directory.Exists(jobData));
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
        Assert.All(Directory.EnumerateFileSystemEntries(catalogData), path =>
            Assert.Contains(Path.GetFileName(path), new[] { "rule-presets.json", "rule-presets.lock" }));

        foreach (var item in session.Snapshot.Items)
        {
            var sourceAfter = await HashAsync(item.SourcePath);
            Assert.Equal(sourceHashes[item.SourcePath], sourceAfter);
            var retainedSubtitle = Assert.Single(item.Inspection!.Tracks,
                track => track.Kind == TrackKind.Subtitles && item.CurrentRetainedTrackIds.Contains(track.Id));
            evidence.WriteLine(JsonSerializer.Serialize(new
            {
                kind = "real-portable-rule-preset-preview-apply",
                presetId,
                catalogHash,
                freshInstances = true,
                exactInactiveConfigurationPreserved = true,
                templateHash,
                backend = item.Inspection.Backend,
                fixture = nameof(ForcedSubtitleFixture),
                source = Path.GetFileName(item.SourcePath),
                sourceHashBefore = sourceHashes[item.SourcePath],
                sourceHashAfter = sourceAfter,
                previewId = preview.Id,
                appliedIds = item.CurrentRetainedTrackIds,
                retainedSubtitleId = retainedSubtitle.Id,
                retainedSubtitleUid = retainedSubtitle.Uid.ToString(CultureInfo.InvariantCulture),
                retainedSubtitleIsForced = retainedSubtitle.IsForced,
                subtitlePayload = item.SourcePath == fixtures.First ? originalPayload : addedPayload,
                remuxStarts,
                approvedPlan = item.ApprovedPlan is not null,
                scratchEntriesRemaining = 0
            }));
        }
    }

    /// <summary>Hashes complete original files and portable metadata through read-only handles.</summary>
    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, TestContext.Current.CancellationToken));
    }
}
