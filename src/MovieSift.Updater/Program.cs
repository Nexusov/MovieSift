using MovieSift.Application.Updates;
using MovieSift.Infrastructure.Updates;

namespace MovieSift.Updater;

/// <summary>Runs outside the application tree and accepts only a separately hash-bound prepared transaction.</summary>
internal static class Program
{
    private static async Task<int> Main(string[] arguments)
    {
        if (arguments is ["--verify-runtime"])
        {
            Console.WriteLine($"MovieSift.Updater {typeof(Program).Assembly.GetName().Version}; .NET {Environment.Version}");
            return 0;
        }
        if (arguments is ["--verify-update-archive", var archivePath, var runtimeIdentifier])
        {
            try
            {
                var assemblyVersion = typeof(Program).Assembly.GetName().Version!;
                var version = new AppVersion(assemblyVersion.Major, assemblyVersion.Minor, assemblyVersion.Build);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(110));
                var manifest = await PortableUpdateInstaller.VerifyArchiveAsync(archivePath, runtimeIdentifier, version, timeout.Token);
                Console.WriteLine($"Update archive compatibility passed: {runtimeIdentifier}, {version}, manifest SHA256 {manifest}.");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Update archive compatibility failed: {exception.Message}");
                return 1;
            }
        }
        if (arguments.Length != 2)
        {
            Console.Error.WriteLine("MovieSift.Updater requires a prepared installation plan and its SHA256.");
            return 2;
        }
        return await PortableUpdateInstaller.RunAsync(arguments[0], arguments[1]);
    }
}
