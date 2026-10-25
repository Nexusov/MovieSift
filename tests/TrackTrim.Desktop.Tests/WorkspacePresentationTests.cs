using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TrackTrim.Application.Batches;
using TrackTrim.Core.Media;
using TrackTrim.Desktop.Views;
using Xunit;

namespace TrackTrim.Desktop.Tests;

/// <summary>Checks contextual UX and structured review through the existing workspace authority.</summary>
public sealed class WorkspacePresentationTests
{
    /// <summary>Rules and preset management remain reachable before the first source is added.</summary>
    [AvaloniaFact]
    public async Task EmptyWorkspaceOpensActualPresetPanelWithoutMediaOrReview()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.Main.InitializeAsync();
        var view = new BatchView { DataContext = fixture.Batch };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            view.FindControl<Button>("OpenRulesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(fixture.Batch.IsRulesOpen);
            Assert.True(view.FindControl<Border>("RulesPanel")!.IsEffectivelyVisible);
            Assert.True(view.FindControl<RulePresetsView>("WorkspacePresets")!.IsEffectivelyVisible);
            fixture.Batch.Presets.PresetName = "Empty workspace — правила";
            await fixture.Batch.Presets.SaveAsNewCommand.ExecuteAsync(null);
            Assert.Single(fixture.Batch.Presets.Presets);
            Assert.Empty(fixture.Batch.Items);
            Assert.False(fixture.Batch.HasReview);
            Assert.Empty(fixture.Backend.RemuxRequests);
            fixture.Batch.IsRulesOpen = false;
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.FindControl<Border>("EmptyDropTarget")!.IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Keeps rapid keyboard activation on Review from crossing into the separate execution control.</summary>
    [AvaloniaFact]
    public async Task RepeatedReviewSpaceCannotActivateRunAndReviewFocusesItsHeading()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.LoadAsync();
        fixture.Batch.Items[0].Tracks.Single(track => track.Track.Id == 1).Keep = false;
        var view = new BatchView { DataContext = fixture.Batch };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();
            var reviewButton = view.FindControl<Button>("ReviewBatchButton")!;
            Assert.True(reviewButton.Focus());
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            await fixture.Batch.ReviewCommand.ExecutionTask!;
            Dispatcher.UIThread.RunJobs();
            Assert.True(fixture.Batch.IsReviewOpen);
            Assert.True(fixture.Batch.HasReview);
            Assert.Empty(fixture.Backend.RemuxRequests);
            var heading = Assert.Single(view.GetVisualDescendants().OfType<TextBlock>(), control => control.Name == "ReviewHeading");
            Assert.True(heading.IsFocused);
            var runButton = Assert.Single(view.GetVisualDescendants().OfType<Button>(), control => control.Name == "StartBatchButton");
            Assert.False(runButton.IsFocused);
            Assert.NotSame(reviewButton, runButton);
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Displays separate actual immutable plans and never turns repeated Review into execution.</summary>
    [AvaloniaFact]
    public async Task StructuredReviewShowsChangedAndUnchangedPlansWithoutStartingJobs()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.LoadAsync();
        var batch = fixture.Batch;
        var first = batch.Items[0];
        first.Tracks.Single(track => track.Track.Id == 1).Keep = false;
        Assert.False(batch.IsReviewOpen);
        await batch.ReviewCommand.ExecuteAsync(null);
        Assert.True(batch.IsReviewOpen);
        Assert.Equal(2, batch.ReviewRows.Count);
        Assert.Equal(1, batch.ReviewedCopyCount);
        Assert.Equal(1, batch.ReviewedNoChangeCount);
        var changed = Assert.Single(batch.ReviewRows, row => row.Item.Id == first.Id);
        Assert.Same(first.ApprovedPlan, changed.Plan);
        Assert.Same(batch.Snapshot.Review!.Items.Single(item => item.ItemId == first.Id).Plan, changed.Plan);
        Assert.Equal(first.SourcePath, changed.SourcePath);
        Assert.Equal(Path.GetFileName(first.SourcePath), changed.SourceName);
        Assert.Equal(first.OutputPath, changed.DestinationPath);
        Assert.Contains("Audio A default", changed.RemovedTracksText);
        Assert.DoesNotContain("Audio A default", changed.RetainedTracksText);
        Assert.True(changed.RemovesDefaultAudio);
        Assert.NotEmpty(changed.DefaultAudioText);
        Assert.True(Assert.Single(batch.ReviewRows, row => row.Item.Id != first.Id).NoChange);
        await batch.ReviewCommand.ExecuteAsync(null);
        Assert.Same(changed, Assert.Single(batch.ReviewRows, row => row.Item.Id == first.Id));
        Assert.Empty(fixture.Backend.RemuxRequests);
        var review = batch.Snapshot.Review;
        await fixture.Main.ChangeLanguageCommand.ExecuteAsync("ru");
        Assert.Same(review, batch.Snapshot.Review);
        Assert.Contains("Audio A default", changed.RemovedTracksText);
        await fixture.Main.ChangeLanguageCommand.ExecuteAsync("en");
        Assert.Same(review, batch.Snapshot.Review);
        batch.CloseReviewCommand.Execute(null);
        Assert.False(batch.IsReviewOpen);
        Assert.Same(review, batch.Snapshot.Review);
        first.Tracks.Single(track => track.Track.Id == 1).Keep = true;
        Assert.False(batch.CanRun);
        Assert.Null(changed.Plan);
    }

