using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MovieSift.Desktop.ViewModels;
using MovieSift.Desktop.Views;
using Xunit;

namespace MovieSift.Desktop.Tests;

/// <summary>Checks actual vector disclosure geometry and scrollbar separation without changing track choices.</summary>
public sealed class TrackDisclosureGeometryTests
{
    /// <summary>Expands and collapses through the bound button with scalable geometry rather than a font glyph.</summary>
    [AvaloniaTheory]
    [InlineData("en", 1d)]
    [InlineData("ru", 2d)]
    public async Task MemberDisclosureUsesReversibleVectorStateWithoutChangingReview(string language, double density)
    {
        var fixture = new BatchViewModelFixture();
        await fixture.PrepareAsync();
        fixture.Batch.CloseReviewCommand.Execute(null);
        var review = fixture.Batch.Snapshot;
        await fixture.Main.ChangeLanguageCommand.ExecuteAsync(language);
        var window = new MainWindow { DataContext = fixture.Main, Width = 1040, Height = 800 };
        window.Show();
        window.SetRenderScaling(density);

        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var button = window.GetVisualDescendants().OfType<ToggleButton>().First(control => control.Classes.Contains("memberExpand"));
            var group = Assert.IsType<AggregateTrackGroupViewModel>(button.DataContext);
            var glyph = Assert.IsType<PathIcon>(button.Content);
            Assert.True(glyph.Data!.Bounds.Width > 0);
            Assert.True(glyph.Data.Bounds.Height > 0);
            Assert.False(group.IsExpanded);
            Assert.Equal(1, glyph.RenderTransform!.Value.M11, precision: 8);
            button.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(group.IsExpanded);
            Assert.Equal(-1, glyph.RenderTransform!.Value.M11, precision: 8);
            Assert.Equal(-1, glyph.RenderTransform.Value.M22, precision: 8);
            Assert.True(glyph.Bounds.Width > 0);
            Assert.True(glyph.Bounds.Height > 0);
            button.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(group.IsExpanded);
            Assert.Equal(1, glyph.RenderTransform!.Value.M11, precision: 8);
            Assert.Same(review, fixture.Batch.Snapshot);
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Reserves a real gap between the vertical scrollbar and a visible expandable track group.</summary>
    [AvaloniaTheory]
    [InlineData(640, "en")]
    [InlineData(640, "ru")]
    [InlineData(1040, "en")]
    public async Task ScrollingTrackListKeepsDisclosureOutsideScrollbarHitArea(int width, string language)
    {
        var fixture = new BatchViewModelFixture();
        await fixture.LoadAsync();
        await fixture.Main.ChangeLanguageCommand.ExecuteAsync(language);
        var window = new MainWindow { DataContext = fixture.Main, Width = width, Height = 800 };
        window.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var scroll = window.FindControl<BatchView>("Workspace")!.FindControl<ScrollViewer>("WorkspaceScrollViewer")!;
            var button = scroll.GetVisualDescendants().OfType<ToggleButton>().First(control => control.Classes.Contains("memberExpand"));
            button.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            var scrollbar = Assert.Single(scroll.GetVisualDescendants().OfType<ScrollBar>(), bar => bar.Orientation == Orientation.Vertical);
            Assert.True(scrollbar.IsVisible);
            Assert.True(scrollbar.Bounds.Width > 0);
            var buttonPoint = button.TranslatePoint(default, window)!.Value;
            var barPoint = scrollbar.TranslatePoint(default, window)!.Value;
            var buttonBounds = new Rect(buttonPoint, button.Bounds.Size);
            Assert.True(buttonBounds.Right < barPoint.X, $"Disclosure right {buttonBounds.Right}; scrollbar left {barPoint.X}.");
            var hit = window.InputHitTest(buttonBounds.Center);
            Assert.True(ReferenceEquals(hit, button) || hit is Visual visual && visual.GetVisualAncestors().Contains(button));
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            window.Close();
        }
    }
}
