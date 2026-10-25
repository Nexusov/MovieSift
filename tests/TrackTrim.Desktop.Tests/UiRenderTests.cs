using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Serilog;
using TrackTrim.Application.Batches;
using TrackTrim.Application.Inspection;
using TrackTrim.Application.Jobs;
using TrackTrim.Application.Presets;
using TrackTrim.Core.Media;
using TrackTrim.Desktop.Localization;
using TrackTrim.Desktop.ViewModels;
using TrackTrim.Desktop.Views;
using TrackTrim.Infrastructure.Files;
using TrackTrim.Infrastructure.MediaTools;
using TrackTrim.Infrastructure.Persistence;
using Xunit;

namespace TrackTrim.Desktop.Tests;

/// <summary>Checks real compiled layouts and optionally captures explicitly labelled headless evidence.</summary>
public sealed class UiRenderTests
{
    private const double LayoutTolerance = 1;

    /// <summary>Renders both supported languages at normal and minimum window widths.</summary>
    [AvaloniaTheory]
    [InlineData("en", 1040, 900)]
    [InlineData("ru", 1040, 900)]
    [InlineData("en", 640, 480)]
    [InlineData("ru", 640, 480)]
    public async Task CompiledInspectorFitsBothLanguages(string language, int width, int height)
    {
        var renderDirectory = Environment.GetEnvironmentVariable("TRACKTRIM_UI_RENDER_OUTPUT");
        var sourcePath = Environment.GetEnvironmentVariable("TRACKTRIM_UI_SOURCE");
        var toolDirectory = Environment.GetEnvironmentVariable("TRACKTRIM_TOOL_DIRECTORY");
        var usesRealMedia = !string.IsNullOrWhiteSpace(sourcePath) && !string.IsNullOrWhiteSpace(toolDirectory);
        var hasPartialMediaConfiguration = !usesRealMedia && !string.IsNullOrWhiteSpace(sourcePath);

        if (hasPartialMediaConfiguration)
        {
            throw new InvalidOperationException("TRACKTRIM_UI_SOURCE requires TRACKTRIM_TOOL_DIRECTORY.");
        }

        var scratchDirectory = Path.Combine(Path.GetTempPath(), $"TrackTrim UI render {Guid.NewGuid():N}");
        Directory.CreateDirectory(scratchDirectory);
        using var logger = new LoggerConfiguration().CreateLogger();
        MainWindow? window = null;

        try
        {
            var viewModel = usesRealMedia ? CreateRealViewModel(new(sourcePath!, toolDirectory!, scratchDirectory, logger)) : new ViewModelFixture().ViewModel;
            window = new MainWindow { DataContext = viewModel, Width = width, Height = height };
            window.Show();
            await viewModel.Batch.Recovery.InitializeAsync();
            await viewModel.Batch.AddFilesCommand.ExecuteAsync(null);
            var item = Assert.Single(viewModel.Batch.Items);
            Assert.False(item.HasError, item.ErrorReasonText);
            Assert.True(item.HasTracks);
            item.OutputPath = Path.Combine(scratchDirectory, "result.trimmed.mkv");
            var removableTrack = item.Tracks.First(track => track.Track.Kind == TrackKind.Audio);
            removableTrack.Keep = false;
            await viewModel.Batch.ReviewCommand.ExecuteAsync(null);
            await viewModel.ChangeLanguageCommand.ExecuteAsync(language);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var scrollViewer = window.FindControl<BatchView>("Workspace")!.FindControl<ScrollViewer>("WorkspaceScrollViewer")!;
            Assert.True(scrollViewer.Extent.Width <= scrollViewer.Viewport.Width + LayoutTolerance);
            Assert.True(item.HasPlan, item.ErrorReasonText);

            if (!string.IsNullOrWhiteSpace(renderDirectory))
            {
                Directory.CreateDirectory(renderDirectory);
                var prefix = Path.Combine(renderDirectory, $"tracktrim-{language}-{width}x{height}");
                using (var inspectionFrame = window.CaptureRenderedFrame())
                {
                    Assert.NotNull(inspectionFrame);
                    inspectionFrame.Save($"{prefix}-inspection.png", PngBitmapEncoderOptions.Default);
                }

                scrollViewer.ScrollToEnd();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();

                using (var reviewFrame = window.CaptureRenderedFrame())
                {
                    Assert.NotNull(reviewFrame);
                    reviewFrame.Save($"{prefix}-review.png", PngBitmapEncoderOptions.Default);
                }

                var evidence = new
                {
                    Renderer = "Avalonia.Headless with Skia; not native desktop acceptance",
                    RealBackendInspection = usesRealMedia,
                    Source = usesRealMedia ? sourcePath : "Deterministic UI-only fixture ports",
                    Language = language,
                    WindowWidth = width,
                    WindowHeight = height,
                    TrackCount = item.Tracks.Count,
                    Backend = item.Snapshot.Inspection!.Backend.Name
                };
                await File.WriteAllTextAsync($"{prefix}.json", JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        finally
        {
            window?.Close();
            File.Delete(Path.Combine(scratchDirectory, "settings.json"));
            Directory.Delete(scratchDirectory, recursive: false);
        }
    }

    /// <summary>Injects actual pinned media adapters while substituting only the native file picker.</summary>
    private static MainViewModel CreateRealViewModel(RealRenderRequest request)
    {
        var sourceAccess = new FileSourceAccess();
        var executableExtension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        var paths = new MkvToolPaths(Path.Combine(request.ToolDirectory, $"mkvmerge{executableExtension}"),
            Path.Combine(request.ToolDirectory, $"mkvextract{executableExtension}"), IsDevelopmentOverride: true);
        var backend = new MkvBackend(new(sourceAccess, paths));
        var outputs = new OwnedOutputStore(request.DataDirectory);
        var localization = new LocalizationService();
        var session = new BatchSession();
        var coordinator = new BatchCoordinator(session, new InspectionService(backend, sourceAccess),
            new JobCoordinator(backend, sourceAccess, outputs), sourceAccess);
        var workspace = new BatchViewModel(new(session, coordinator, new FileBatchInputDiscovery(sourceAccess, outputs, [request.DataDirectory]),
            new FakeDialogs(request.SourcePath), localization, true, request.Log,
            new RulePresetService(new BatchUiPresetStore()), new BatchUiQueueListStore()));
        return new(new(new PortableSettingsStore(request.DataDirectory), outputs, localization, true, request.Log, workspace));
    }
}
