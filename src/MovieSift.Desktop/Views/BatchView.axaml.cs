using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MovieSift.Application.Batches;
using MovieSift.Desktop.ViewModels;

namespace MovieSift.Desktop.Views;

/// <summary>Owns responsive placement, local drop input and focus, never plans or media execution.</summary>
public sealed partial class BatchView : UserControl
{
    private const double NarrowWorkspaceWidth = 850;
    private const double NarrowFileListHeight = 140;
    private const double NarrowExpandedShelfFraction = 0.75;
    private const double NarrowMinimumTrackHeight = 48;
    private const double FileSidebarWidth = 330;
    private BatchViewModel? observedWorkspace;
    private bool? narrowLayout;

    /// <summary>Loads compiled controls and attaches only local workspace input handlers.</summary>
    public BatchView()
    {
        InitializeComponent();
        BatchFileList.AddHandler(KeyDownEvent, OnFileListKeyDown, RoutingStrategies.Tunnel);
        FileSidebar.PropertyChanged += OnFileShelfChanged;
        WorkspaceColumns.SizeChanged += OnWorkspaceAreaSizeChanged;
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
        var rulesClosed = eventArguments.PropertyName == nameof(BatchViewModel.IsRulesOpen) && observedWorkspace?.IsRulesOpen == false;

        if (rulesClosed && RulesPanel.IsKeyboardFocusWithin)
        {
            TopLevel.GetTopLevel(this)?.FocusManager?.Focus(null);
        }

        var reviewTransition = eventArguments.PropertyName is nameof(BatchViewModel.IsReviewOpen) or nameof(BatchViewModel.IsReviewPending);

        if (reviewTransition && observedWorkspace?.IsReviewOpen == true)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (observedWorkspace?.IsReviewOpen == true)
                {
                    StructuredReview.FindControl<TextBlock>("ReviewHeading")?.Focus();
                }
            });
        }

        // Details expand in place: neither scroll the editor nor collapse a different section automatically.
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
        NewQueueLabel.IsVisible = !narrow;
        WorkspaceColumns.ColumnDefinitions = new(narrow ? "*" : $"{FileSidebarWidth},*");
        UpdateShelfRows();
        Grid.SetColumn(WorkspaceScrollViewer, narrow ? 0 : 1);
        Grid.SetRow(WorkspaceScrollViewer, narrow ? 1 : 0);
        BatchFileList.MaxHeight = narrow ? NarrowFileListHeight : double.PositiveInfinity;
        FileSidebar.IsExpanded = !narrow;
    }

    /// <summary>Shares the finite narrow layout height while keeping the same file collection and scroll position alive.</summary>
    private void OnFileShelfChanged(object? sender, AvaloniaPropertyChangedEventArgs eventArguments)
    {
        if (eventArguments.Property == Expander.IsExpandedProperty)
        {
            UpdateShelfRows();
        }
    }

    /// <summary>Rebounds file content when footer wrapping or window height changes the remaining preparation area.</summary>
    private void OnWorkspaceAreaSizeChanged(object? sender, SizeChangedEventArgs eventArguments) => UpdateShelfRows();

    /// <summary>Prevents an expanded Auto row from consuming the track viewport or overlapping the fixed footer.</summary>
    private void UpdateShelfRows()
    {
        if (narrowLayout != true)
        {
            WorkspaceColumns.RowDefinitions = new("*");
            FileSidebar.MaxHeight = double.PositiveInfinity;
            return;
        }

        WorkspaceColumns.RowDefinitions = new("Auto,*");
        // Wrapped footer text can leave too little space for even one track action under a fractional cap alone.
        var availableHeight = WorkspaceColumns.Bounds.Height;
        var expandedShelfHeight = Math.Min(availableHeight * NarrowExpandedShelfFraction,
            Math.Max(0, availableHeight - NarrowMinimumTrackHeight));
        // Closing content remains visible during its fade and must keep the same finite layout budget.
        FileSidebar.MaxHeight = expandedShelfHeight;
    }

    /// <summary>Opens deliberately activated row details while inclusion and filename controls retain their own actions.</summary>
    private void OnOpenFileDetails(object? sender, TappedEventArgs eventArguments)
    {
        if (IsRowControlAction(eventArguments.Source))
        {
            return;
        }

        observedWorkspace?.OpenSelectedDetailsCommand.Execute(null);
        eventArguments.Handled = true;
    }

    /// <summary>Offers Enter on the focused collection row without intercepting a checkbox or filename button.</summary>
    private void OnFileListKeyDown(object? sender, KeyEventArgs eventArguments)
    {
        if (eventArguments.Key != Key.Enter || IsRowControlAction(eventArguments.Source))
        {
            return;
        }

        observedWorkspace?.OpenSelectedDetailsCommand.Execute(null);
        eventArguments.Handled = true;
    }

    /// <summary>Prevents bubbled row gestures from turning inclusion into an unrelated details action.</summary>
    private static bool IsRowControlAction(object? source)
    {
        if (source is not Control control)
        {
            return false;
        }

        return control is CheckBox or Button || control.GetVisualAncestors().Any(ancestor => ancestor is CheckBox or Button);
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
    private void OnNewQueue(object? sender, RoutedEventArgs eventArguments)
    {
        if (observedWorkspace?.NewBatchCommand.CanExecute(null) == true)
        {
            observedWorkspace.NewBatchCommand.Execute(null);
        }

        WorkspaceOptions.Flyout?.Hide();
    }

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
