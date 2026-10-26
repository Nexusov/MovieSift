using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MovieSift.Application.Appearance;
using MovieSift.Desktop.Appearance;
using MovieSift.Desktop.Bootstrap;

namespace MovieSift.Desktop;

/// <summary>Creates the single desktop composition root after Avalonia starts.</summary>
public sealed partial class App : Avalonia.Application
{
    private readonly bool canInstallUpdates;

    /// <summary>Allows designer and headless construction without registering a live portable process.</summary>
    public App() : this(false) { }

    /// <summary>Receives the executable lifetime's installation ownership without a global service locator.</summary>
    internal App(bool canInstallUpdates)
    {
        this.canInstallUpdates = canInstallUpdates;
    }

    /// <summary>Loads the Fluent theme and application styles.</summary>
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        var settings = AppearanceSettings.Default;
        new AppearanceApplier(this).Apply(settings, AppearancePalettePolicy.Resolve(settings));
    }

    /// <summary>Creates the main window for a classic desktop lifetime.</summary>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = AppBootstrap.CreateMainWindow(canInstallUpdates);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
