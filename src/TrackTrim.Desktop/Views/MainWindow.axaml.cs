using Avalonia.Controls;
using Avalonia.Interactivity;
using TrackTrim.Desktop.Appearance;
using TrackTrim.Desktop.ViewModels;

namespace TrackTrim.Desktop.Views;

/// <summary>Owns only window initialization and safe asynchronous close behavior.</summary>
public sealed partial class MainWindow : Window
{
    private bool shutdownCompleted;
    private bool shutdownInProgress;

    /// <summary>Loads the compiled view and connects lifetime-only events.</summary>
    public MainWindow()
    {
        InitializeComponent();
        AppearanceMotionPolicy.Track(this);
        Opened += OnOpened;
        Closing += OnClosing;
    }

    /// <summary>Restores portable state after the ViewModel has been composed.</summary>
    private async void OnOpened(object? sender, EventArgs eventArguments)
    {
        if (DataContext is MainViewModel viewModel)
        {
            await viewModel.InitializeAsync();
        }
    }

    /// <summary>Keeps the window alive until its running media work has safely stopped.</summary>
    private async void OnClosing(object? sender, WindowClosingEventArgs eventArguments)
    {
        if (shutdownCompleted)
        {
            return;
        }

        if (shutdownInProgress)
        {
            eventArguments.Cancel = true;
            return;
        }

        var needsWarningReview = !shutdownCompleted && DataContext is MainViewModel { HasWarningCandidate: true, HasOwnedOperation: false };

        if (needsWarningReview)
        {
            eventArguments.Cancel = true;
            var warningReviewViewModel = (MainViewModel)DataContext!;
            warningReviewViewModel.RequestWarningCloseReview();
            this.FindControl<Button>("RemainInWarningReviewButton")?.Focus();
            return;
        }

        var needsExplicitDecision = !shutdownCompleted && DataContext is MainViewModel { RequiresCloseDecision: true };

        if (needsExplicitDecision)
        {
            eventArguments.Cancel = true;
            return;
        }

        var requiresShutdown = !shutdownCompleted && DataContext is MainViewModel { HasActiveOperation: true };

        if (!requiresShutdown)
        {
            return;
        }

        eventArguments.Cancel = true;

        shutdownInProgress = true;
        var viewModel = (MainViewModel)DataContext!;
        await viewModel.ShutdownAsync();

        var requiresReview = viewModel.RequiresCloseDecision || viewModel.HasWarningCandidate || viewModel.HasCleanupIssue;

        if (requiresReview)
        {
            shutdownInProgress = false;
            viewModel.RequestWarningCloseReview();
            return;
        }

        shutdownCompleted = true;
        Close();
    }

    /// <summary>Closes only after the explicit discard choice has awaited cleanup successfully.</summary>
    private async void OnDiscardWarningsAndClose(object? sender, RoutedEventArgs eventArguments)
    {
        if (shutdownInProgress || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        shutdownInProgress = true;

        try
        {
            var canClose = await viewModel.DiscardWarningsForCloseAsync();

            if (!canClose)
            {
                return;
            }

            shutdownCompleted = true;
            Close();
        }
        finally
        {
            shutdownInProgress = false;
        }
    }

    /// <summary>Honors the explicit close choice without deleting an unconfirmed worker's files.</summary>
    private void OnCloseDespiteUncertainty(object? sender, RoutedEventArgs eventArguments)
    {
        shutdownCompleted = true;
        Close();
    }
}
