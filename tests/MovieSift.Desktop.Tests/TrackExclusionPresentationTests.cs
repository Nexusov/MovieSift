using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MovieSift.Core.Media;
using MovieSift.Desktop.ViewModels;
using MovieSift.Desktop.Views;
using Xunit;

namespace MovieSift.Desktop.Tests;

/// <summary>Proves that exclusion edits retained selections in place instead of deleting or reordering presentation rows.</summary>
public sealed class TrackExclusionPresentationTests
{
    /// <summary>The same file card becomes excluded and restorable while video, other tracks and source identity stay intact.</summary>
    [AvaloniaFact]
    public async Task SingleTrackExclusionKeepsCardIdentityAndRestoresItsActualLocalId()
    {
        var fixture = new BatchViewModelFixture();
        var source = fixture.Backend.Inspections.Values.First().Source;
        fixture.Discovery.Result = new([source], []);
        fixture.Dialogs.Sources = [source.FullPath];
        await fixture.LoadAsync();
        var item = Assert.Single(fixture.Batch.Items);
        await fixture.Batch.ReviewCommand.ExecuteAsync(null);
        fixture.Batch.CloseReviewCommand.Execute(null);
        var tracks = item.Tracks.ToArray();
        var target = tracks.Single(track => track.Track.Id == 1);
        var view = new BatchView { DataContext = fixture.Batch };
        var window = new Window { Content = view, Width = 1040, Height = 800 };
        window.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();
            var editor = Assert.Single(view.GetVisualDescendants().OfType<SingleFileTracksView>(), control => control.IsEffectivelyVisible);
            var cards = editor.GetVisualDescendants().OfType<Border>().Where(control => control.Classes.Contains("trackCard")).ToArray();
            var card = Assert.Single(cards, control => ReferenceEquals(control.DataContext, target));
            var action = Assert.Single(card.GetVisualDescendants().OfType<TrackSelectionActionView>());
            var button = action.FindControl<Button>("TrackSelectionActionButton")!;
            Assert.Same(target.ToggleSelectionCommand, button.Command);
            Activate(window, button);
            Assert.True(target.IsExcluded);
            Assert.Contains("excluded", card.Classes);
            Assert.Equal(fixture.Localization["TrackRestore"], action.Text);
            Assert.True(button.IsEffectivelyEnabled);
            Assert.Equal(1, button.Opacity);
            Assert.Equal(1, card.Opacity);
            Assert.Equal(cards, editor.GetVisualDescendants().OfType<Border>().Where(control => control.Classes.Contains("trackCard")));
            Assert.Equal(tracks, item.Tracks);
            Assert.All(tracks.Where(track => track.Track.Id != 1), track => Assert.True(track.Keep));
            Assert.Null(item.ApprovedPlan);
            Assert.False(fixture.Batch.CanRun);
            Activate(window, button);
            Assert.False(target.IsExcluded);
            Assert.DoesNotContain("excluded", card.Classes);
            Assert.All(tracks, track => Assert.True(track.Keep));
            Assert.Equal(cards, editor.GetVisualDescendants().OfType<Border>().Where(control => control.Classes.Contains("trackCard")));
            item.Included = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(button.IsEffectivelyEnabled);
            var excludedRevision = fixture.Batch.Snapshot.Revision;
            target.ToggleSelectionCommand.Execute(null);
            Assert.True(target.Keep);
            Assert.Equal(excludedRevision, fixture.Batch.Snapshot.Revision);
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Mixed groups exclude editable retained members atomically and restore through each file's real identifiers.</summary>
    [AvaloniaFact]
    public async Task MixedGroupAndMemberActionsPreserveRowsAndIndependentFileIds()
    {
        var fixture = new BatchViewModelFixture();
        var first = fixture.Backend.Inspections.Values.First();
        var second = fixture.Backend.Inspections.Values.Skip(1).First();
        var shared = first.Tracks.Single(track => track.Id == 1);
        fixture.Backend.Inspections[second.Source.FullPath] = second with
        {
            Tracks = second.Tracks.Select(track => track.Id == 25 ? shared with { Id = 25, Uid = 125 } : track).ToImmutableArray()
        };
        await fixture.LoadAsync();
        var group = Assert.Single(fixture.Batch.AggregateTracks.Groups, candidate => candidate.Snapshot.Key.Title == shared.Title);
        group.IsExpanded = true;
        var members = group.Members.ToArray();
        group.Members[0].Keep = false;
        var view = new AggregateTracksView { DataContext = fixture.Batch.AggregateTracks };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();
            var card = Assert.Single(view.GetVisualDescendants().OfType<Border>(), control =>
                control.Classes.Contains("groupCard") && ReferenceEquals(control.DataContext, group));
            var action = Assert.Single(card.GetVisualDescendants().OfType<TrackSelectionActionView>(), control => ReferenceEquals(control.DataContext, group));
            var button = action.FindControl<Button>("TrackSelectionActionButton")!;
            Assert.Null(group.Keep);
            Assert.False(group.RestoresExcluded);
            var revision = fixture.Batch.Snapshot.Revision;
            Activate(window, button);
            Assert.Equal(revision + 1, fixture.Batch.Snapshot.Revision);
            Assert.True(group.IsExcluded);
            Assert.True(group.RestoresExcluded);
            Assert.Contains("excluded", card.Classes);
            Assert.Equal(members, group.Members);
            Assert.True(group.IsExpanded);
            Assert.DoesNotContain(1, fixture.Batch.Items[0].Snapshot.CurrentRetainedTrackIds);
            Assert.DoesNotContain(25, fixture.Batch.Items[1].Snapshot.CurrentRetainedTrackIds);
            Assert.All(fixture.Batch.Items.SelectMany(item => item.Tracks).Where(track => track.Track.Kind == TrackKind.Video), track => Assert.True(track.Keep));
            var member = members[0];
            var memberAction = Assert.Single(card.GetVisualDescendants().OfType<TrackSelectionActionView>(), control => ReferenceEquals(control.DataContext, member));
            Activate(window, memberAction.FindControl<Button>("TrackSelectionActionButton")!);
            Assert.True(member.Keep);
            Assert.Null(group.Keep);
            Assert.False(group.RestoresExcluded);
            Activate(window, button);
            Assert.True(group.IsExcluded);
            Activate(window, button);
            Assert.True(group.Keep);
            Assert.Equal(members, group.Members);
            Assert.Same(card, Assert.Single(view.GetVisualDescendants().OfType<Border>(), control =>
                control.Classes.Contains("groupCard") && ReferenceEquals(control.DataContext, group)));
            Assert.Contains(1, fixture.Batch.Items[0].Snapshot.CurrentRetainedTrackIds);
            Assert.Contains(25, fixture.Batch.Items[1].Snapshot.CurrentRetainedTrackIds);
            await fixture.Batch.ReviewCommand.ExecuteAsync(null);
            await fixture.Batch.RunCommand.ExecuteAsync(null);
            Assert.False(group.ToggleKeepCommand.CanExecute(null));
            Assert.All(group.Members, row => Assert.False(row.ToggleSelectionCommand.CanExecute(null)));
            var completedRevision = fixture.Batch.Snapshot.Revision;
            group.ToggleKeepCommand.Execute(null);
            member.ToggleSelectionCommand.Execute(null);
            Assert.True(member.Keep);
            Assert.Equal(completedRevision, fixture.Batch.Snapshot.Revision);
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Activates the actual current button through native headless key input without assuming tab counts.</summary>
    private static void Activate(Window window, Button button)
    {
        Assert.True(button.Focus());
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }
}
