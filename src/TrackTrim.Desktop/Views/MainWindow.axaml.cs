using Avalonia.Controls;
using Avalonia.Interactivity;
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
        var needsExplicitDecision = !shutdownCompleted && DataContext is MainViewModel { RequiresCloseDecision: true };

        if (needsExplicitDecision)
        {
            eventArguments.Cancel = true;
            return;
        }

        var requiresShutdown = !shutdownCompleted && DataContext is MainViewModel { IsBusy: true };

        if (!requiresShutdown)
        {
            return;
        }

        eventArguments.Cancel = true;

        if (shutdownInProgress)
        {
            return;
        }

        shutdownInProgress = true;
        var viewModel = (MainViewModel)DataContext!;
        await viewModel.ShutdownAsync();

        if (viewModel.RequiresCloseDecision)
        {
            shutdownInProgress = false;
            return;
        }

        shutdownCompleted = true;
        Close();
    }

    /// <summary>Honors the explicit close choice without deleting an unconfirmed worker's files.</summary>
    private void OnCloseDespiteUncertainty(object? sender, RoutedEventArgs eventArguments)
    {
        shutdownCompleted = true;
        Close();
    }
}
