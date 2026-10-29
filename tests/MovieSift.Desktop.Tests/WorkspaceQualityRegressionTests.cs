using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MovieSift.Application.Ports;
using MovieSift.Application.QueueLists;
using MovieSift.Core.Planning;
using MovieSift.Desktop.Views;
using Xunit;

namespace MovieSift.Desktop.Tests;

/// <summary>Protects direct queue actions, cosmetic track tabs and explicit remembered output choices.</summary>
public sealed class WorkspaceQualityRegressionTests
{
    /// <summary>Removing the targeted row cannot remove the currently selected different source.</summary>
    [AvaloniaFact]
    public async Task QueueRowRemoveTargetsItsOwnItemAndNewQueueActuallyConfirms()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.LoadAsync();
        var first = fixture.Batch.Items[0];
        var second = fixture.Batch.Items[1];
        fixture.Batch.SelectedItem = second;
        fixture.Batch.RemoveItemCommand.Execute(first);
        Assert.DoesNotContain(first, fixture.Batch.Items);
        Assert.Same(second, Assert.Single(fixture.Batch.Items));
        Assert.Same(second, fixture.Batch.SelectedItem);
        var view = new BatchView { DataContext = fixture.Batch };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();
            view.FindControl<Button>("NewBatchButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(fixture.Batch.HasNewBatchConfirmation);
            Assert.Single(fixture.Batch.Items);
            fixture.Batch.KeepCurrentBatchCommand.Execute(null);
            Assert.Single(fixture.Batch.Items);
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Changing the visible track category and language leaves authoritative selections and Review intact.</summary>
    [AvaloniaFact]
    public async Task TrackTabsAndEmptyPresetPickerArePresentationOnly()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.PrepareAsync();
        var snapshot = fixture.Batch.Snapshot;
        var item = fixture.Batch.Items[0];
        var selection = item.Tracks.Select(track => track.Keep).ToArray();
        item.SelectedTrackTab = 1;
        fixture.Batch.AggregateTracks.SelectedTrackTab = 1;
        await fixture.Main.ChangeLanguageCommand.ExecuteAsync("ru");
        Assert.Equal(1, item.SelectedTrackTab);
        Assert.Equal(1, fixture.Batch.AggregateTracks.SelectedTrackTab);
        Assert.Equal(selection, item.Tracks.Select(track => track.Keep));
        Assert.Same(snapshot, fixture.Batch.Snapshot);
        await fixture.Batch.Presets.InitializeAsync();
        Assert.False(fixture.Batch.Presets.CanSelect);
        Assert.False(fixture.Batch.Presets.HasPresets);
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>Remembered replacement is a default only; later inputs and a new queue still need fresh Review.</summary>
    [AvaloniaFact]
    public async Task RememberedChoiceSurvivesNewQueueWithoutGrantingApproval()
    {
        var store = new MemoryOutputPreferences { Value = new(true, OutputMode.ReplaceSource) };
        var fixture = new BatchViewModelFixture(outputPreferences: store);
        await fixture.Batch.InitializePreferencesAsync();
        Assert.True(fixture.Batch.OutputPreferences.RememberChoice);
        Assert.True(fixture.Batch.IsReplaceMode);
        await fixture.Batch.AddFilesCommand.ExecuteAsync(null);
        Assert.All(fixture.Batch.Items, item =>
        {
            Assert.Equal(OutputMode.ReplaceSource, item.Snapshot.OutputMode);
            Assert.Null(item.ApprovedPlan);
        });
        fixture.Batch.NewBatchCommand.Execute(null);
        fixture.Batch.ConfirmNewBatchCommand.Execute(null);
        Assert.Empty(fixture.Batch.Items);
        Assert.True(fixture.Batch.IsReplaceMode);
        Assert.False(fixture.Batch.CanRun);
        fixture.Batch.SelectedOutputModeIndex = (int)OutputMode.Copy;
        await fixture.Batch.OutputPreferences.WhenIdleAsync();
        Assert.Equal(new(true, OutputMode.Copy), store.Value);
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>Remember stores the displayed restored mode, while a mixed queue offers no ambiguous preference.</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RememberChoiceUsesRestoredModesAndRejectsMixedIntent(bool mixed)
    {
        var store = new MemoryOutputPreferences();
        var fixture = new BatchViewModelFixture(initializeQueue: false, outputPreferences: store);
        fixture.QueueStore.List = new(fixture.Backend.Inspections.Keys.Select((source, index) =>
        {
            var mode = mixed && index == 0 ? OutputMode.Copy : OutputMode.ReplaceSource;
            var destination = mode == OutputMode.Copy ? source + ".trimmed.mkv" : source;
            return new QueueListEntry(source, destination, OutputMode: mode);
        }).ToImmutableArray());
        await fixture.Batch.InitializePreferencesAsync();
        await fixture.Batch.Recovery.InitializeAsync();
        await fixture.Batch.Recovery.RestoreCommand.ExecuteAsync(null);
        Assert.Equal(mixed, fixture.Batch.HasMixedOutputModes);
        Assert.Equal(!mixed, fixture.Batch.CanRememberOutputChoice);
        fixture.Batch.OutputPreferences.RememberChoice = true;
        await fixture.Batch.OutputPreferences.WhenIdleAsync();
        Assert.Equal(!mixed, fixture.Batch.OutputPreferences.RememberChoice);
        Assert.Equal(mixed ? new(false, OutputMode.Copy) : new(true, OutputMode.ReplaceSource), store.Value);
        Assert.All(fixture.Batch.Items, item => Assert.Null(item.ApprovedPlan));
        Assert.Empty(fixture.Backend.RemuxRequests);

        fixture.Batch.SelectedOutputModeIndex = (int)OutputMode.Copy;
        await fixture.Batch.OutputPreferences.WhenIdleAsync();
        Assert.True(fixture.Batch.CanRememberOutputChoice);
        Assert.Equal(OutputMode.Copy, store.Value.Mode);
    }

    /// <summary>Expanding file details preserves both the existing shelf and the track viewport's user offset.</summary>
    [AvaloniaFact]
    public async Task ExpandingDetailsDoesNotMoveTheViewportOrCollapseTheFileList()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.LoadAsync();
        var view = new BatchView { DataContext = fixture.Batch };
        var window = new Window { Content = view, Width = 640, Height = 480 };
        window.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();
            var shelf = view.FindControl<Expander>("FileSidebar")!;
            shelf.IsExpanded = true;
            window.UpdateLayout();
            var scroll = view.FindControl<ScrollViewer>("WorkspaceScrollViewer")!;
            scroll.Offset = new Vector(0, 20);
            window.UpdateLayout();
            var offset = scroll.Offset;
            Assert.True(offset.Y > 0);
            fixture.Batch.IsDetailsOpen = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(shelf.IsExpanded);
            Assert.Equal(offset, scroll.Offset);
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            window.Close();
        }
    }

    private sealed class MemoryOutputPreferences : IOutputPreferencesStore
    {
        public OutputPreference Value { get; set; } = new(false, OutputMode.Copy);
        public Task<OutputPreference> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Value);
        public Task SaveAsync(OutputPreference preference, CancellationToken cancellationToken = default)
        {
            Value = preference;
            return Task.CompletedTask;
        }
    }
}
