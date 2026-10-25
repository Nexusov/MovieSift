using MovieSift.Application.Batches;
using MovieSift.Application.Inspection;
using MovieSift.Application.Jobs;
using MovieSift.Application.Presets;
using MovieSift.Desktop.Appearance;
using MovieSift.Desktop.Interaction;
using MovieSift.Desktop.Localization;
using MovieSift.Desktop.ViewModels;
using MovieSift.Desktop.Views;
using MovieSift.Infrastructure.Files;
using MovieSift.Infrastructure.MediaTools;
using MovieSift.Infrastructure.Persistence;
using Serilog;

namespace MovieSift.Desktop.Bootstrap;

/// <summary>Constructs all concrete adapters at the only desktop composition root.</summary>
internal static class AppBootstrap
{
    private const long LogFileSizeLimitBytes = 2 * 1024 * 1024;
    private const int RetainedLogFiles = 7;

    /// <summary>Creates the live application with app-local tools and portable data.</summary>
    public static MainWindow CreateMainWindow()
    {
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
        var localization = new LocalizationService();
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
        window.DataContext = new MainViewModel(new(settings, outputs, localization, dataWritable, logger, batchViewModel, appearance));
        window.Closed += (_, _) => logger.Dispose();
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
