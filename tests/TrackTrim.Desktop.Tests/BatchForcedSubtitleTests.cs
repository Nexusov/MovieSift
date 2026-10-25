using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Media;
using TrackTrim.Desktop.Views;
using Xunit;

namespace TrackTrim.Desktop.Tests;

/// <summary>Checks explicit forced-subtitle editing and compiled bilingual controls without native claims.</summary>
public sealed class BatchForcedSubtitleTests
{
    /// <summary>Preserves approved choices during edits and EN/RU switches, then changes only the affected plan.</summary>
    [AvaloniaFact]
    public async Task ForcedEditRequiresFreshPreviewAndExplicitApplyWithChangedOnlyInvalidation()
    {
        var fixture = CreateFixture();
        await fixture.LoadAsync();
        fixture.Batch.Items[1].Tracks.Single(track => track.Track.Id == 99).Keep = false;
        ApproveAll(fixture);
        await fixture.Batch.ReviewCommand.ExecuteAsync(null);
        var before = fixture.Session.Snapshot;
        var plans = fixture.Batch.Items.Select(item => item.ApprovedPlan).ToArray();
        var rules = fixture.Batch.LanguageRules;
        var view = new BatchLanguageRulesView { DataContext = rules };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            Assert.False(rules.KeepOnlyForcedSubtitles);
            Assert.False(view.FindControl<CheckBox>("SubtitleForcedOnlyCheckBox")!.IsEnabled);
            Configure(fixture);
            rules.PreviewCommand.Execute(null);
            var previous = rules.Preview!;
            Assert.False(previous.Rules.KeepOnlyForcedSubtitles);
            Assert.Contains(3, previous.Items[0].ProposedRetainedTrackIds);
            Assert.Contains(9, previous.Items[0].ProposedRetainedTrackIds);

            rules.KeepOnlyForcedSubtitles = true;
            Assert.False(rules.HasPreview);
            Assert.False(rules.ApplyCommand.CanExecute(null));
            rules.ApplyCommand.Execute(null);
            Assert.Same(before, fixture.Session.Snapshot);
            Assert.True(fixture.Batch.HasReview);
            Assert.Equal(plans, fixture.Batch.Items.Select(item => item.ApprovedPlan));

            rules.PreviewCommand.Execute(null);
            var preview = rules.Preview!;
            Assert.True(preview.Rules.KeepOnlyForcedSubtitles);
            Assert.Equal([0, 1, 2, 9], preview.Items[0].ProposedRetainedTrackIds);
            Assert.Equal([77, 25, 8, 61], preview.Items[1].ProposedRetainedTrackIds);
            Assert.Equal(ErrorCode.StalePlan, fixture.Coordinator.ApplyLanguageRules(previous).Failure!.Code);

            foreach (var language in new[] { "ru", "en" })
            {
                await fixture.Main.ChangeLanguageCommand.ExecuteAsync(language);
                Dispatcher.UIThread.RunJobs();
                Assert.Same(preview, rules.Preview);
                Assert.Same(before, fixture.Session.Snapshot);
                Assert.True(rules.KeepOnlyForcedSubtitles);
                Assert.True(rules.SubtitleTitleFilterEnabled);
                Assert.Equal("eng", rules.SubtitleLanguages);
                Assert.Equal(" Captions ", rules.SubtitleTitleContains);
                var control = view.FindControl<CheckBox>("SubtitleForcedOnlyCheckBox")!;
                Assert.True(control.IsChecked);
                Assert.True(control.IsEnabled);
                Assert.Equal(fixture.Localization["RuleForcedSubtitlesOnly"], control.Content);
                Assert.Equal(plans, fixture.Batch.Items.Select(item => item.ApprovedPlan));
            }

            rules.ApplyCommand.Execute(null);
            Assert.True(rules.IsApplied);
            Assert.False(rules.ApplyCommand.CanExecute(null));
            Assert.Equal([0, 1, 2, 9], fixture.Session.Snapshot.Items[0].CurrentRetainedTrackIds);
            Assert.Null(fixture.Batch.Items[0].ApprovedPlan);
            Assert.Same(plans[1], fixture.Batch.Items[1].ApprovedPlan);
            Assert.False(fixture.Batch.HasReview);
            Assert.False(fixture.Batch.RunCommand.CanExecute(null));
            Assert.Empty(fixture.Backend.RemuxRequests);
            Assert.Equal(0, fixture.Outputs.CleanupCount);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Ignores a stored constraint in unchanged mode and preserves no-op approvals and review identities.</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnchangedOrAlreadyMatchingChoicesPreserveApprovalsAndReview(bool targetSubtitles)
    {
        var fixture = CreateFixture();
        await fixture.LoadAsync();

        if (targetSubtitles)
        {
            foreach (var item in fixture.Batch.Items)
            {
                foreach (var track in item.Tracks.Where(track => track.Track.Kind == TrackKind.Subtitles && !track.Track.IsForced))
                {
                    track.Keep = false;
                }
            }
        }

        ApproveAll(fixture);
        await fixture.Batch.ReviewCommand.ExecuteAsync(null);
        var before = fixture.Session.Snapshot;
        var rules = fixture.Batch.LanguageRules;
        Configure(fixture);
        rules.KeepOnlyForcedSubtitles = true;
        rules.SubtitleRuleEnabled = targetSubtitles;
        Assert.Equal(targetSubtitles, rules.CanEditSubtitleLanguages);
        rules.PreviewCommand.Execute(null);
        rules.ApplyCommand.Execute(null);
        Assert.True(rules.IsApplied);
        Assert.Same(before, fixture.Session.Snapshot);
        Assert.True(fixture.Batch.HasReview);
        Assert.True(fixture.Batch.RunCommand.CanExecute(null));
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>Retains rule preferences but clears old proposals across an explicit safe New batch.</summary>
    [AvaloniaFact]
    public async Task NewBatchRetainsForcedConfigurationWithoutAutomaticallySelectingNewInputs()
    {
        var fixture = CreateFixture();
        await fixture.LoadAsync();
        await fixture.Main.ChangeLanguageCommand.ExecuteAsync("ru");
        Configure(fixture);
        var rules = fixture.Batch.LanguageRules;
        rules.KeepOnlyForcedSubtitles = true;
        rules.PreviewCommand.Execute(null);
        var previous = rules.Preview!;
        var folder = fixture.Batch.OutputFolder;
        fixture.Batch.NewBatchCommand.Execute(null);
        fixture.Batch.ConfirmNewBatchCommand.Execute(null);
        Assert.Empty(fixture.Batch.Items);
        Assert.False(rules.HasPreview);
        Assert.True(rules.KeepOnlyForcedSubtitles);
        Assert.Equal("ru", fixture.Localization.Language);
        Assert.Equal(folder, fixture.Batch.OutputFolder);
        Assert.Equal(ErrorCode.StalePlan, fixture.Coordinator.ApplyLanguageRules(previous).Failure!.Code);
        await fixture.LoadAsync();
        Assert.All(fixture.Batch.Items, item => Assert.All(item.Tracks, track => Assert.True(track.Keep)));
        Assert.All(fixture.Batch.Items, item => Assert.Null(item.ApprovedPlan));
        Assert.False(rules.HasPreview);
        Assert.False(rules.ApplyCommand.CanExecute(null));
        Assert.Empty(fixture.Backend.RemuxRequests);
        Assert.Equal(0, fixture.Outputs.CleanupCount);
    }

    /// <summary>Uses the shared discovery operation gate for the new compiled control and proposal actions.</summary>
    [AvaloniaFact]
    public async Task ActiveDiscoveryDisablesTheForcedControlWithoutChangingItsProposal()
    {
        var fixture = CreateFixture();
        await fixture.LoadAsync();
        Configure(fixture);
        var rules = fixture.Batch.LanguageRules;
        rules.KeepOnlyForcedSubtitles = true;
        rules.PreviewCommand.Execute(null);
        var preview = rules.Preview;
        var view = new BatchLanguageRulesView { DataContext = rules };
        var window = new Window { Content = view };
        window.Show();
        var entered = BatchViewModelFixture.Signal();
        var release = BatchViewModelFixture.Signal();
        fixture.Discovery.OnDiscover = async request =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(request.CancellationToken);
            return new([], []);
        };
        var operation = fixture.Batch.AddFilesCommand.ExecuteAsync(null);

        try
        {
            await BatchViewModelFixture.Await(entered.Task);
            Dispatcher.UIThread.RunJobs();
            Assert.False(view.FindControl<CheckBox>("SubtitleForcedOnlyCheckBox")!.IsEnabled);
            Assert.False(rules.PreviewCommand.CanExecute(null));
            Assert.False(rules.ApplyCommand.CanExecute(null));
            Assert.True(rules.KeepOnlyForcedSubtitles);
            Assert.Same(preview, rules.Preview);
        }
        finally
        {
            release.TrySetResult();
        }

        await BatchViewModelFixture.Await(operation);
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.FindControl<CheckBox>("SubtitleForcedOnlyCheckBox")!.IsEnabled);
        Assert.Same(preview, rules.Preview);
        Assert.Empty(fixture.Backend.RemuxRequests);
        window.Close();
    }

