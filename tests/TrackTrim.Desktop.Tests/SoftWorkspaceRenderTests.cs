using System.Collections.Immutable;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TrackTrim.Application.Appearance;
using TrackTrim.Core.Media;
using TrackTrim.Desktop.ViewModels;
using TrackTrim.Desktop.Views;
using Xunit;

namespace TrackTrim.Desktop.Tests;

/// <summary>Renders the real Soft workspace with controlled media metadata; never claims native desktop acceptance.</summary>
public sealed class SoftWorkspaceRenderTests
{
    private const double BoundsTolerance = 1;
    private const double LogicalDpi = 96;

    /// <summary>Checks both variants/languages at normal and minimum logical widths, with optional density renders.</summary>
    [AvaloniaTheory]
    [InlineData("en", AppearanceTheme.Light, 1040, 800, 1d)]
    [InlineData("ru", AppearanceTheme.Light, 1040, 800, 1d)]
    [InlineData("en", AppearanceTheme.Light, 1200, 850, 1d)]
    [InlineData("ru", AppearanceTheme.Light, 1200, 850, 1d)]
    [InlineData("en", AppearanceTheme.Dark, 1200, 850, 1d)]
    [InlineData("ru", AppearanceTheme.Dark, 1200, 850, 1d)]
    [InlineData("en", AppearanceTheme.Light, 640, 480, 1.5d)]
    [InlineData("ru", AppearanceTheme.Light, 640, 480, 1d)]
    [InlineData("ru", AppearanceTheme.Light, 640, 480, 2d)]
    [InlineData("en", AppearanceTheme.Dark, 640, 480, 2d)]
    [InlineData("ru", AppearanceTheme.Dark, 640, 480, 1.5d)]
    public async Task ActualSoftViewsRemainUsableAcrossVariantsAndLogicalSizes(string language, AppearanceTheme theme, int width, int height, double density)
    {
        var fixture = CreateLongNameFixture();
        await fixture.Main.InitializeAsync();
        fixture.Appearance.ThemeIndex = (int)theme;
        fixture.Appearance.MotionIndex = (int)AppearanceMotion.Reduced;

        if (theme == AppearanceTheme.Light)
        {
            fixture.Appearance.LightMuted = "#707070";
            Assert.False(fixture.Appearance.HasError, fixture.Appearance.ErrorText);
        }

        var window = new MainWindow { DataContext = fixture.Main, Width = width, Height = height };
        window.Show();

        try
        {
            await fixture.Main.ChangeLanguageCommand.ExecuteAsync(language);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            await fixture.LoadAsync();
            await fixture.Main.ChangeLanguageCommand.ExecuteAsync(language);
            var workspace = window.FindControl<BatchView>("Workspace")!;
            fixture.Batch.Items[0].Tracks.First(track => track.Track.Kind == TrackKind.Audio && !track.Track.IsDefault).Keep = false;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var scroll = workspace.FindControl<ScrollViewer>("WorkspaceScrollViewer")!;
            Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + BoundsTolerance);
            var review = workspace.FindControl<Button>("ReviewBatchButton")!;
            var reviewPoint = review.TranslatePoint(default, window);
            Assert.NotNull(reviewPoint);
            Assert.True(reviewPoint.Value.Y + review.Bounds.Height <= window.ClientSize.Height + BoundsTolerance);
            Assert.True(review.IsEffectivelyVisible);
            Assert.False(fixture.Batch.HasReview);
            var fileSidebar = workspace.FindControl<Expander>("FileSidebar")!;
            var expandedBeforeCapture = fileSidebar.IsExpanded;
            fileSidebar.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Save(window, $"{theme}-{language}-{width}x{height}-{density}-files-selection", density);
            fileSidebar.IsExpanded = expandedBeforeCapture;
            var editableGroup = workspace.GetVisualDescendants().OfType<CheckBox>().First(control =>
                control.DataContext is AggregateTrackGroupViewModel { IsRequired: false });
            editableGroup.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var trackPoint = editableGroup.TranslatePoint(default, scroll);
            Assert.NotNull(trackPoint);
            Assert.InRange(trackPoint.Value.Y, -BoundsTolerance, scroll.Viewport.Height);
            Assert.True(editableGroup.IsEffectivelyEnabled);
            await fixture.Batch.ReviewCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal(2, fixture.Batch.ReviewRows.Count);
            Assert.Single(fixture.Batch.ReviewRows, row => row.NoChange);
            Assert.Single(fixture.Batch.ReviewRows, row => !row.NoChange);
            Assert.True(fixture.Batch.HasReview);
            Assert.Empty(fixture.Backend.RemuxRequests);
            var run = Assert.Single(workspace.GetVisualDescendants().OfType<Button>(), button => button.Name == "StartBatchButton");
            Assert.True(run.IsEffectivelyVisible);
            var runPoint = run.TranslatePoint(default, window);
            Assert.NotNull(runPoint);
            Assert.True(runPoint.Value.Y + run.Bounds.Height <= window.ClientSize.Height + BoundsTolerance);
            Save(window, $"{theme}-{language}-{width}x{height}-{density}-review", density);
            fixture.Batch.CloseReviewCommand.Execute(null);

            if (language == "en" && width == 1040)
            {
                fixture.Main.ToggleAppearanceCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Save(window, $"{theme}-{language}-{width}x{height}-{density}-appearance-accents", density);
            }
        }
        finally
        {
            await fixture.Appearance.WhenIdleAsync();
            window.Close();
        }
    }

    /// <summary>Captures repeated asynchronous validation and cancellation without media execution or arbitrary delays.</summary>
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public async Task PendingAndCancelledReviewRemainTruthfulAndDirectlyCancellable(string language)
    {
        var fixture = CreateLongNameFixture();
        await fixture.Main.InitializeAsync();
        fixture.Appearance.ThemeIndex = (int)AppearanceTheme.Light;
        fixture.Appearance.MotionIndex = (int)AppearanceMotion.Reduced;
        fixture.Appearance.LightMuted = "#707070";
        var window = new MainWindow { DataContext = fixture.Main, Width = 1040, Height = 800 };
        var entered = BatchViewModelFixture.Signal();
        var release = BatchViewModelFixture.Signal();
        Task? pendingReview = null;
        window.Show();

        try
        {
            await fixture.LoadAsync();
            await fixture.Main.ChangeLanguageCommand.ExecuteAsync(language);
            fixture.Batch.Items[0].Tracks.First(track => track.Track.Kind == TrackKind.Audio && !track.Track.IsDefault).Keep = false;
            await fixture.Batch.ReviewCommand.ExecuteAsync(null);
            Assert.True(fixture.Batch.HasCurrentReview);
            fixture.SourceAccess.OnValidateDestination = async request =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(request.CancellationToken);
            };
            pendingReview = fixture.Batch.ReviewCommand.ExecuteAsync(null);
            await BatchViewModelFixture.Await(entered.Task);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(fixture.Batch.IsReviewOpen);
            Assert.True(fixture.Batch.IsReviewPending);
            Assert.False(fixture.Batch.CanRun);
            var workspace = window.FindControl<BatchView>("Workspace")!;
            var cancel = Assert.Single(workspace.GetVisualDescendants().OfType<Button>(), button =>
                ReferenceEquals(button.Command, fixture.Batch.CancelCommand) && button.IsEffectivelyVisible);
            Assert.True(cancel.IsEffectivelyEnabled);
            var cancelPoint = cancel.TranslatePoint(default, window);
            Assert.NotNull(cancelPoint);
            Assert.True(cancelPoint.Value.Y + cancel.Bounds.Height <= window.ClientSize.Height + BoundsTolerance);
            Save(window, $"Light-{language}-1040x800-1-review-pending", 1);
            cancel.Command!.Execute(null);
            await BatchViewModelFixture.Await(pendingReview);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(fixture.Batch.IsReviewCancelled);
            Assert.False(fixture.Batch.IsReviewPending);
            Assert.False(fixture.Batch.HasCurrentReview);
            Assert.True(fixture.Batch.NeedsReview);
            Assert.False(fixture.Batch.CanRun);
            Assert.Empty(fixture.Backend.RemuxRequests);
            Save(window, $"Light-{language}-1040x800-1-review-cancelled", 1);
        }
        finally
        {
            fixture.Batch.CancelCommand.Execute(null);
            release.TrySetResult();

            if (pendingReview is not null)
            {
                await BatchViewModelFixture.Await(pendingReview);
            }

            await fixture.Appearance.WhenIdleAsync();
            window.Close();
        }
    }

    /// <summary>Uses similarly prefixed episode filenames so renders expose meaningful filename truncation.</summary>
    private static BatchViewModelFixture CreateLongNameFixture()
    {
        var fixture = new BatchViewModelFixture();
        var sources = fixture.Backend.Inspections.Values.ToArray();

        for (var index = 0; index < sources.Length; index++)
        {
            var original = sources[index];
            var name = $"Series — A shared production title — Episode {index + 1:00} [English + Русский] collection.mkv";
            var path = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "TrackTrim fixtures", "Series", "Season " + (index + 1), name);
            var described = original with
            {
                Source = original.Source with { FullPath = path },
                Tracks = original.Tracks.Select(track => track with
                {
                    Codec = track.Kind == TrackKind.Video ? "H.264 / AVC" : track.Kind == TrackKind.Audio ? "AAC" : "SRT",
                    Title = track.Kind == TrackKind.Video ? "Original video" : track.Kind == TrackKind.Audio ? track.IsDefault ? "Main soundtrack" : "Commentary — original titles" : "Captions",
                    Dispositions = track.Kind == TrackKind.Audio && !track.IsDefault ? [TrackDisposition.Commentary] : []
                }).ToImmutableArray()
            };
            fixture.Backend.Inspections.Remove(original.Source.FullPath);
            fixture.Backend.Inspections.Add(path, described);
        }

        var revisions = fixture.Backend.Inspections.Values.Select(inspection => inspection.Source).ToImmutableArray();
        fixture.Dialogs.OutputFolder = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "TrackTrim outputs");
        fixture.Discovery.Result = new(revisions, []);
        fixture.Dialogs.Sources = revisions.Select(source => source.FullPath).ToArray();
        return fixture;
    }

    /// <summary>Exports logical-size/density snapshots only when explicitly requested by the development environment.</summary>
    private static void Save(Window window, string name, double density)
    {
        var directory = Environment.GetEnvironmentVariable("TRACKTRIM_SOFT_RENDER_OUTPUT");

        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        var pixels = new PixelSize((int)(window.ClientSize.Width * density), (int)(window.ClientSize.Height * density));
        using var bitmap = new RenderTargetBitmap(pixels, new Vector(LogicalDpi * density, LogicalDpi * density));
        using (var drawing = bitmap.CreateDrawingContext())
        {
            drawing.DrawRectangle(new VisualBrush(window), null, new Rect(window.ClientSize));
        }
        bitmap.Save(Path.Combine(directory, name + ".png"), PngBitmapEncoderOptions.Default);
        var main = window.DataContext as MainViewModel;
        File.WriteAllText(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(new
        {
            method = "Avalonia.Headless + Skia RenderTargetBitmap; controlled UI metadata; not native desktop acceptance",
            logicalWidth = window.ClientSize.Width,
            logicalHeight = window.ClientSize.Height,
            renderDensity = density,
            language = main?.Localization.Language,
            appearanceTheme = main?.Appearance?.Current.Theme.ToString(),
            lightSecondaryTextOverride = main?.Appearance?.Current.LightOverrides?.SecondaryText,
            sourceNames = main?.Batch.Items.Select(item => item.FileName).ToArray(),
            reviewSummary = main?.Batch.ReviewSummaryText,
            reviewPending = main?.Batch.IsReviewPending,
            reviewCancelled = main?.Batch.IsReviewCancelled,
            currentReview = main?.Batch.HasCurrentReview,
            reviewedCopyCount = main?.Batch.ReviewedCopyCount,
            reviewedNoChangeCount = main?.Batch.ReviewedNoChangeCount,
            osDisplayScalingChanged = false
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
