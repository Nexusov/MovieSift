using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;

[assembly: AvaloniaTestApplication(typeof(MovieSift.Desktop.Tests.TestAppBuilder))]

namespace MovieSift.Desktop.Tests;

/// <summary>Runs the actual Fluent XAML application without a desktop display server.</summary>
public static class TestAppBuilder
{
    /// <summary>Initializes isolated headless controls for each UI test.</summary>
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions
        {
            UseHeadlessDrawing = false
        });
    }
}