    /// <summary>Gives each file matching forced/non-forced subtitles with deliberately different identities.</summary>
    private static BatchViewModelFixture CreateFixture()
    {
        var fixture = new BatchViewModelFixture();

        foreach (var (path, inspection) in fixture.Backend.Inspections.ToArray())
        {
            var subtitle = inspection.Tracks.Single(track => track.Kind == TrackKind.Subtitles);
            var first = subtitle.Id == 3;
            var extra = subtitle with { Id = first ? 9 : 99, Uid = first ? 900UL : 9900UL, IsForced = first, Title = "Captions" };
            fixture.Backend.Inspections[path] = inspection with
            {
                Tracks = inspection.Tracks.Select(track => track.Kind == TrackKind.Subtitles
                    ? track with { Title = "Captions", IsForced = !first } : track).Append(extra).ToImmutableArray()
            };
        }

        return fixture;
    }

    /// <summary>Targets subtitles with the existing exact language and literal-title conjunction.</summary>
    private static void Configure(BatchViewModelFixture fixture)
    {
        var rules = fixture.Batch.LanguageRules;
        rules.SubtitleRuleEnabled = true;
        rules.SubtitleLanguages = "eng";
        rules.SubtitleTitleFilterEnabled = true;
        rules.SubtitleTitleContains = " Captions ";
    }

    /// <summary>Creates independent approvals without changing any current file-local selection.</summary>
    private static void ApproveAll(BatchViewModelFixture fixture)
    {
        foreach (var item in fixture.Batch.Items)
        {
            fixture.Batch.SelectedItem = item;
            fixture.Batch.ApproveSelectedCommand.Execute(null);
        }
    }
}
