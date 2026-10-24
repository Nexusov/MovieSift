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

/// <summary>Checks literal-title rule editing and compiled bilingual bindings without native-desktop claims.</summary>
public sealed class BatchTitleFilterTests
{
    /// <summary>Preserves plans during preview and language changes, then invalidates only actually changed choices.</summary>
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public async Task ExplicitApplyCombinesLanguageAndTitleWithoutChangingAnAlreadyMatchingFile(string language)
    {
        var fixture = CreateFixture();
        var rules = fixture.Batch.LanguageRules;
        var view = new BatchLanguageRulesView { DataContext = rules };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            await fixture.LoadAsync();
            ApproveAll(fixture);
            await fixture.Batch.ReviewCommand.ExecuteAsync(null);
            var before = fixture.Session.Snapshot;
            var plans = fixture.Batch.Items.Select(item => item.ApprovedPlan).ToArray();
            Configure(fixture);
            rules.PreviewCommand.Execute(null);
            var preview = Assert.IsType<BatchLanguagePreview>(rules.Preview);
            Assert.Same(before, fixture.Session.Snapshot);
            Assert.True(fixture.Batch.HasReview);
            Assert.Equal([0, 1, 3], preview.Items[0].ProposedRetainedTrackIds);
            Assert.Equal([77, 25, 8, 61], preview.Items[1].ProposedRetainedTrackIds);
            Assert.Equal(BatchLanguagePreviewState.Changed, preview.Items[0].State);
            Assert.Equal(BatchLanguagePreviewState.Unchanged, preview.Items[1].State);

            await fixture.Main.ChangeLanguageCommand.ExecuteAsync(language);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(preview, rules.Preview);
            Assert.Same(before, fixture.Session.Snapshot);
            Assert.Equal(" [en].+ ", rules.AudioTitleContains);
            Assert.True(rules.AudioTitleFilterEnabled);
            Assert.False(rules.SubtitleTitleFilterEnabled);
            Assert.Equal(fixture.Localization["RuleAudioTitleFilter"], view.FindControl<CheckBox>("AudioTitleFilterCheckBox")!.Content);
            Assert.True(view.FindControl<CheckBox>("AudioTitleFilterCheckBox")!.IsChecked);
            Assert.True(view.FindControl<TextBox>("AudioTitleContainsTextBox")!.IsEnabled);
            Assert.Equal(" [en].+ ", view.FindControl<TextBox>("AudioTitleContainsTextBox")!.Text);
            Assert.False(view.FindControl<TextBox>("SubtitleTitleContainsTextBox")!.IsEnabled);
            Assert.Equal(plans, fixture.Batch.Items.Select(item => item.ApprovedPlan));

            rules.ApplyCommand.Execute(null);
            Assert.True(rules.IsApplied);
            Assert.Equal([0, 1, 3], fixture.Session.Snapshot.Items[0].CurrentRetainedTrackIds);
            Assert.Equal([77, 25, 8, 61], fixture.Session.Snapshot.Items[1].CurrentRetainedTrackIds);
            Assert.Null(fixture.Batch.Items[0].ApprovedPlan);
            Assert.Same(plans[1], fixture.Batch.Items[1].ApprovedPlan);
            Assert.False(fixture.Batch.HasReview);
            Assert.False(fixture.Batch.RunCommand.CanExecute(null));
            Assert.Equal("Commentary", fixture.Batch.Items[0].Tracks.Single(track => track.Track.Id == 2).Track.Title);
            Assert.Empty(fixture.Backend.RemuxRequests);
            Assert.Equal(0, fixture.Outputs.CleanupCount);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Requires a new explicit preview after every title setting edit without altering approved media choices.</summary>
    [AvaloniaTheory]
    [InlineData("audio-enabled")]
    [InlineData("audio-text")]
    [InlineData("subtitle-enabled")]
    [InlineData("subtitle-text")]
    public async Task TitleConfigurationEditInvalidatesProposalWithoutEditingTheBatch(string edit)
    {
        var fixture = CreateFixture();
        await fixture.LoadAsync();
        ApproveAll(fixture);
        await fixture.Batch.ReviewCommand.ExecuteAsync(null);
        Configure(fixture);
        var rules = fixture.Batch.LanguageRules;
        rules.SubtitleRuleEnabled = true;
        rules.SubtitleLanguages = "en";
        rules.SubtitleTitleFilterEnabled = true;
        rules.SubtitleTitleContains = "subtitle";
        rules.PreviewCommand.Execute(null);
        var previous = rules.Preview!;
        var before = fixture.Session.Snapshot;

        switch (edit)
        {
            case "audio-enabled":
                rules.AudioTitleFilterEnabled = false;
                break;
            case "audio-text":
                rules.AudioTitleContains = "commentary";
                break;
            case "subtitle-enabled":
                rules.SubtitleTitleFilterEnabled = false;
                break;
            case "subtitle-text":
                rules.SubtitleTitleContains = "no match";
                break;
        }

        Assert.False(rules.HasPreview);
        Assert.False(rules.ApplyCommand.CanExecute(null));
        Assert.Empty(rules.Files);
        rules.ApplyCommand.Execute(null);
        Assert.Same(before, fixture.Session.Snapshot);
        Assert.True(fixture.Batch.HasReview);
        rules.PreviewCommand.Execute(null);
        Assert.NotSame(previous, rules.Preview);
        var rejected = fixture.Coordinator.ApplyLanguageRules(previous);
        Assert.Equal(ErrorCode.StalePlan, rejected.Failure!.Code);
        Assert.Same(before, fixture.Session.Snapshot);
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>Displays localized non-blank validation while leaving the whole file and its approval unchanged.</summary>
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public async Task EnabledWhitespaceTitleIsManualReviewWithLocalizedExplanation(string language)
    {
        var fixture = CreateFixture();
        await fixture.LoadAsync();
        ApproveAll(fixture);
        await fixture.Batch.ReviewCommand.ExecuteAsync(null);
        await fixture.Main.ChangeLanguageCommand.ExecuteAsync(language);
        Configure(fixture);
        var rules = fixture.Batch.LanguageRules;
        rules.AudioTitleContains = " \t ";
        var before = fixture.Session.Snapshot;
        rules.PreviewCommand.Execute(null);
        Assert.All(rules.Files, file =>
        {
            Assert.Equal(BatchLanguagePreviewState.ManualReview, file.Item.State);
            Assert.Contains(fixture.Localization["RuleIssueEmptyTitleFilter"], file.IssuesText);
        });
        Assert.False(rules.ApplyCommand.CanExecute(null));
        Assert.Same(before, fixture.Session.Snapshot);
        Assert.True(fixture.Batch.HasReview);
        Assert.All(fixture.Batch.Items, item => Assert.NotNull(item.ApprovedPlan));
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>Clears issued title proposals on New batch without losing language, folder, or rule preferences.</summary>
    [AvaloniaFact]
    public async Task NewBatchRetainsTitleConfigurationButNeverAppliesAnOldProposalToReaddedFiles()
    {
        var fixture = CreateFixture();
        await fixture.LoadAsync();
        await fixture.Main.ChangeLanguageCommand.ExecuteAsync("ru");
        Configure(fixture);
        var rules = fixture.Batch.LanguageRules;
        rules.PreviewCommand.Execute(null);
        var previous = rules.Preview!;
        var folder = fixture.Batch.OutputFolder;
        fixture.Batch.NewBatchCommand.Execute(null);
        fixture.Batch.ConfirmNewBatchCommand.Execute(null);
        Assert.Empty(fixture.Batch.Items);
        Assert.Empty(rules.Files);
        Assert.False(rules.HasPreview);
        Assert.False(rules.IsStale);
        Assert.True(rules.AudioTitleFilterEnabled);
        Assert.Equal(" [en].+ ", rules.AudioTitleContains);
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

    /// <summary>Disables title inputs with the existing shared discovery workspace gate.</summary>
    [AvaloniaFact]
    public async Task ActiveDiscoveryDisablesTitleControlsWithoutLosingTheirConfiguration()
    {
        var fixture = CreateFixture();
        await fixture.LoadAsync();
        Configure(fixture);
        var rules = fixture.Batch.LanguageRules;
        rules.PreviewCommand.Execute(null);
        var preview = rules.Preview;
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
            Assert.False(rules.CanEditAudioLanguages);
            Assert.False(rules.CanEditAudioTitle);
            Assert.False(rules.PreviewCommand.CanExecute(null));
            Assert.False(rules.ApplyCommand.CanExecute(null));
            Assert.Same(preview, rules.Preview);
            Assert.Equal(" [en].+ ", rules.AudioTitleContains);
        }
        finally
        {
            release.TrySetResult();
        }

        await BatchViewModelFixture.Await(operation);
        Assert.True(rules.CanEditAudioTitle);
        Assert.Same(preview, rules.Preview);
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>Uses all-English audio with one non-matching title in A and two matching, reordered audio tracks in B.</summary>
    private static BatchViewModelFixture CreateFixture()
    {
        var fixture = new BatchViewModelFixture();
        var firstPath = fixture.Dialogs.Sources[0];

        foreach (var (path, inspection) in fixture.Backend.Inspections.ToArray())
        {
            fixture.Backend.Inspections[path] = inspection with
            {
                Tracks = inspection.Tracks.Select(track => track.Kind == TrackKind.Audio
                    ? track with { Title = path == firstPath && !track.IsDefault ? "Commentary" : "Main [EN].+ one" }
                    : track).ToImmutableArray()
            };
        }

        return fixture;
    }

    /// <summary>Enables an audio language predicate and one explicitly literal, whitespace-trimmed title constraint.</summary>
    private static void Configure(BatchViewModelFixture fixture)
    {
        var rules = fixture.Batch.LanguageRules;
        rules.AudioRuleEnabled = true;
        rules.AudioLanguages = "en";
        rules.AudioTitleFilterEnabled = true;
        rules.AudioTitleContains = " [en].+ ";
    }

    /// <summary>Approves each current file separately without replacing any file-local Keep choices.</summary>
    private static void ApproveAll(BatchViewModelFixture fixture)
    {
        foreach (var item in fixture.Batch.Items)
        {
            fixture.Batch.SelectedItem = item;
            fixture.Batch.ApproveSelectedCommand.Execute(null);
        }
    }
}
