using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MovieSift.Application.Batches;
using MovieSift.Core.Failures;
using MovieSift.Core.Media;
using MovieSift.Desktop.ViewModels;
using MovieSift.Desktop.Views;
using Xunit;

namespace MovieSift.Desktop.Tests;

/// <summary>Exercises the direct one-row presentation without creating another selection or plan authority.</summary>
public sealed class SingleFileWorkspaceTests
{
    /// <summary>The direct editor retains the existing reversible remove-all-subtitles action and its selection authority.</summary>
    [AvaloniaFact]
    public async Task SingleFileRemoveAllSubtitlesPreservesAudioVideoAndCanBeReversed()
    {
        var fixture = SingleFileFixture();
        await fixture.LoadAsync();
        var item = Assert.Single(fixture.Batch.Items);
        item.Tracks.Single(track => track.Track.Id == 1).Keep = false;
        await fixture.Batch.ReviewCommand.ExecuteAsync(null);
        Assert.NotNull(item.ApprovedPlan);
        fixture.Batch.CloseReviewCommand.Execute(null);
        var otherChoices = item.Tracks.Where(track => track.Track.Kind != TrackKind.Subtitles)
            .Select(track => (track.Track.Id, track.Keep)).ToArray();
        var revision = fixture.Batch.Snapshot.Revision;
        var view = new BatchView { DataContext = fixture.Batch };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();
            var editor = Assert.Single(view.GetVisualDescendants().OfType<SingleFileTracksView>(), control => control.IsEffectivelyVisible);
            var button = editor.FindControl<Button>("SingleFileRemoveAllSubtitlesButton")!;
            Assert.True(button.IsEffectivelyVisible);
            Assert.Same(fixture.Batch.AggregateTracks.RemoveAllSubtitlesCommand, button.Command);
            Assert.Equal(fixture.Localization["RemoveAllSubtitles"], button.Content);
            Assert.True(button.IsEffectivelyEnabled);
            Assert.True(button.Focus());
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(revision + 1, fixture.Batch.Snapshot.Revision);
            Assert.All(item.Tracks.Where(track => track.Track.Kind == TrackKind.Subtitles), track => Assert.False(track.Keep));
            Assert.Equal(otherChoices, item.Tracks.Where(track => track.Track.Kind != TrackKind.Subtitles)
                .Select(track => (track.Track.Id, track.Keep)));
            Assert.Null(item.ApprovedPlan);
            Assert.False(fixture.Batch.CanRun);
            Assert.False(button.IsEffectivelyEnabled);
            var subtitle = Assert.Single(item.Tracks, track => track.Track.Kind == TrackKind.Subtitles);
            subtitle.Keep = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(subtitle.Keep);
            Assert.True(button.IsEffectivelyEnabled);
            Assert.Equal(otherChoices, item.Tracks.Where(track => track.Track.Kind != TrackKind.Subtitles)
                .Select(track => (track.Track.Id, track.Keep)));
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>One accepted file exposes actual local IDs directly and keeps details free of a duplicated track editor.</summary>
    [AvaloniaFact]
    public async Task SoleFileShowsActualTracksAndUsesExistingConsentAndReview()
    {
        var fixture = SingleFileFixture();
        await fixture.LoadAsync();
        var item = Assert.Single(fixture.Batch.Items);
        var view = new BatchView { DataContext = fixture.Batch };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.True(fixture.Batch.IsSingleFile);
            Assert.Same(item, fixture.Batch.SingleFileItem);
            Assert.True(view.FindControl<ContentControl>("SingleFileEditor")!.IsEffectivelyVisible);
            Assert.False(view.FindControl<AggregateTracksView>("AggregateEditor")!.IsEffectivelyVisible);
            var editor = Assert.Single(view.GetVisualDescendants().OfType<SingleFileTracksView>(), control => control.IsEffectivelyVisible);
            var checkboxes = editor.GetVisualDescendants().OfType<CheckBox>().Where(control => control.DataContext is TrackViewModel).ToArray();
            Assert.Equal([0, 1, 2, 3], checkboxes.Select(control => ((TrackViewModel)control.DataContext!).Track.Id));
            var video = Assert.Single(checkboxes, control => ((TrackViewModel)control.DataContext!).Track.Kind == TrackKind.Video);
            Assert.False(video.IsEnabled);
            item.Tracks[0].Keep = false;
            Assert.True(item.Tracks[0].Keep);
            item.Tracks.Single(track => track.Track.Id == 1).Keep = false;
            await fixture.Batch.ReviewCommand.ExecuteAsync(null);
            Assert.Equal([0, 2, 3], item.ApprovedPlan!.RetainedTracks.Select(track => track.Id));
            Assert.True(item.RemovesDefaultAudio);
            fixture.Batch.CloseReviewCommand.Execute(null);

            foreach (var track in item.Tracks.Where(track => track.Track.Kind == TrackKind.Audio))
            {
                track.Keep = false;
            }

            Dispatcher.UIThread.RunJobs();
            Assert.True(item.NeedsZeroAudioConfirmation);
            var consent = Assert.Single(editor.GetVisualDescendants().OfType<CheckBox>(), control => control.Name == "FileZeroAudioConfirmation");
            Assert.True(consent.IsEffectivelyVisible);
            await fixture.Batch.ReviewCommand.ExecuteAsync(null);
            Assert.False(fixture.Batch.CanRun);
            fixture.Batch.CloseReviewCommand.Execute(null);
            consent.IsChecked = true;
            await fixture.Batch.ReviewCommand.ExecuteAsync(null);
            Assert.True(fixture.Batch.CanRun);
            Assert.True(item.ApprovedPlan!.ZeroAudioConfirmed);
            Assert.DoesNotContain(item.ApprovedPlan.RetainedTracks, track => track.Kind == TrackKind.Audio);
            fixture.Batch.CloseReviewCommand.Execute(null);
            fixture.Batch.IsDetailsOpen = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(fixture.Localization["WorkspaceDetails"], view.FindControl<Expander>("SelectedFileDetails")!.Header);
            var details = Assert.Single(view.GetVisualDescendants().OfType<BatchFileDetailsView>(), control => control.IsEffectivelyVisible);
            Assert.False(details.ShowTrackSelection);
            Assert.DoesNotContain(details.GetVisualDescendants().OfType<FileTracksView>(), control => control.IsEffectivelyVisible);
            Assert.Single(view.GetVisualDescendants().OfType<FileTracksView>(), control => control.IsEffectivelyVisible);
            Assert.Contains(details.GetVisualDescendants().OfType<TextBox>(), control => control.IsEffectivelyVisible && !control.IsReadOnly);
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Adding and removing rows changes only presentation scope, retaining stable editors and actual per-file choices.</summary>
    [AvaloniaFact]
    public async Task OneTwoOneTransitionPreservesSelectionsAndDoesNotUseIncludedCount()
    {
        var fixture = SingleFileFixture();
        await fixture.LoadAsync();
        var first = Assert.Single(fixture.Batch.Items);
        var tracks = first.Tracks.ToArray();
        var view = new BatchView { DataContext = fixture.Batch };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.False(view.FindControl<AggregateTracksView>("AggregateEditor")!.IsEffectivelyVisible);
            first.Tracks.Single(track => track.Track.Id == 1).Keep = false;
            await fixture.Batch.ReviewCommand.ExecuteAsync(null);
            var plan = first.ApprovedPlan;
            Assert.NotNull(plan);
            fixture.Batch.CloseReviewCommand.Execute(null);
            fixture.Batch.IsDetailsOpen = true;
            var secondSource = fixture.Backend.Inspections.Values.Skip(1).First().Source;
            fixture.Dialogs.Sources = [secondSource.FullPath];
            fixture.Discovery.Result = new([secondSource], []);
            await fixture.Batch.AddFilesCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, fixture.Batch.Items.Count);
            Assert.False(fixture.Batch.IsSingleFile);
            Assert.Null(fixture.Batch.SingleFileItem);
            Assert.True(view.FindControl<AggregateTracksView>("AggregateEditor")!.IsEffectivelyVisible);
            Assert.False(view.FindControl<ContentControl>("SingleFileEditor")!.IsEffectivelyVisible);
            var multiDetails = Assert.Single(view.GetVisualDescendants().OfType<BatchFileDetailsView>(), control => control.IsEffectivelyVisible);
            Assert.True(multiDetails.ShowTrackSelection);
            Assert.Single(multiDetails.GetVisualDescendants().OfType<FileTracksView>(), control => control.IsEffectivelyVisible);
            var second = fixture.Batch.Items.Single(item => item.Id != first.Id);
            second.Included = false;
            Assert.False(fixture.Batch.IsSingleFile);
            Assert.Same(plan, first.ApprovedPlan);
            Assert.Equal(tracks, first.Tracks);
            fixture.Batch.SelectedItem = second;
            fixture.Batch.RemoveSelectedCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(fixture.Batch.IsSingleFile);
            Assert.Same(first, fixture.Batch.SingleFileItem);
            Assert.True(view.FindControl<ContentControl>("SingleFileEditor")!.IsEffectivelyVisible);
            Assert.False(view.FindControl<AggregateTracksView>("AggregateEditor")!.IsEffectivelyVisible);
            var singleDetails = Assert.Single(view.GetVisualDescendants().OfType<BatchFileDetailsView>(), control => control.IsEffectivelyVisible);
            Assert.False(singleDetails.ShowTrackSelection);
            Assert.Single(view.GetVisualDescendants().OfType<FileTracksView>(), control => control.IsEffectivelyVisible);
            Assert.Equal(tracks, first.Tracks);
            Assert.False(first.Tracks.Single(track => track.Track.Id == 1).Keep);
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Exclusion and inspection failure never invent aggregate data or turn a one-row list into an empty workspace.</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SoleExcludedOrFailedRowRetainsItsOwnState(bool inspectionFails)
    {
        var fixture = SingleFileFixture();

        if (inspectionFails)
        {
            fixture.Backend.InspectionFailures[fixture.Dialogs.Sources[0]] = new(ErrorCode.ToolFailed, "Controlled inspection failure");
        }

        await fixture.LoadAsync();
        var item = Assert.Single(fixture.Batch.Items);

        if (!inspectionFails)
        {
            item.Included = false;
        }

        var view = new BatchView { DataContext = fixture.Batch };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.True(fixture.Batch.IsSingleFile);
            Assert.Same(item, fixture.Batch.SingleFileItem);
            Assert.True(view.FindControl<ContentControl>("SingleFileEditor")!.IsEffectivelyVisible);
            Assert.False(view.FindControl<AggregateTracksView>("AggregateEditor")!.IsEffectivelyVisible);

            var removeSubtitles = Assert.Single(view.GetVisualDescendants().OfType<Button>(),
                button => button.Name == "SingleFileRemoveAllSubtitlesButton" && button.IsEffectivelyVisible);
            Assert.False(removeSubtitles.IsEffectivelyEnabled);

            if (inspectionFails)
            {
                Assert.Equal(BatchItemState.Failed, item.Snapshot.State);
                Assert.Empty(item.Tracks);
                Assert.True(item.HasError);
                Assert.False(item.CanEditPlan);
            }

            if (!inspectionFails)
            {
                Assert.False(item.Included);
                Assert.Equal([0, 1, 2, 3], item.Tracks.Select(track => track.Track.Id));
            }

            Assert.False(fixture.Batch.CanRun);
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Accepts one source through the established discovery fixture rather than mutating session collections directly.</summary>
    private static BatchViewModelFixture SingleFileFixture()
    {
        var fixture = new BatchViewModelFixture();
        var source = fixture.Backend.Inspections.Values.First().Source;
        fixture.Dialogs.Sources = [source.FullPath];
        fixture.Discovery.Result = new([source], []);
        return fixture;
    }
}
