using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using TrackTrim.Application.Appearance;
using TrackTrim.Desktop.Appearance;
using TrackTrim.Desktop.Bootstrap;

namespace TrackTrim.Desktop;

/// <summary>Creates the single desktop composition root after Avalonia starts.</summary>
public sealed partial class App : Avalonia.Application
{
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
            desktop.MainWindow = AppBootstrap.CreateMainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
