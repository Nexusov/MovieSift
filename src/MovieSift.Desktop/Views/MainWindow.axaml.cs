using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MovieSift.Application.Updates;
using MovieSift.Desktop.Appearance;
using MovieSift.Desktop.ViewModels;

namespace MovieSift.Desktop.Views;

/// <summary>Owns window-level focus, initialization and safe asynchronous close behavior.</summary>
public sealed partial class MainWindow : Window
{
    private bool shutdownCompleted;
    private bool shutdownInProgress;
    private UpdateWindow? updateWindow;

    public event Action? StartupCompleted;

    /// <summary>Loads compiled controls and connects view-only input and lifetime events.</summary>
    public MainWindow()
    {
        InitializeComponent();
        AppearanceMotionPolicy.Track(this);
        AppearancePanel.PropertyChanged += OnAppearancePanelChanged;
        AddHandler(KeyDownEvent, OnAppearanceKeyDown, RoutingStrategies.Tunnel);
        Opened += OnOpened;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    /// <summary>Moves focus only on an actual open/close transition, preserving ordinary editor navigation.</summary>
    private void OnAppearancePanelChanged(object? sender, AvaloniaPropertyChangedEventArgs eventArguments)
    {
        if (eventArguments.Property != IsVisibleProperty)
        {
            return;
        }

        var appearanceIsOpen = AppearancePanel.IsVisible;
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible || AppearancePanel.IsVisible != appearanceIsOpen)
            {
                return;
            }

            AppearanceFocusTarget(appearanceIsOpen).Focus();
        });
    }

    /// <summary>Prefers an already pending mandatory close decision over the ordinary cosmetic invoking control.</summary>
    private Button AppearanceFocusTarget(bool appearanceIsOpen)
    {
        if (appearanceIsOpen)
        {
            return CloseAppearanceButton;
        }

        if (DataContext is MainViewModel { HasWarningCloseDecision: true })
        {
            return RemainInWarningReviewButton;
        }

        if (DataContext is MainViewModel { RequiresCloseDecision: true })
        {
            return CloseDespiteUncertaintyButton;
        }

        return AppearanceButton;
    }

    /// <summary>Escape closes only cosmetic UI; media cancellation and warning decisions remain explicit actions.</summary>
    private void OnAppearanceKeyDown(object? sender, KeyEventArgs eventArguments)
    {
        var closesAppearance = eventArguments.Key == Key.Escape && DataContext is MainViewModel { IsAppearanceOpen: true };

        if (!closesAppearance)
        {
            return;
        }

        ((MainViewModel)DataContext!).IsAppearanceOpen = false;
        eventArguments.Handled = true;
    }

    /// <summary>Restores portable state after the ViewModel has been composed.</summary>
    private async void OnOpened(object? sender, EventArgs eventArguments)
    {
        if (DataContext is MainViewModel viewModel)
        {
            if (viewModel.Updates is { } updates)
            {
                updates.ShowRequested += OnShowUpdate;
                updates.InstallPrepared += OnInstallPreparedAsync;
            }

            await viewModel.InitializeAsync();

            if (IsVisible)
            {
                StartupCompleted?.Invoke();
            }
        }
    }

    /// <summary>Shows at most one notification and keeps available updates accessible after dismissal.</summary>
    private async void OnShowUpdate()
    {
        if (!IsVisible || shutdownInProgress || updateWindow is not null || DataContext is not MainViewModel { Updates: { HasUpdate: true } updates })
        {
            return;
        }

        updateWindow = new UpdateWindow { DataContext = updates };

        try
        {
            await updateWindow.ShowDialog(this);
        }
        finally
        {
            updateWindow = null;
            AvailableUpdateButton.Focus();
        }
    }

    /// <summary>Hands off installation only after the shell is idle and its portable preference writes finish.</summary>
    private async Task OnInstallPreparedAsync(PreparedUpdate prepared)
    {
        if (shutdownInProgress || DataContext is not MainViewModel { Updates: { } updates } viewModel || !viewModel.CanInstallUpdate)
        {
            throw new UpdateException(UpdateErrorCode.Busy, "Finish media and recovery decisions before installing an update.");
        }

        shutdownInProgress = true;

        try
        {
            if (viewModel.Appearance is { } appearance)
            {
                await appearance.WhenIdleAsync();
            }

            await updates.WhenPreferencesSavedAsync();

            if (!viewModel.CanInstallUpdate)
            {
                throw new UpdateException(UpdateErrorCode.Busy, "Media state changed before installation could begin.");
            }

            await updates.LaunchInstallerAsync(prepared);
            shutdownCompleted = true;
            Close();
        }
        finally
        {
            shutdownInProgress = false;
        }
    }

    /// <summary>Detaches view callbacks when the window lifetime ends.</summary>
    private void OnClosed(object? sender, EventArgs eventArguments)
    {
        if (DataContext is MainViewModel { Updates: { } updates })
        {
            updates.ShowRequested -= OnShowUpdate;
            updates.InstallPrepared -= OnInstallPreparedAsync;
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
            warningReviewViewModel.IsAppearanceOpen = false;
            this.FindControl<Button>("RemainInWarningReviewButton")?.Focus();
            return;
        }

        var needsExplicitDecision = !shutdownCompleted && DataContext is MainViewModel { RequiresCloseDecision: true };

        if (needsExplicitDecision)
        {
            eventArguments.Cancel = true;
            ((MainViewModel)DataContext!).IsAppearanceOpen = false;
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