    /// <summary>Shows actionable per-file silent-output consent and routes it through the existing editor.</summary>
    [AvaloniaFact]
    public async Task ReviewFailureRowsExposeFileSpecificZeroAudioConsent()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.LoadAsync();
        var batch = fixture.Batch;
        var first = batch.Items[0];

        foreach (var track in first.Tracks.Where(track => track.Track.Kind == TrackKind.Audio))
        {
            track.Keep = false;
        }

        await batch.ReviewCommand.ExecuteAsync(null);
        Assert.True(batch.IsReviewOpen);
        Assert.False(batch.CanRun);
        var row = Assert.Single(batch.ReviewRows, candidate => candidate.Item.Id == first.Id);
        Assert.True(row.HasFailure);
        Assert.True(row.NeedsZeroAudioConfirmation);
        Assert.Equal(fixture.Localization["ReviewNextConfirmAudio"], row.NextActionText);
        row.ZeroAudioConfirmed = true;
        Assert.True(first.ZeroAudioConfirmed);
        Assert.False(batch.CanRun);
        await batch.ReviewCommand.ExecuteAsync(null);
        Assert.Same(row, Assert.Single(batch.ReviewRows, candidate => candidate.Item.Id == first.Id));
        Assert.False(row.HasFailure);
        Assert.True(row.Plan!.ZeroAudioConfirmed);
        Assert.DoesNotContain(row.Plan.RetainedTracks, track => track.Kind == TrackKind.Audio);
        Assert.True(batch.CanRun);
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>Offers the existing no-copy completion path while keeping inactive queue controls contextual.</summary>
    [AvaloniaFact]
    public async Task AllNoChangeReviewFinishesWithoutCopiesAndNewQueueResetsPresentation()
    {
        var fixture = new BatchViewModelFixture();
        var batch = fixture.Batch;
        Assert.True(batch.IsEmpty);
        Assert.False(batch.ShowNewQueue);
        Assert.False(batch.CanCancel);
        Assert.False(batch.CanPause);
        await fixture.LoadAsync();
        Assert.True(batch.HasItems);
        Assert.True(batch.ShowNewQueue);
        Assert.False(batch.HasPendingInspections);
        await batch.ReviewCommand.ExecuteAsync(null);
        Assert.Equal(0, batch.ReviewedCopyCount);
        Assert.Equal(2, batch.ReviewedNoChangeCount);
        Assert.Equal(fixture.Localization["RunNoChanges"], batch.RunActionText);
        await batch.RunCommand.ExecuteAsync(null);
        Assert.False(batch.IsReviewOpen);
        Assert.Equal(BatchPhase.Completed, batch.Snapshot.Phase);
        Assert.All(batch.Items, item => Assert.Equal(BatchItemState.NoChange, item.Snapshot.State));
        Assert.Empty(fixture.Backend.RemuxRequests);
        Assert.Equal(0, fixture.Outputs.CleanupCount);
        batch.NewBatchCommand.Execute(null);
        batch.ConfirmNewBatchCommand.Execute(null);
        Assert.True(batch.IsEmpty);
        Assert.False(batch.IsReviewOpen);
        Assert.False(batch.IsDetailsOpen);
        Assert.Empty(batch.ReviewRows);
        Assert.False(batch.CanRun);
    }

    /// <summary>Dropped files enter the same discovery pipeline and are rejected during conflicting UI work.</summary>
    [AvaloniaFact]
    public async Task DroppedFilesReuseBoundedDiscoveryAndTheExistingBusyGate()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.QueueInitialization;
        var entered = BatchViewModelFixture.Signal();
        var release = BatchViewModelFixture.Signal();
        var sources = fixture.Backend.Inspections.Values.Select(inspection => inspection.Source).ToImmutableArray();
        var discoveries = 0;
        fixture.Discovery.OnDiscover = async request =>
        {
            discoveries++;
            entered.TrySetResult();
            await release.Task.WaitAsync(request.CancellationToken);
            return new(sources, []);
        };
        var paths = sources.Select(source => source.FullPath).ToArray();
        var dropping = fixture.Batch.AddDroppedFilesAsync(paths);

        try
        {
            await BatchViewModelFixture.Await(entered.Task);
            Assert.True(fixture.Batch.CanCancel);
            Assert.False(fixture.Batch.CanEdit);
            await fixture.Batch.AddDroppedFilesAsync(paths);
            Assert.Equal(1, discoveries);
            Assert.Equal(BatchInputKind.ExplicitFiles, fixture.Discovery.LastRequest!.Kind);
            Assert.Equal(paths, fixture.Discovery.LastRequest.Paths);
        }
        finally
        {
            release.TrySetResult();
        }

        await BatchViewModelFixture.Await(dropping);
        Assert.Equal(2, fixture.Batch.Items.Count);
        Assert.All(fixture.Batch.Items, item => Assert.All(item.Tracks, track => Assert.True(track.Keep)));
        Assert.All(fixture.Batch.Items, item => Assert.Null(item.ApprovedPlan));
        Assert.False(fixture.Batch.HasReview);
        Assert.Empty(fixture.Backend.RemuxRequests);
        fixture.Batch.OpenSelectedDetailsCommand.Execute(null);
        Assert.True(fixture.Batch.IsDetailsOpen);
        Assert.Equal(Path.GetFileName(fixture.Batch.SelectedItem!.SourcePath), fixture.Batch.SelectedItem.FileName);
        Assert.NotEmpty(fixture.Batch.SelectedItem.ContextPath);
    }
}
