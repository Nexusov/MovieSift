using System.Text;
using Avalonia;
using MovieSift.Desktop.Bootstrap;
using MovieSift.Desktop.Localization;

namespace MovieSift.Desktop;

internal static class Program
{
    /// <summary>Starts the portable desktop application.</summary>
    [STAThread]
    public static int Main(string[] arguments)
    {
        using var lifetime = PortableApplicationLifetime.Acquire();

        if (lifetime.InstallationInProgress)
        {
            // Exit before loading the desktop while another process owns the installation switch.
            Console.Error.WriteLine(new LocalizationService()["UpdatesAlreadyInstalling"]);
            return 1;
        }

        var isDiagnosticCommand = arguments.FirstOrDefault() is "--verify-tools" or "--verify-portable";

        if (isDiagnosticCommand)
        {
            Console.OutputEncoding = Encoding.UTF8;

            return PortableSmoke.RunAsync(arguments).GetAwaiter().GetResult();
        }

        BuildAvaloniaApp(lifetime.CanInstallUpdates).StartWithClassicDesktopLifetime(arguments);
        return 0;
    }

    /// <summary>Configures Avalonia without starting its lifetime.</summary>
    public static AppBuilder BuildAvaloniaApp(bool canInstallUpdates = false)
    {
        return AppBuilder.Configure(() => new App(canInstallUpdates)).UsePlatformDetect();
    }
}
