using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    private MainViewModel? preferenceFocusOwner;

    public event Action? StartupCompleted;

    /// <summary>Loads compiled controls and connects view-only input and lifetime events.</summary>
    public MainWindow()
    {
        InitializeComponent();

        if (OperatingSystem.IsWindows())
        {
            WindowDecorations = WindowDecorations.None;
        }

        AppearanceMotionPolicy.Track(this);
        DataContextChanged += OnPreferenceFocusContextChanged;
        AppearancePanel.PropertyChanged += OnAppearancePanelChanged;
        LanguagePanel.PropertyChanged += OnLanguagePanelChanged;
        UpdatesPanel.PropertyChanged += OnUpdatesPanelChanged;
        AddHandler(KeyDownEvent, OnAppearanceKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnEditorBackgroundPressed, RoutingStrategies.Tunnel);
        Opened += OnOpened;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    /// <summary>Ends text editing on an outside primary press without consuming the target control's normal action.</summary>
    private void OnEditorBackgroundPressed(object? sender, PointerPressedEventArgs eventArguments)
    {
        var focusedEditor = FocusManager?.GetFocusedElement() as TextBox;
        var primaryPress = eventArguments.GetCurrentPoint(this).Properties.IsLeftButtonPressed;

        if (focusedEditor is null || !primaryPress || eventArguments.Source is not Visual source)
        {
            return;
        }

        var insideEditor = ReferenceEquals(source, focusedEditor) || source.GetVisualAncestors().Contains(focusedEditor);

        if (insideEditor)
        {
            return;
        }

        FocusManager?.Focus(null);
    }

    /// <summary>Connects logical panel opening to focus independently of an outgoing visibility animation.</summary>
    private void OnPreferenceFocusContextChanged(object? sender, EventArgs eventArguments)
    {
        if (preferenceFocusOwner is not null)
        {
            preferenceFocusOwner.PropertyChanged -= OnPreferenceFocusChanged;
        }

        preferenceFocusOwner = DataContext as MainViewModel;

        if (preferenceFocusOwner is not null)
        {
            preferenceFocusOwner.PropertyChanged += OnPreferenceFocusChanged;
            QueuePreferenceFocus();
        }
    }

    /// <summary>Reopening a still-visible outgoing panel must focus its current controls, not the previous panel.</summary>
    private void OnPreferenceFocusChanged(object? sender, PropertyChangedEventArgs eventArguments)
    {
        var changesPanel = eventArguments.PropertyName is nameof(MainViewModel.IsAppearanceOpen)
            or nameof(MainViewModel.IsLanguageOpen) or nameof(MainViewModel.IsUpdatesOpen);

        if (changesPanel)
        {
            QueuePreferenceFocus();
        }
    }

    /// <summary>Uses the latest logical panel after bindings settle and ignores callbacks from a former context.</summary>
    private void QueuePreferenceFocus()
    {
        var owner = preferenceFocusOwner;
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible || !ReferenceEquals(owner, DataContext))
            {
                return;
            }

            FocusOpenPreference();
        });
    }

    /// <summary>Focuses only the currently requested cosmetic panel; outgoing frames cannot own keyboard input.</summary>
    private void FocusOpenPreference()
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var target = viewModel.IsAppearanceOpen ? CloseAppearanceButton : viewModel.IsLanguageOpen ? CloseLanguageButton
            : viewModel.IsUpdatesOpen ? CloseUpdatesButton : null;

        if (target is not null)
        {
            target.Focus();
            return;
        }

        var focused = FocusManager?.GetFocusedElement() as Visual;
        var insideOutgoingPanel = focused?.GetVisualAncestors().Any(ancestor =>
            ReferenceEquals(ancestor, AppearancePanel) || ReferenceEquals(ancestor, LanguagePanel) || ReferenceEquals(ancestor, UpdatesPanel)) == true;

        if (insideOutgoingPanel)
        {
            FocusManager?.Focus(null);
        }
    }

    /// <summary>Uses only non-button header space for native Windows caption gestures.</summary>
    private void OnWindowCaptionPressed(object? sender, PointerPressedEventArgs eventArguments) => Caption.HandleCaptionPress(eventArguments);

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

            // An outgoing fade must not steal focus from a newly opened cosmetic panel.
            if (!appearanceIsOpen && DataContext is MainViewModel { IsPreferencesOpen: true })
            {
                return;
            }

            if (appearanceIsOpen)
            {
                FocusOpenPreference();
                return;
            }

            AppearanceFocusTarget(false).Focus();
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

    /// <summary>Returns focus to the language trigger while respecting mandatory media decisions.</summary>
    private void OnLanguagePanelChanged(object? sender, AvaloniaPropertyChangedEventArgs eventArguments)
    {
        if (eventArguments.Property != IsVisibleProperty)
        {
            return;
        }

        var wasOpen = LanguagePanel.IsVisible;
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible || LanguagePanel.IsVisible != wasOpen)
            {
                return;
            }

            // An outgoing fade must not steal focus from a newly opened cosmetic panel.
            if (!wasOpen && DataContext is MainViewModel { IsPreferencesOpen: true })
            {
                return;
            }

            if (wasOpen)
            {
                FocusOpenPreference();
                return;
            }

            var target = DataContext is MainViewModel { HasPendingMediaDecision: true }
                ? AppearanceFocusTarget(false) : LanguageButton;
            target.Focus();
        });
    }

    /// <summary>Uses fresh visibility to move focus into updates and back to its permanent entry.</summary>
    private void OnUpdatesPanelChanged(object? sender, AvaloniaPropertyChangedEventArgs eventArguments)
    {
        if (eventArguments.Property != IsVisibleProperty)
        {
            return;
        }

        var isOpen = UpdatesPanel.IsVisible;
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible || UpdatesPanel.IsVisible != isOpen)
            {
                return;
            }

            // An outgoing fade must not steal focus from a newly opened cosmetic panel.
            if (!isOpen && DataContext is MainViewModel { IsPreferencesOpen: true })
            {
                return;
            }

            if (isOpen)
            {
                FocusOpenPreference();
                return;
            }

            var target = DataContext is MainViewModel { HasPendingMediaDecision: true }
                ? AppearanceFocusTarget(false) : AvailableUpdateButton;
            target.Focus();
        });
    }

    /// <summary>Dismisses only cosmetic panels when the user presses their inert backdrop.</summary>
    private void OnPreferencesBackdropPressed(object? sender, PointerPressedEventArgs eventArguments)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        viewModel.IsAppearanceOpen = false;
        viewModel.IsLanguageOpen = false;
        viewModel.IsUpdatesOpen = false;
        eventArguments.Handled = true;
    }

    /// <summary>Escape closes only cosmetic UI; media cancellation and warning decisions remain explicit actions.</summary>
    private void OnAppearanceKeyDown(object? sender, KeyEventArgs eventArguments)
    {
        var closesAppearance = eventArguments.Key == Key.Escape && DataContext is MainViewModel { IsPreferencesOpen: true };

        if (!closesAppearance)
        {
            return;
        }

        ((MainViewModel)DataContext!).IsAppearanceOpen = false;
        ((MainViewModel)DataContext!).IsLanguageOpen = false;
        ((MainViewModel)DataContext!).IsUpdatesOpen = false;
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
        if (preferenceFocusOwner is not null)
        {
            preferenceFocusOwner.PropertyChanged -= OnPreferenceFocusChanged;
            preferenceFocusOwner = null;
        }

        DataContextChanged -= OnPreferenceFocusContextChanged;

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
            warningReviewViewModel.IsLanguageOpen = false;
            this.FindControl<Button>("RemainInWarningReviewButton")?.Focus();
            return;
        }

        var needsExplicitDecision = !shutdownCompleted && DataContext is MainViewModel { RequiresCloseDecision: true };

        if (needsExplicitDecision)
        {
            eventArguments.Cancel = true;
            ((MainViewModel)DataContext!).IsAppearanceOpen = false;
            ((MainViewModel)DataContext!).IsLanguageOpen = false;
            ((MainViewModel)DataContext!).IsUpdatesOpen = false;
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
    private async void OnCloseDespiteUncertainty(object? sender, RoutedEventArgs eventArguments)
    {
        if (shutdownInProgress)
        {
            return;
        }

        shutdownInProgress = true;

        try
        {
            if (DataContext is MainViewModel viewModel)
            {
                await viewModel.Languages.ShutdownAsync();
            }

            shutdownCompleted = true;
            Close();
        }
        finally
        {
            shutdownInProgress = false;
        }
    }
}
