using System.Text;
using Avalonia;
using MovieSift.Desktop.Bootstrap;

namespace MovieSift.Desktop;

internal static class Program
{
    /// <summary>Starts the portable desktop application.</summary>
    [STAThread]
    public static int Main(string[] arguments)
    {
        var isDiagnosticCommand = arguments.FirstOrDefault() is "--verify-tools" or "--verify-portable";

        if (isDiagnosticCommand)
        {
            Console.OutputEncoding = Encoding.UTF8;
            return PortableSmoke.RunAsync(arguments).GetAwaiter().GetResult();
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(arguments);
        return 0;
    }

    /// <summary>Configures Avalonia without starting its lifetime.</summary>
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>().UsePlatformDetect();
    }
}
