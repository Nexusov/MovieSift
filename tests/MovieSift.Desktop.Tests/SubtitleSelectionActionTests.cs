using System.Collections.Immutable;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MovieSift.Application.Batches;
using MovieSift.Core.Media;
using MovieSift.Desktop.ViewModels;
using MovieSift.Desktop.Views;
using Xunit;

namespace MovieSift.Desktop.Tests;

/// <summary>Uses actual workspace controls for the existing reversible subtitle-selection edit.</summary>
public sealed class SubtitleSelectionActionTests
{
    /// <summary>Removes all heterogeneous subtitle occurrences in one action and still requires explicit Review.</summary>
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public async Task SubtitleActionPreservesAudioAndRequiresFreshReview(string language)
    {
        var fixture = new BatchViewModelFixture();
        var first = fixture.Backend.Inspections.Values.First();
        var subtitle = first.Tracks.Single(track => track.Kind == TrackKind.Subtitles);
        fixture.Backend.Inspections[first.Source.FullPath] = first with
        {
            Tracks = first.Tracks.AddRange(new[] {
                subtitle with { Id = 90, Uid = 90 },
                subtitle with { Id = 91, Uid = 91, Language = "und", LanguageIetf = null } })
        };
        await fixture.LoadAsync();
        await fixture.Main.ChangeLanguageCommand.ExecuteAsync(language);
        var audio = fixture.Batch.Items[0].Tracks.First(track => track.Track.Kind == TrackKind.Audio);
        audio.Keep = false;
        await fixture.Batch.ReviewCommand.ExecuteAsync(null);
        var before = fixture.Batch.Items.ToDictionary(item => item.Id, item => item.Snapshot);
        var revision = fixture.Batch.Snapshot.Revision;
        fixture.Batch.AggregateTracks.SelectedTrackTab = 1;
        var view = new AggregateTracksView { DataContext = fixture.Batch.AggregateTracks };
        var window = new Window { Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var button = view.FindControl<Button>("RemoveAllSubtitlesButton")!;
            Assert.Equal(fixture.Localization["RemoveAllSubtitles"], button.Content);
            Assert.True(button.IsEnabled);
            Assert.True(button.Focus());
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(revision + 1, fixture.Batch.Snapshot.Revision);
            Assert.False(audio.Keep);
            foreach (var item in fixture.Batch.Items)
            {
                Assert.All(item.Tracks.Where(track => track.Track.Kind == TrackKind.Subtitles), track => Assert.False(track.Keep));
                Assert.Equal(before[item.Id].CurrentRetainedTrackIds.Where(identifier =>
                    item.Snapshot.Inspection!.Tracks.Single(track => track.Id == identifier).Kind != TrackKind.Subtitles),
                    item.Snapshot.CurrentRetainedTrackIds);
                Assert.Null(item.ApprovedPlan);
            }

            Assert.False(fixture.Batch.HasReview);
            Assert.False(fixture.Batch.CanRun);
            Assert.False(button.IsEffectivelyEnabled);
            Assert.False(fixture.Batch.AggregateTracks.RemoveAllSubtitlesCommand.CanExecute(null));
            Assert.Empty(fixture.Backend.RemuxRequests);
            Assert.Equal(0, fixture.Outputs.CleanupCount);
            await fixture.Batch.ReviewCommand.ExecuteAsync(null);
            Assert.True(fixture.Batch.CanRun);
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A file without subtitles keeps its unrelated audio edits and valid review when no change applies.</summary>
    [AvaloniaFact]
    public async Task NoSubtitleActionIsDisabledWithoutRevokingReview()
    {
        var fixture = new BatchViewModelFixture();
        foreach (var (path, inspection) in fixture.Backend.Inspections.ToArray())
        {
            fixture.Backend.Inspections[path] = inspection with
            {
                Tracks = inspection.Tracks.Where(track => track.Kind != TrackKind.Subtitles).ToImmutableArray()
            };
        }

        await fixture.LoadAsync();
        fixture.Batch.Items[0].Tracks.Single(track => track.Track.IsDefault).Keep = false;
        await fixture.Batch.ReviewCommand.ExecuteAsync(null);
        var snapshot = fixture.Session.Snapshot;
        Assert.False(fixture.Batch.AggregateTracks.RemoveAllSubtitlesCommand.CanExecute(null));
        fixture.Batch.AggregateTracks.RemoveAllSubtitlesCommand.Execute(null);
        Assert.Same(snapshot, fixture.Session.Snapshot);
        Assert.True(fixture.Batch.CanRun);
    }

    /// <summary>The one-file shortcut cannot carry silent-output consent into a different selection.</summary>
    [AvaloniaFact]
    public async Task OneFileSubtitleEditRequiresFreshZeroAudioConsent()
    {
        var fixture = new ViewModelFixture();
        await fixture.LoadAsync();
        foreach (var track in fixture.Item.Tracks.Where(track => track.Track.Kind == TrackKind.Audio))
        {
            track.Keep = false;
        }

        fixture.Item.ZeroAudioConfirmed = true;
        await fixture.Workspace.ReviewCommand.ExecuteAsync(null);
        Assert.True(fixture.Workspace.CanRun);
        fixture.Workspace.AggregateTracks.RemoveAllSubtitlesCommand.Execute(null);
        Assert.False(fixture.Item.ZeroAudioConfirmed);
        Assert.False(fixture.Workspace.HasReview);
        Assert.False(fixture.Workspace.HasNewBatchConfirmation);
        await fixture.Workspace.ReviewCommand.ExecuteAsync(null);
        Assert.False(fixture.Workspace.CanRun);
        fixture.Item.ZeroAudioConfirmed = true;
        await fixture.Workspace.ReviewCommand.ExecuteAsync(null);
        Assert.True(fixture.Workspace.CanRun);
        Assert.Equal(0, fixture.Backend.RemuxCount);
    }

    /// <summary>Labels repeated controls with concrete file/track identities and translates operations only.</summary>
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public async Task MemberAndInclusionNamesIdentifyTheirOperationsAndTargets(string language)
    {
        var fixture = new BatchViewModelFixture();
        await fixture.LoadAsync();
        await fixture.Main.ChangeLanguageCommand.ExecuteAsync(language);
        foreach (var group in fixture.Batch.AggregateTracks.Groups)
        {
            group.IsExpanded = true;
        }

        var window = new Window { Content = new BatchView { DataContext = fixture.Batch }, Width = 1040, Height = 800 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var members = window.GetVisualDescendants().OfType<Button>()
                .Where(control => control.DataContext is AggregateTrackMemberViewModel member &&
                    ReferenceEquals(control.Command, member.ToggleSelectionCommand)).ToArray();
            Assert.NotEmpty(members);
            Assert.Equal(members.Length, members.Select(AutomationProperties.GetName).Distinct(StringComparer.Ordinal).Count());
            foreach (var control in members)
            {
                var member = (AggregateTrackMemberViewModel)control.DataContext!;
                Assert.Contains(member.SourcePath, AutomationProperties.GetName(control));
                Assert.Contains(member.IdentifierText, AutomationProperties.GetName(control));
                Assert.Equal(member.SelectionActionAccessibleName, AutomationProperties.GetName(control));
                Assert.Equal(!member.IsRequired, control.IsEffectivelyVisible);
                Assert.Equal(member.CanEdit, control.IsEffectivelyEnabled);
            }

            var inclusions = window.GetVisualDescendants().OfType<CheckBox>()
                .Where(control => control.DataContext is BatchItemViewModel).ToArray();
            Assert.Equal(2, inclusions.Length);
            Assert.All(inclusions, control => Assert.Contains(((BatchItemViewModel)control.DataContext!).SourcePath,
                AutomationProperties.GetName(control)));
            Assert.All(window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>()
                .Where(control => control is not CheckBox && control.DataContext is AggregateTrackGroupViewModel), control =>
                Assert.Equal(((AggregateTrackGroupViewModel)control.DataContext!).MembersAccessibleName,
                    AutomationProperties.GetName(control)));
        }
        finally
        {
            window.Close();
        }
    }
}
