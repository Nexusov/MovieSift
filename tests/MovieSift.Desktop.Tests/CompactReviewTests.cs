using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MovieSift.Core.Media;
using MovieSift.Core.Planning;
using MovieSift.Desktop.Views;
using Xunit;

namespace MovieSift.Desktop.Tests;

/// <summary>Checks concise immutable review descriptions and separate reachable execution controls.</summary>
public sealed class CompactReviewTests
{
    /// <summary>Uses each approved file's own IDs/order and counts without repeating a large video title.</summary>
    [AvaloniaFact]
    public async Task CompactRowsDescribeFrozenPlansWithActualPerFileTrackIdentities()
    {
        var fixture = new BatchViewModelFixture();
        var first = fixture.Backend.Inspections.Values.First();
        var longVideoTitle = string.Join(' ', Enumerable.Repeat(first.Source.FullPath, 12));
        fixture.Backend.Inspections[first.Source.FullPath] = first with
        {
            Tracks = first.Tracks.Select(track => track.Kind == TrackKind.Video ? track with { Title = longVideoTitle } : track).ToImmutableArray()
        };
        await fixture.PrepareAsync();
        var review = fixture.Batch.Snapshot.Review;
        var rows = fixture.Batch.ReviewRows.ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal([3], rows[0].RemovedTracks.Select(track => track.Identifier));
        Assert.Equal([77], rows[1].RemovedTracks.Select(track => track.Identifier));

        foreach (var row in rows)
        {
            Assert.Same(row.Item.ApprovedPlan, row.Plan);
            Assert.Equal(fixture.Localization.Format(new("ReviewKeptSummary", [1, 2, 0])), row.RetainedSummaryText);
            Assert.DoesNotContain("ReviewKeptSummary", row.RetainedSummaryText);
            Assert.DoesNotContain(longVideoTitle, row.RetainedTracksText);
            Assert.All(row.Plan!.RetainedTracks, track => Assert.Contains($"{fixture.Localization[track.Kind.ToString()]} {track.Id}", row.RetainedTracksText));
        }

        await fixture.Main.ChangeLanguageCommand.ExecuteAsync("ru");
        Assert.Same(review, fixture.Batch.Snapshot.Review);
        Assert.Equal(rows, fixture.Batch.ReviewRows);
        Assert.All(rows, row => Assert.Equal(fixture.Localization.Format(new("ReviewKeptSummary", [1, 2, 0])), row.RetainedSummaryText));
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>NoChange never promises an output, while an actual replacement names the original target explicitly.</summary>
    [AvaloniaFact]
    public async Task NoChangeAndReplacementRemainDistinctInCompactReview()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.LoadAsync();
        await fixture.Batch.ReviewCommand.ExecuteAsync(null);
        Assert.All(fixture.Batch.ReviewRows, row =>
        {
            Assert.True(row.NoChange);
            Assert.False(row.HasCopyPlan);
            Assert.Equal(fixture.Localization.Format(new("ReviewKeptSummary", [1, 2, 1])), row.RetainedSummaryText);
            Assert.Empty(row.RemovedTracks);
        });

        fixture.Batch.CloseReviewCommand.Execute(null);
        fixture.Batch.SelectedOutputModeIndex = (int)OutputMode.ReplaceSource;
        fixture.Batch.Items[0].Tracks.Single(track => track.Track.Id == 1).Keep = false;
        await fixture.Batch.ReviewCommand.ExecuteAsync(null);
        var replaced = fixture.Batch.ReviewRows[0];
        Assert.True(replaced.ReplacesOriginal);
        Assert.Equal(replaced.SourcePath, replaced.DestinationPath);
        Assert.Equal(fixture.Localization["OutputWillReplace"], replaced.DestinationActionText);
        Assert.Equal([1], replaced.RemovedTracks.Select(track => track.Identifier));
        Assert.True(fixture.Batch.ReviewRows[1].NoChange);
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>Shows removals first, keeps retained details collapsed, and preserves the visible default-audio warning.</summary>
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public async Task CriticalWarningStaysVisibleBesideCompactRetainedDetails(string language)
    {
        var fixture = new BatchViewModelFixture();
        await fixture.LoadAsync();
        fixture.Batch.Items[0].Tracks.Single(track => track.Track.Id == 1).Keep = false;
        await fixture.Batch.ReviewCommand.ExecuteAsync(null);
        await fixture.Main.ChangeLanguageCommand.ExecuteAsync(language);
        var row = fixture.Batch.ReviewRows[0];
        var view = new BatchReviewView { DataContext = fixture.Batch };
        var window = new Window { Content = view, Width = 640, Height = 800 };
        window.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var retained = Assert.Single(view.GetVisualDescendants().OfType<Expander>(), control =>
                control.Name == "RetainedTrackDetails" && ReferenceEquals(control.DataContext, row));
            Assert.False(retained.IsExpanded);
            Assert.Equal(row.RetainedSummaryText, retained.Header);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), control =>
                control.IsEffectivelyVisible && ReferenceEquals(control.DataContext, row) && control.Text == fixture.Localization["SelectionWarning"]);
            var audioHelp = Assert.Single(view.GetVisualDescendants().OfType<HelpHintView>(), control =>
                control.IsEffectivelyVisible && ReferenceEquals(control.DataContext, row));
            Assert.Equal(fixture.Localization["SelectionWarning"], audioHelp.Text);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), control =>
                control.Name == "ReviewDefaultAudioResult" && control.IsEffectivelyVisible && control.Text == row.DefaultAudioText);
            var removal = Assert.Single(view.GetVisualDescendants().OfType<TextBlock>(), control =>
                control.Name == "ReviewRemovedIdentity" && control.IsEffectivelyVisible);
            Assert.Equal(Assert.Single(row.RemovedTracks).IdentityText, removal.Text);
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Places Run below the scrolling rows at minimum width without moving revalidation onto its hit target.</summary>
    [AvaloniaTheory]
    [InlineData(640, "en")]
    [InlineData(640, "ru")]
    [InlineData(1040, "en")]
    public async Task RunFooterRemainsReachableOutsideScrollingPlans(int width, string language)
    {
        var fixture = new BatchViewModelFixture();
        await fixture.PrepareAsync();
        await fixture.Main.ChangeLanguageCommand.ExecuteAsync(language);
        var window = new MainWindow { DataContext = fixture.Main, Width = width, Height = 800 };
        window.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            var view = window.FindControl<BatchView>("Workspace")!.FindControl<BatchReviewView>("StructuredReview")!;
            var run = view.FindControl<Button>("StartBatchButton")!;
            var scroll = view.FindControl<ScrollViewer>("ReviewRowsScroller")!;
            var initialRunBounds = new Rect(run.TranslatePoint(default, window)!.Value, run.Bounds.Size);
            var scrollBounds = new Rect(scroll.TranslatePoint(default, window)!.Value, scroll.Bounds.Size);
            Assert.True(run.IsEffectivelyVisible);
            Assert.True(run.IsEffectivelyEnabled);
            Assert.True(scrollBounds.Bottom <= initialRunBounds.Top);
            Assert.True(initialRunBounds.Bottom <= window.ClientSize.Height);
            scroll.Offset = new Vector(0, scroll.Extent.Height);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal(initialRunBounds, new Rect(run.TranslatePoint(default, window)!.Value, run.Bounds.Size));
            var hit = window.InputHitTest(initialRunBounds.Center);
            Assert.True(ReferenceEquals(hit, run) || hit is Visual visual && visual.GetVisualAncestors().Contains(run));
            Assert.False(view.FindControl<Button>("ReviewAgainButton")!.IsEffectivelyVisible);
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            window.Close();
        }
    }
}
