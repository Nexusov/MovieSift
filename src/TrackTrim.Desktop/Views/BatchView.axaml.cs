using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TrackTrim.Application.Batches;
using TrackTrim.Desktop.ViewModels;

namespace TrackTrim.Desktop.Views;

/// <summary>Owns responsive placement, local drop input and focus, never plans or media execution.</summary>
public sealed partial class BatchView : UserControl
{
    private const double NarrowWorkspaceWidth = 850;
    private const double NarrowFileListHeight = 140;
    private BatchViewModel? observedWorkspace;
    private bool? narrowLayout;

    /// <summary>Loads compiled controls and attaches only local workspace input handlers.</summary>
    public BatchView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        SizeChanged += OnSizeChanged;
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(this, true);
    }

    /// <summary>Observes panel visibility without recreating the file list or its editors.</summary>
    private void OnDataContextChanged(object? sender, EventArgs eventArguments)
    {
        if (observedWorkspace is not null)
        {
            observedWorkspace.PropertyChanged -= OnWorkspaceChanged;
        }

        observedWorkspace = DataContext as BatchViewModel;

        if (observedWorkspace is not null)
        {
            observedWorkspace.PropertyChanged += OnWorkspaceChanged;
        }
    }

    /// <summary>Moves focus to inert review text so queued Space/Enter cannot activate Run.</summary>
    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs eventArguments)
    {
        if (eventArguments.PropertyName == nameof(BatchViewModel.IsReviewOpen) && observedWorkspace?.IsReviewOpen == true)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (observedWorkspace?.IsReviewOpen == true)
                {
                    StructuredReview.FindControl<TextBlock>("ReviewHeading")?.Focus();
                }
            });
        }

        if (eventArguments.PropertyName == nameof(BatchViewModel.IsDetailsOpen) && observedWorkspace?.IsDetailsOpen == true)
        {
            if (narrowLayout == true)
            {
                FileSidebar.IsExpanded = false;
            }

            Dispatcher.UIThread.Post(() => SelectedFileDetails.BringIntoView());
        }
    }

    /// <summary>Rearranges the same controls into a collapsible file shelf at the supported small width.</summary>
    private void OnSizeChanged(object? sender, SizeChangedEventArgs eventArguments)
    {
        var narrow = Bounds.Width < NarrowWorkspaceWidth;

        if (narrowLayout == narrow)
        {
            return;
        }

        narrowLayout = narrow;
        WorkspaceColumns.ColumnDefinitions = new(narrow ? "*" : "270,*");
        WorkspaceColumns.RowDefinitions = new(narrow ? "Auto,*" : "*");
        Grid.SetColumn(WorkspaceScrollViewer, narrow ? 0 : 1);
        Grid.SetRow(WorkspaceScrollViewer, narrow ? 1 : 0);
        BatchFileList.MaxHeight = narrow ? NarrowFileListHeight : double.PositiveInfinity;
        FileSidebar.IsExpanded = !narrow;
    }

    /// <summary>Opens the selected file's individual choices without changing union scope.</summary>
    private void OnOpenFileDetails(object? sender, TappedEventArgs eventArguments)
    {
        observedWorkspace?.OpenSelectedDetailsCommand.Execute(null);
    }

    /// <summary>Targets the concrete clicked row before opening its individual editor.</summary>
    private void OnFileDetails(object? sender, RoutedEventArgs eventArguments)
    {
        if (observedWorkspace is null || sender is not Button { DataContext: BatchItemViewModel item })
        {
            return;
        }

        observedWorkspace.SelectedItem = item;
        observedWorkspace.OpenSelectedDetailsCommand.Execute(null);
    }

    /// <summary>Keeps advanced rules one deliberate action away from the common track editor.</summary>
    private void OnOpenRules(object? sender, RoutedEventArgs eventArguments)
    {
        if (observedWorkspace is not null)
        {
            observedWorkspace.IsRulesOpen = true;
        }
    }

    /// <summary>Returns from rules without changing configuration, selections or authority.</summary>
    private void OnCloseRules(object? sender, RoutedEventArgs eventArguments)
    {
        if (observedWorkspace is not null)
        {
            observedWorkspace.IsRulesOpen = false;
        }
    }

    /// <summary>Reveals the existing reset confirmation outside its contextual flyout.</summary>
    private void OnNewQueue(object? sender, RoutedEventArgs eventArguments) => WorkspaceOptions.Flyout?.Hide();

    /// <summary>Accepts local file transfers only; folders retain their explicit discovery options.</summary>
    private void OnDragOver(object? sender, DragEventArgs eventArguments)
    {
        var files = eventArguments.DataTransfer.TryGetFiles()?.Take(BatchDiscoveryLimits.MaximumExaminedEntries + 1).ToArray() ?? [];
        var accepted = observedWorkspace?.CanEdit == true && files.Length != 0 && files.All(item => item is IStorageFile);
        eventArguments.DragEffects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        EmptyDropTarget.Classes.Set("drag-over", accepted);
        eventArguments.Handled = true;
    }

    /// <summary>Ends transient drop feedback without touching selections or progress.</summary>
    private void OnDragLeave(object? sender, DragEventArgs eventArguments) => EmptyDropTarget.Classes.Set("drag-over", false);

    /// <summary>Passes bounded local paths to the existing awaitable discovery/inspection operation.</summary>
    private async void OnDrop(object? sender, DragEventArgs eventArguments)
    {
        EmptyDropTarget.Classes.Set("drag-over", false);
        var files = eventArguments.DataTransfer.TryGetFiles()?.Take(BatchDiscoveryLimits.MaximumExaminedEntries + 1).ToArray() ?? [];
        var accepted = observedWorkspace?.CanEdit == true && files.Length != 0 && files.All(item => item is IStorageFile);

        if (!accepted)
        {
            return;
        }

        var paths = files.Select(item => item.TryGetLocalPath()).ToArray();

        if (paths.Any(path => path is null))
        {
            return;
        }

        eventArguments.Handled = true;
        await observedWorkspace!.AddDroppedFilesAsync(paths.Select(path => path!).ToArray());
    }
}
