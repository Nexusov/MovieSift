using System.Runtime.InteropServices;
using Avalonia.Controls;
using MovieSift.Application.Batches;
using MovieSift.Application.Inspection;
using MovieSift.Application.Jobs;
using MovieSift.Application.Presets;
using MovieSift.Application.Updates;
using MovieSift.Desktop.Appearance;
using MovieSift.Desktop.Interaction;
using MovieSift.Desktop.Localization;
using MovieSift.Desktop.Updates;
using MovieSift.Desktop.ViewModels;
using MovieSift.Desktop.Views;
using MovieSift.Infrastructure.Files;
using MovieSift.Infrastructure.Localization;
using MovieSift.Infrastructure.MediaTools;
using MovieSift.Infrastructure.Persistence;
using MovieSift.Infrastructure.Updates;
using Serilog;

namespace MovieSift.Desktop.Bootstrap;

/// <summary>Constructs all concrete adapters at the only desktop composition root.</summary>
internal static class AppBootstrap
{
    private const long LogFileSizeLimitBytes = 2 * 1024 * 1024;
    private const int RetainedLogFiles = 7;

    /// <summary>Creates the live application with app-local tools and portable data.</summary>
    public static Window CreateMainWindow(bool canInstallUpdates)
    {
        var localization = new LocalizationService();
        var dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
        var dataWritable = CanWriteData(dataDirectory);
        var loggerConfiguration = new LoggerConfiguration().MinimumLevel.Information();

        if (dataWritable)
        {
            loggerConfiguration.WriteTo.File(Path.Combine(dataDirectory, "logs", "moviesift-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: RetainedLogFiles,
                fileSizeLimitBytes: LogFileSizeLimitBytes, rollOnFileSizeLimit: true);
        }

        var logger = loggerConfiguration.CreateLogger();
        var sourceAccess = new FileSourceAccess();
        var backend = new ContainerMediaBackend(
            new MkvBackend(new(sourceAccess, MkvToolPaths.FromApplicationDirectory(),
                Diagnostic: detail => logger.Warning("{MediaCleanupDiagnostic}", detail))),
            new FfmpegBackend(new(sourceAccess, FfmpegToolPaths.FromApplicationDirectory())));
        var outputs = new OwnedOutputStore(dataDirectory);
        var settings = new PortableSettingsStore(dataDirectory);
        var window = new MainWindow();
        var dialogs = new WindowFileDialogs(new(window, localization));
        var jobs = new JobCoordinator(backend, sourceAccess, outputs);
        var batch = new BatchSession();
        var batchViewModel = new BatchViewModel(new(batch,
            new BatchCoordinator(batch, new InspectionService(backend, sourceAccess), jobs, sourceAccess),
            new FileBatchInputDiscovery(sourceAccess, outputs, [dataDirectory]), dialogs, localization, dataWritable, logger,
            new RulePresetService(new PortableRulePresetStore(dataDirectory)), new PortableQueueListStore(dataDirectory)));
        var appearance = new AppearanceViewModel(new(new PortableAppearanceStore(dataDirectory), localization,
            new AppearanceApplier(Avalonia.Application.Current!)));
        var version = typeof(App).Assembly.GetName().Version!;
        var currentVersion = new AppVersion(version.Major, version.Minor, version.Build);
        var runtime = RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsLinux() ? "linux-x64" : "unsupported"
            : "unsupported";
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        var previousInstallFailed = Environment.GetEnvironmentVariable("MOVIESIFT_UPDATE_FAILED") == "1";
        Environment.SetEnvironmentVariable("MOVIESIFT_UPDATE_FAILED", null);
        var updates = new UpdatesViewModel(new GitHubUpdateService(http, AppContext.BaseDirectory, currentVersion,
            runtime, canInstallUpdates), new PortableUpdatePreferencesStore(dataDirectory), localization, currentVersion, logger, previousInstallFailed);
        var translations = new GitHubTranslationPackStore(http, dataDirectory, LocalizationService.EnglishMessages);
        window.DataContext = new MainViewModel(new(settings, outputs, localization, dataWritable, logger, batchViewModel, appearance, updates, translations));
        window.StartupCompleted += PortableUpdateInstaller.AcknowledgeStartup;
        window.Closed += (_, _) =>
        {
            http.Dispose();
            logger.Dispose();
        };
        return window;
    }

    /// <summary>Checks required portable storage without redirecting data into a user profile.</summary>
    private static bool CanWriteData(string directoryPath)
    {
        var probePath = Path.Combine(directoryPath, $".write-probe-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(directoryPath);
            using (new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }

            File.Delete(probePath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
