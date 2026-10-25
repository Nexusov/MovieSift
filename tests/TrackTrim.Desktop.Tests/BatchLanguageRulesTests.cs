using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using TrackTrim.Application.Batches;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Media;
using TrackTrim.Desktop.Views;
using Xunit;

namespace TrackTrim.Desktop.Tests;

/// <summary>Checks explicit per-file language proposals and their compiled UI bindings without native claims.</summary>
public sealed class BatchLanguageRulesTests
{
    /// <summary>Preserves existing plans during preview/language changes and applies different file-local choices explicitly.</summary>
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public async Task PreviewIsReadOnlyUntilApplyAndEachFileStillRequiresApproval(string language)
    {
        var fixture = CreateLanguageFixture();
        var view = new BatchLanguageRulesView { DataContext = fixture.Batch.LanguageRules };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            await fixture.LoadAsync();
            ApproveAll(fixture);
            await fixture.Batch.ReviewCommand.ExecuteAsync(null);
            var before = fixture.Session.Snapshot;
            var plans = fixture.Batch.Items.Select(item => item.ApprovedPlan).ToArray();
            var rules = fixture.Batch.LanguageRules;
            ConfigureRules(fixture);
            rules.PreviewCommand.Execute(null);
            var preview = Assert.IsType<BatchLanguagePreview>(rules.Preview);
            Assert.Same(before, fixture.Session.Snapshot);
            Assert.True(fixture.Batch.HasReview);
            Assert.All(fixture.Batch.Items, item => Assert.All(item.Tracks, track => Assert.True(track.Keep)));
            Assert.Equal([0, 1, 2], preview.Items[0].ProposedRetainedTrackIds);
            Assert.Equal([8, 61], preview.Items[1].ProposedRetainedTrackIds);
            Assert.All(preview.Items, item => Assert.Equal(BatchLanguagePreviewState.Changed, item.State));
            Assert.All(rules.Files, file => Assert.True(file.NoSubtitleMatches));
            fixture.Batch.SelectedItem = fixture.Batch.Items[0];
            fixture.Batch.SelectedItem = fixture.Batch.Items[1];
            await fixture.Main.ChangeLanguageCommand.ExecuteAsync(language);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(preview, rules.Preview);
            Assert.Same(before, fixture.Session.Snapshot);
            Assert.Equal("en", rules.AudioLanguages);
            Assert.Equal("eng", rules.SubtitleLanguages);
            Assert.True(rules.AudioRuleEnabled);
            Assert.True(rules.SubtitleRuleEnabled);
            Assert.Equal(fixture.Localization["RulePreview"], view.FindControl<Button>("PreviewLanguageRulesButton")!.Content);
            Assert.Equal(fixture.Localization["RuleApply"], view.FindControl<Button>("ApplyLanguageRulesButton")!.Content);
            Assert.Contains(fixture.Localization["RuleNoSubtitleMatches"], rules.Files[0].NoSubtitleMatchesText);
            Assert.Equal(plans, fixture.Batch.Items.Select(item => item.ApprovedPlan));
            rules.ApplyCommand.Execute(null);
            Assert.True(rules.IsApplied);
            Assert.True(rules.HasPreview);
            Assert.False(rules.ApplyCommand.CanExecute(null));
            Assert.False(fixture.Batch.HasReview);
            Assert.False(fixture.Batch.RunCommand.CanExecute(null));
            Assert.Equal([0, 1, 2], Kept(fixture, 0));
            Assert.Equal([8, 61], Kept(fixture, 1));
            Assert.All(fixture.Batch.Items, item => Assert.Null(item.ApprovedPlan));
            Assert.Empty(fixture.Backend.RemuxRequests);
            Assert.Equal(0, fixture.Outputs.CleanupCount);

            fixture.Batch.Items[0].Tracks.Single(track => track.Track.Id == 1).Keep = false;
            Assert.Equal([0, 2], fixture.Session.Snapshot.Items[0].CurrentRetainedTrackIds);
            Assert.Equal([8, 61], fixture.Session.Snapshot.Items[1].CurrentRetainedTrackIds);
            Assert.True(rules.IsStale);
            ApproveAll(fixture);
            await fixture.Batch.ReviewCommand.ExecuteAsync(null);
            Assert.True(fixture.Batch.RunCommand.CanExecute(null));
            await fixture.Batch.RunCommand.ExecuteAsync(null);
            Assert.Equal(BatchPhase.Completed, fixture.Batch.Snapshot.Phase);
            Assert.Equal(2, fixture.Backend.RemuxRequests.Count);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Leaves untargeted choices and matching approved plans/review exactly intact on a no-op apply.</summary>
    [AvaloniaFact]
    public async Task UnchangedAndAlreadyMatchingRulesPreserveApprovalAndReviewIdentity()
    {
        var fixture = CreateLanguageFixture();
        await fixture.PrepareAsync();
        fixture.Batch.Items[1].Tracks.Single(track => track.Track.Id == 25).Keep = false;
        ApproveAll(fixture);
        await fixture.Batch.ReviewCommand.ExecuteAsync(null);
        var before = fixture.Session.Snapshot;
        var rules = fixture.Batch.LanguageRules;
        Assert.False(rules.AudioRuleEnabled);
        Assert.False(rules.SubtitleRuleEnabled);
        rules.PreviewCommand.Execute(null);
        Assert.All(rules.Preview!.Items, item => Assert.Equal(BatchLanguagePreviewState.Unchanged, item.State));
        rules.ApplyCommand.Execute(null);
        Assert.Same(before, fixture.Session.Snapshot);
        rules.AudioRuleEnabled = true;
        rules.AudioLanguages = "eng";
        rules.PreviewCommand.Execute(null);
        rules.ApplyCommand.Execute(null);
        Assert.Same(before, fixture.Session.Snapshot);
        Assert.True(fixture.Batch.RunCommand.CanExecute(null));
        Assert.All(fixture.Batch.Items, item => Assert.False(item.Tracks.Single(track => track.Track.Kind == TrackKind.Subtitles).Keep));
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>Rejects saved proposals after real per-file edits rather than applying stale IDs or destinations.</summary>
    [AvaloniaTheory]
    [InlineData("selection")]
    [InlineData("destination")]
    [InlineData("remove")]
    [InlineData("inspection")]
    public async Task RelevantBatchEditInvalidatesThePreview(string edit)
    {
        var fixture = CreateLanguageFixture();
        await fixture.LoadAsync();
        ConfigureRules(fixture);
        var rules = fixture.Batch.LanguageRules;
        rules.PreviewCommand.Execute(null);
        var previous = rules.Preview!;
        var first = fixture.Batch.Items[0];
        fixture.Batch.SelectedItem = first;

        switch (edit)
        {
            case "selection":
                first.Tracks.Single(track => track.Track.Id == 1).Keep = false;
                break;
            case "destination":
                first.OutputPath = Path.Combine(fixture.Batch.OutputFolder, "explicit different destination.mkv");
                break;
            case "remove":
                fixture.Batch.RemoveSelectedCommand.Execute(null);
                break;
            case "inspection":
                await fixture.Batch.InspectSelectedCommand.ExecuteAsync(null);
                break;
        }

        var changed = fixture.Session.Snapshot;
        Assert.False(rules.HasPreview);
        Assert.True(rules.IsStale);
        Assert.False(rules.ApplyCommand.CanExecute(null));
        rules.ApplyCommand.Execute(null);
        var rejected = fixture.Coordinator.ApplyLanguageRules(previous);
        Assert.False(rejected.Applied);
        Assert.Equal(ErrorCode.StalePlan, rejected.Failure!.Code);
        Assert.Same(changed, fixture.Session.Snapshot);
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>Clears rule previews on New batch while keeping configuration and rejecting detached editors/proposals.</summary>
    [AvaloniaFact]
    public async Task NewBatchClearsProposalWithoutAutomaticallyApplyingRulesToNewFiles()
    {
        var fixture = CreateLanguageFixture();
        await fixture.LoadAsync();
        await fixture.Main.ChangeLanguageCommand.ExecuteAsync("ru");
        ConfigureRules(fixture);
        var rules = fixture.Batch.LanguageRules;
        rules.PreviewCommand.Execute(null);
        var previous = rules.Preview!;
        var previousEditor = fixture.Batch.Items[0];
        var folder = fixture.Batch.OutputFolder;
        fixture.Batch.NewBatchCommand.Execute(null);
        Assert.False(rules.PreviewCommand.CanExecute(null));
        Assert.False(rules.ApplyCommand.CanExecute(null));
        fixture.Batch.ConfirmNewBatchCommand.Execute(null);
        Assert.Empty(fixture.Batch.Items);
        Assert.Empty(rules.Files);
        Assert.False(rules.HasPreview);
        Assert.False(rules.IsStale);
        Assert.Empty(rules.StatusText);
        Assert.Equal("ru", fixture.Localization.Language);
        Assert.Equal(folder, fixture.Batch.OutputFolder);
        Assert.True(rules.AudioRuleEnabled);
        Assert.Equal("en", rules.AudioLanguages);
        await fixture.Batch.AddFilesCommand.ExecuteAsync(null);
        Assert.False(rules.HasPreview);
        Assert.All(fixture.Batch.Items, item => Assert.All(item.Tracks, track => Assert.True(track.Keep)));
        rules.PreviewCommand.Execute(null);
        var current = rules.Preview;
        var snapshot = fixture.Session.Snapshot;
        previousEditor.Tracks.Single(track => track.Track.Id == 1).Keep = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Same(current, rules.Preview);
        Assert.Same(snapshot, fixture.Session.Snapshot);
        Assert.Equal(ErrorCode.StalePlan, fixture.Coordinator.ApplyLanguageRules(previous).Failure!.Code);
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>Requires fresh silent-output consent when a subtitle-only rule changes an already silent selection.</summary>
    [AvaloniaFact]
    public async Task ApplyingChangedChoicesCannotCarryZeroAudioConsent()
    {
        var fixture = CreateLanguageFixture();
        await fixture.LoadAsync();
        var first = fixture.Batch.Items[0];
        fixture.Batch.Items[1].Included = false;

        foreach (var track in first.Tracks.Where(track => track.Track.Kind == TrackKind.Audio))
        {
            track.Keep = false;
        }

        first.ZeroAudioConfirmed = true;
        fixture.Batch.SelectedItem = first;
        fixture.Batch.ApproveSelectedCommand.Execute(null);
        Assert.NotNull(first.ApprovedPlan);
        var rules = fixture.Batch.LanguageRules;
        rules.SubtitleRuleEnabled = true;
        rules.SubtitleLanguages = "en";
        rules.PreviewCommand.Execute(null);
        rules.ApplyCommand.Execute(null);
        Assert.Equal([0], Kept(fixture, 0));
        Assert.False(first.ZeroAudioConfirmed);
        Assert.Null(first.ApprovedPlan);
        fixture.Batch.ApproveSelectedCommand.Execute(null);
        Assert.Null(first.ApprovedPlan);
        Assert.Equal(fixture.Localization["ErrorZeroAudioConfirmationRequired"], first.ErrorText);
        first.ZeroAudioConfirmed = true;
        fixture.Batch.ApproveSelectedCommand.Execute(null);
        Assert.NotNull(first.ApprovedPlan);
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>Keeps an unreliable file visible and unchanged while applying a reliable proposal to another file.</summary>
    [AvaloniaFact]
    public async Task ManualReviewIssuesDoNotSilentlyExcludeOrChangeTheFile()
    {
        var fixture = CreateLanguageFixture();
        var firstPath = fixture.Dialogs.Sources[0];
        var inspection = fixture.Backend.Inspections[firstPath];
        fixture.Backend.Inspections[firstPath] = inspection with
        {
            Tracks = inspection.Tracks.Select(track => track.Id == 1 ? track with { Language = "und", LanguageIetf = "und" } : track).ToImmutableArray()
        };
        await fixture.LoadAsync();
        ApproveAll(fixture);
        await fixture.Batch.ReviewCommand.ExecuteAsync(null);
        var manuallyApproved = fixture.Batch.Items[0].ApprovedPlan;
        ConfigureRules(fixture);
        var rules = fixture.Batch.LanguageRules;
        rules.PreviewCommand.Execute(null);
        Assert.Equal(BatchLanguagePreviewState.ManualReview, rules.Files[0].Item.State);
        Assert.Contains("und", rules.Files[0].IssuesText);
        Assert.Contains(fixture.Localization["RuleIssueUnknownLanguage"], rules.Files[0].IssuesText);
        rules.ApplyCommand.Execute(null);
        Assert.Same(manuallyApproved, fixture.Batch.Items[0].ApprovedPlan);
        Assert.All(fixture.Batch.Items[0].Tracks, track => Assert.True(track.Keep));
        Assert.True(fixture.Batch.Items[0].Included);
        Assert.Equal([8, 61], Kept(fixture, 1));
        Assert.Null(fixture.Batch.Items[1].ApprovedPlan);
        Assert.False(fixture.Batch.HasReview);
        Assert.True(rules.Files[0].HasIssues);
        Assert.Equal(2, fixture.Batch.Items.Count);
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>Disables rule operations while the shared workspace is owned by an input-discovery operation.</summary>
    [AvaloniaFact]
    public async Task ActiveDiscoveryDisablesRulePreviewAndApplication()
    {
        var fixture = CreateLanguageFixture();
        await fixture.LoadAsync();
        ConfigureRules(fixture);
        var rules = fixture.Batch.LanguageRules;
        rules.PreviewCommand.Execute(null);
        var preview = rules.Preview;
        var snapshot = fixture.Session.Snapshot;
        var entered = BatchViewModelFixture.Signal();
        var release = BatchViewModelFixture.Signal();
        fixture.Discovery.OnDiscover = async request =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(request.CancellationToken);
            return new([], []);
        };
        var work = fixture.Batch.AddFilesCommand.ExecuteAsync(null);

        try
        {
            await BatchViewModelFixture.Await(entered.Task);
            Assert.False(rules.CanConfigure);
            Assert.False(rules.PreviewCommand.CanExecute(null));
            Assert.False(rules.ApplyCommand.CanExecute(null));
            rules.ApplyCommand.Execute(null);
            Assert.Same(preview, rules.Preview);
            Assert.Same(snapshot, fixture.Session.Snapshot);
        }
        finally
        {
            release.TrySetResult();
        }

        await BatchViewModelFixture.Await(work);
        Assert.True(rules.CanConfigure);
        Assert.True(rules.ApplyCommand.CanExecute(null));
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>Uses actual test inspection metadata with one French audio in the reordered second file.</summary>
    private static BatchViewModelFixture CreateLanguageFixture()
    {
        var fixture = new BatchViewModelFixture();
        var secondPath = fixture.Dialogs.Sources[1];

        foreach (var (path, inspection) in fixture.Backend.Inspections.ToArray())
        {
            fixture.Backend.Inspections[path] = inspection with
            {
                Tracks = inspection.Tracks.Select(track => track.Kind switch
                {
                    TrackKind.Subtitles => track with { Language = "rus", LanguageIetf = "ru" },
                    TrackKind.Audio when path == secondPath && !track.IsDefault => track with { Language = "fre", LanguageIetf = "fr" },
                    _ => track
                }).ToImmutableArray()
            };
        }

        return fixture;
    }

    /// <summary>Configures explicit all-English matching; Russian subtitles consequently have no match.</summary>
    private static void ConfigureRules(BatchViewModelFixture fixture)
    {
        var rules = fixture.Batch.LanguageRules;
        rules.AudioRuleEnabled = true;
        rules.AudioLanguages = "en";
        rules.SubtitleRuleEnabled = true;
        rules.SubtitleLanguages = "eng";
    }

    /// <summary>Approves each current editor independently without applying any cross-file IDs.</summary>
    private static void ApproveAll(BatchViewModelFixture fixture)
    {
        foreach (var item in fixture.Batch.Items.Where(item => item.Included))
        {
            fixture.Batch.SelectedItem = item;
            fixture.Batch.ApproveSelectedCommand.Execute(null);
        }
    }

    /// <summary>Reads observed Keep choices from one current file editor.</summary>
    private static IEnumerable<int> Kept(BatchViewModelFixture fixture, int index) =>
        fixture.Batch.Items[index].Tracks.Where(track => track.Keep).Select(track => track.Track.Id);
}
