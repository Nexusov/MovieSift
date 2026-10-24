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
        var fixture = new BatchViewModelFixture();
        var sources = fixture.Backend.Inspections.Values.ToArray();

        for (var index = 0; index < sources.Length; index++)
        {
            var original = sources[index];
            var name = index == 0 ? "Series — Episode 01 [English + Русский] long title.mkv" : "Movie — Глава 02 [Studio collection] long title.mkv";
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
        await fixture.Main.InitializeAsync();
        fixture.Appearance.ThemeIndex = (int)theme;
        fixture.Appearance.MotionIndex = (int)AppearanceMotion.Reduced;
        var window = new MainWindow { DataContext = fixture.Main, Width = width, Height = height };
        window.Show();

        try
        {
            await fixture.Main.ChangeLanguageCommand.ExecuteAsync(language);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Save(window, $"{theme}-{language}-{width}x{height}-{density}-empty", density);
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
            Save(window, $"{theme}-{language}-{width}x{height}-{density}-selection", density);
            var editableGroup = workspace.GetVisualDescendants().OfType<CheckBox>().First(control =>
                control.DataContext is AggregateTrackGroupViewModel { IsRequired: false });
            editableGroup.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var trackPoint = editableGroup.TranslatePoint(default, scroll);
            Assert.NotNull(trackPoint);
            Assert.InRange(trackPoint.Value.Y, -BoundsTolerance, scroll.Viewport.Height);
            Assert.True(editableGroup.IsEffectivelyEnabled);
            Save(window, $"{theme}-{language}-{width}x{height}-{density}-tracks", density);
            await fixture.Batch.ReviewCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal(2, fixture.Batch.ReviewRows.Count);
            Assert.True(fixture.Batch.HasReview);
            Assert.Empty(fixture.Backend.RemuxRequests);
            var run = Assert.Single(workspace.GetVisualDescendants().OfType<Button>(), button => button.Name == "StartBatchButton");
            Assert.True(run.IsEffectivelyVisible);
            var runPoint = run.TranslatePoint(default, window);
            Assert.NotNull(runPoint);
            Assert.True(runPoint.Value.Y + run.Bounds.Height <= window.ClientSize.Height + BoundsTolerance);
            Save(window, $"{theme}-{language}-{width}x{height}-{density}-review", density);
            fixture.Batch.CloseReviewCommand.Execute(null);
            fixture.Main.ToggleAppearanceCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Save(window, $"{theme}-{language}-{width}x{height}-{density}-appearance", density);
        }
        finally
        {
            await fixture.Appearance.WhenIdleAsync();
            window.Close();
        }
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
        File.WriteAllText(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(new
        {
            method = "Avalonia.Headless + Skia RenderTargetBitmap; controlled UI metadata; not native desktop acceptance",
            logicalWidth = window.ClientSize.Width,
            logicalHeight = window.ClientSize.Height,
            renderDensity = density,
            osDisplayScalingChanged = false
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
