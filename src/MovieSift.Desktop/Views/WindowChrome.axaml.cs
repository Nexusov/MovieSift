using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MovieSift.Desktop.Localization;
using MovieSift.Desktop.ViewModels;

namespace MovieSift.Desktop.Views;

/// <summary>Provides Windows caption input while preserving the window's ordinary close lifecycle.</summary>
public sealed partial class WindowChrome : UserControl
{
    public static readonly StyledProperty<string> MaximizeTextProperty =
        AvaloniaProperty.Register<WindowChrome, string>(nameof(MaximizeText), string.Empty);
    private Window? window;
    private LocalizationService? localization;

    public string MaximizeText
    {
        get => GetValue(MaximizeTextProperty);
        private set => SetValue(MaximizeTextProperty, value);
    }

    /// <summary>Loads compiled caption controls; other platforms retain their native decorations.</summary>
    public WindowChrome()
    {
        InitializeComponent();
        IsVisible = OperatingSystem.IsWindows();
        DataContextChanged += OnContextChanged;
    }

    /// <summary>Observes only the containing window's presentation state.</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs eventArguments)
    {
        base.OnAttachedToVisualTree(eventArguments);
        window = TopLevel.GetTopLevel(this) as Window;

        if (window is not null)
        {
            window.PropertyChanged += OnWindowChanged;
        }

        ConnectLocalization();
    }

    /// <summary>Releases presentation subscriptions when the caption leaves its window.</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs eventArguments)
    {
        if (window is not null)
        {
            window.PropertyChanged -= OnWindowChanged;
        }

        if (localization is not null)
        {
            localization.PropertyChanged -= OnLanguageChanged;
        }

        localization = null;
        window = null;
        base.OnDetachedFromVisualTree(eventArguments);
    }

    /// <summary>Reconnects localization when the composed shell is assigned.</summary>
    private void OnContextChanged(object? sender, EventArgs eventArguments) => ConnectLocalization();

    /// <summary>Keeps caption names in the current UI language without changing workspace state.</summary>
    private void ConnectLocalization()
    {
        if (localization is not null)
        {
            localization.PropertyChanged -= OnLanguageChanged;
        }

        localization = (DataContext as MainViewModel)?.Localization;

        if (localization is not null)
        {
            localization.PropertyChanged += OnLanguageChanged;
        }

        UpdateCaption();
    }

    /// <summary>Refreshes the restore/maximize name after a live language change.</summary>
    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs eventArguments) => UpdateCaption();

    /// <summary>Reflects maximizing through native gestures as well as the caption button.</summary>
    private void OnWindowChanged(object? sender, AvaloniaPropertyChangedEventArgs eventArguments)
    {
        if (eventArguments.Property == Window.WindowStateProperty || eventArguments.Property == Window.CanResizeProperty)
        {
            UpdateCaption();
        }
    }

    /// <summary>Updates the window-state glyph and localized accessible action.</summary>
    private void UpdateCaption()
    {
        var maximized = window?.WindowState == WindowState.Maximized;
        MaximizeText = localization?[maximized ? "WindowRestore" : "WindowMaximize"] ?? string.Empty;
        MaximizeButton.IsEnabled = window?.CanResize == true;
        MaximizeGlyph.Data = Avalonia.Media.Geometry.Parse(maximized
            ? "M 2,0 L 10,0 L 10,8 L 8,8 L 8,10 L 0,10 L 0,2 L 2,2 Z M 3,1 L 3,2 L 8,2 L 8,7 L 9,7 L 9,1 Z M 1,3 L 1,9 L 7,9 L 7,3 Z"
            : "M 0,0 L 10,0 L 10,10 L 0,10 Z M 1,1 L 1,9 L 9,9 L 9,1 Z");
    }

    /// <summary>Delegates dragging and double-click maximize to Avalonia's actual window.</summary>
    public void HandleCaptionPress(PointerPressedEventArgs eventArguments)
    {
        var pressedButton = eventArguments.Source is Visual source &&
            (source is Button || source.GetVisualAncestors().OfType<Button>().Any());

        if (pressedButton || !OperatingSystem.IsWindows())
        {
            return;
        }

        if (window is null || !eventArguments.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (eventArguments.ClickCount == 2)
        {
            ToggleMaximized();
            eventArguments.Handled = true;
            return;
        }

        window.BeginMoveDrag(eventArguments);
        eventArguments.Handled = true;
    }

    /// <summary>Minimizes without cancelling or detaching any active job.</summary>
    private void OnMinimize(object? sender, RoutedEventArgs eventArguments)
    {
        if (window is not null)
        {
            window.WindowState = WindowState.Minimized;
        }
    }

    /// <summary>Uses the same state transition as a caption double click.</summary>
    private void OnMaximize(object? sender, RoutedEventArgs eventArguments) => ToggleMaximized();

    /// <summary>Restores or maximizes only windows that permit resizing.</summary>
    private void ToggleMaximized()
    {
        if (window?.CanResize != true)
        {
            return;
        }

        window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    /// <summary>Requests ordinary Close so warning decisions and owned shutdown remain authoritative.</summary>
    private void OnClose(object? sender, RoutedEventArgs eventArguments) => window?.Close();
}
