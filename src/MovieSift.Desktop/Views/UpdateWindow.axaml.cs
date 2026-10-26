using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MovieSift.Desktop.Updates;

namespace MovieSift.Desktop.Views;

/// <summary>Offers a release without starting a download until the user explicitly installs it.</summary>
public sealed partial class UpdateWindow : Window
{
    private bool closeAfterCancellation;

    /// <summary>Connects accessible dismissal and waits for any requested download cancellation.</summary>
    public UpdateWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            InstallButton.Focus();

            if (DataContext is UpdatesViewModel updates)
            {
                updates.PropertyChanged += OnUpdateChanged;
            }
        };
        Closed += (_, _) =>
        {
            if (DataContext is UpdatesViewModel updates)
            {
                updates.PropertyChanged -= OnUpdateChanged;
            }
        };
        Closing += OnClosing;
        KeyDown += (_, eventArguments) =>
        {
            if (eventArguments.Key == Key.Escape)
            {
                Close();
                eventArguments.Handled = true;
            }
        };
    }

    /// <summary>Closing a notification alone never downloads or installs anything.</summary>
    private void OnCloseClick(object? sender, RoutedEventArgs eventArguments) => Close();

    /// <summary>A download may be cancelled; the short installer handoff must finish before its windows disappear.</summary>
    private void OnClosing(object? sender, WindowClosingEventArgs eventArguments)
    {
        if (DataContext is UpdatesViewModel { IsInstalling: true, IsRestarting: false } updates)
        {
            eventArguments.Cancel = true;
            closeAfterCancellation = true;

            if (updates.CancelCommand.CanExecute(null))
            {
                updates.CancelCommand.Execute(null);
            }
        }
    }

    /// <summary>Completes dismissal after the existing operation has acknowledged cancellation.</summary>
    private void OnUpdateChanged(object? sender, PropertyChangedEventArgs eventArguments)
    {
        if (closeAfterCancellation && DataContext is UpdatesViewModel { IsInstalling: false })
        {
            Close();
        }
    }
}
