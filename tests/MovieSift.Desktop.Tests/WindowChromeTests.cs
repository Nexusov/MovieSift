using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Platform;
using MovieSift.Desktop.Views;
using Xunit;

namespace MovieSift.Desktop.Tests;

/// <summary>Checks custom caption routing and packaged icon resources without claiming native window-manager acceptance.</summary>
public sealed class WindowChromeTests
{
    /// <summary>Preserves native decorations outside Windows while retaining the actual resizable window.</summary>
    [AvaloniaFact]
    public void CaptionReplacesOnlyWindowsDecorations()
    {
        var window = new MainWindow();
        window.Show();
        var caption = window.FindControl<WindowChrome>("Caption")!;
        Assert.Equal(OperatingSystem.IsWindows(), caption.IsVisible);
        Assert.Equal(OperatingSystem.IsWindows() ? WindowDecorations.None : WindowDecorations.Full, window.WindowDecorations);
        Assert.True(window.CanResize);
        Assert.NotNull(window.Icon);
        window.Close();
    }

    /// <summary>Uses the containing window's state and reacts to externally requested maximize/restore transitions.</summary>
    [AvaloniaFact]
    public void CaptionControlsFollowWindowStateAndResizeAvailability()
    {
        var fixture = new ViewModelFixture();
        var window = new MainWindow { DataContext = fixture.ViewModel };
        window.Show();
        var caption = window.FindControl<WindowChrome>("Caption")!;
        var maximize = caption.FindControl<Button>("MaximizeButton")!;
        var minimize = caption.FindControl<Button>("MinimizeButton")!;
        Assert.Equal(fixture.ViewModel.Localization["WindowMaximize"], AutomationProperties.GetName(maximize));
        maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(WindowState.Maximized, window.WindowState);
        Assert.Equal(fixture.ViewModel.Localization["WindowRestore"], AutomationProperties.GetName(maximize));
        maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(WindowState.Normal, window.WindowState);
        minimize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(WindowState.Minimized, window.WindowState);
        window.WindowState = WindowState.Normal;
        window.CanResize = false;
        Assert.False(maximize.IsEnabled);
        window.Close();
    }

    /// <summary>Cannot bypass an ordinary Close cancellation or replace shutdown with a forced lifetime exit.</summary>
    [AvaloniaFact]
    public void CaptionCloseUsesCancelableWindowClose()
    {
        var window = new MainWindow();
        var requests = 0;
        void RejectClose(object? sender, WindowClosingEventArgs eventArguments)
        {
            requests++;
            eventArguments.Cancel = true;
        }

        window.Show();
        window.Closing += RejectClose;
        var caption = window.FindControl<WindowChrome>("Caption")!;
        caption.FindControl<Button>("CloseWindowButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, requests);
        Assert.True(window.IsVisible);
        window.Closing -= RejectClose;
        window.Close();
    }

    /// <summary>Includes genuine PNG-backed ICO entries at every supported caption/apphost size.</summary>
    [AvaloniaFact]
    public void IconContainsMultipleActualPngResolutions()
    {
        using var icon = AssetLoader.Open(new Uri("avares://MovieSift/Assets/moviesift.ico"));
        using var copy = new MemoryStream();
        icon.CopyTo(copy);
        copy.Position = 0;
        using var reader = new BinaryReader(copy);
        Assert.Equal(0, reader.ReadUInt16());
        Assert.Equal(1, reader.ReadUInt16());
        int[] sizes = [16, 24, 32, 48, 64, 128, 256];
        Assert.Equal(sizes.Length, reader.ReadUInt16());
        foreach (var size in sizes)
        {
            Assert.Equal(size % 256, reader.ReadByte());
            Assert.Equal(size % 256, reader.ReadByte());
            reader.ReadUInt16();
            Assert.Equal(1, reader.ReadUInt16());
            Assert.Equal(32, reader.ReadUInt16());
            var length = reader.ReadUInt32();
            var offset = reader.ReadUInt32();
            Assert.True(offset + length <= copy.Length);
            var position = copy.Position;
            copy.Position = offset;
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, reader.ReadBytes(8));
            copy.Position = position;
        }
    }
}
