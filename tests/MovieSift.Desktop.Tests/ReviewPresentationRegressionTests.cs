using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MovieSift.Core.Media;
using MovieSift.Desktop.Views;
using Xunit;

namespace MovieSift.Desktop.Tests;

/// <summary>Exercises truthful cancellable Review presentation through controlled destination validation.</summary>
public sealed class ReviewPresentationRegressionTests
{
    /// <summary>Initial and repeated source checks expose cancellation on the active Review surface.</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingReviewCanBeCancelledWithoutLookingLikeAnEmptySuccessfulReview(bool repeat)
    {
        var fixture = new BatchViewModelFixture();
        await fixture.LoadAsync();
        fixture.Batch.Items[0].Tracks.First(track => track.Track.Kind == TrackKind.Subtitles).Keep = false;

        if (repeat)
        {
            await fixture.Batch.ReviewCommand.ExecuteAsync(null);
        }

        var entered = BatchViewModelFixture.Signal();
        var release = BatchViewModelFixture.Signal();
        fixture.SourceAccess.OnValidateDestination = async request =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(request.CancellationToken);
        };
        var window = new Window { Content = new BatchView { DataContext = fixture.Batch } };
        window.Show();
        var reviewing = fixture.Batch.ReviewCommand.ExecuteAsync(null);

        try
        {
            await BatchViewModelFixture.Await(entered.Task);
            Dispatcher.UIThread.RunJobs();
            Assert.True(fixture.Batch.IsReviewOpen);
            var cancel = Assert.Single(window.GetVisualDescendants().OfType<Button>(), control => control.Name == "ReviewCancelButton");
            Assert.True(cancel.IsEffectivelyVisible);
            Assert.True(cancel.IsEffectivelyEnabled);
            Assert.True(cancel.Focus());
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            await BatchViewModelFixture.Await(reviewing);
            Dispatcher.UIThread.RunJobs();
            Assert.False(fixture.Batch.HasReview);
            Assert.False(fixture.Batch.CanRun);
            Assert.Equal(fixture.Localization["ReviewCancelled"], fixture.Batch.ReviewSummaryText);
            Assert.Empty(fixture.Backend.RemuxRequests);
            fixture.SourceAccess.OnValidateDestination = null;
            await fixture.Batch.ReviewCommand.ExecuteAsync(null);
            Assert.True(fixture.Batch.HasReview);
            Assert.True(fixture.Batch.CanRun);
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            fixture.Batch.CancelCommand.Execute(null);
            release.TrySetResult();
            await reviewing;
            window.Close();
        }
    }
    /// <summary>NoChange describes the untouched source without promising a destination or copy-specific changes.</summary>
    [AvaloniaFact]
    public async Task NoChangeRowsDoNotPromiseOutputsAndValidReviewDoesNotRequestAnotherReview()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.LoadAsync();
        fixture.Batch.Items[0].Tracks.First(track => track.Track.Kind == TrackKind.Audio).Keep = false;
        await fixture.Batch.ReviewCommand.ExecuteAsync(null);
        var view = new BatchView { DataContext = fixture.Batch };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();
            var row = Assert.Single(fixture.Batch.ReviewRows, item => item.NoChange);
            var destination = Assert.Single(view.GetVisualDescendants().OfType<TextBlock>(),
                control => control.Name == "ReviewDestination" && ReferenceEquals(control.DataContext, row));
            Assert.False(destination.IsEffectivelyVisible);
            var copyDetails = Assert.Single(view.GetVisualDescendants().OfType<StackPanel>(),
                control => control.Name == "CopyPlanDetails" && ReferenceEquals(control.DataContext, row));
            Assert.False(copyDetails.IsEffectivelyVisible);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), control =>
                ReferenceEquals(control.DataContext, row) && control.IsEffectivelyVisible && control.Text == fixture.Localization["ReviewNoCopy"]);
            Assert.True(fixture.Batch.HasCurrentReview);
            Assert.False(fixture.Batch.NeedsReview);
            var again = Assert.Single(view.GetVisualDescendants().OfType<Button>(), control => control.Name == "ReviewAgainButton");
            Assert.False(again.IsEffectivelyVisible);
            row.Item.Tracks.First(track => track.Track.Kind == TrackKind.Audio).Keep = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(fixture.Batch.HasCurrentReview);
            Assert.True(fixture.Batch.NeedsReview);
            Assert.True(again.IsEffectivelyVisible);
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Repeated keyboard or pointer activation of revalidation cannot cross to the separate Run action.</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevalidationActivationCannotRunCopies(bool pointer)
    {
        var fixture = new BatchViewModelFixture();
        await fixture.LoadAsync();
        await fixture.Batch.ReviewCommand.ExecuteAsync(null);
        fixture.Batch.Items[0].Tracks.First(track => track.Track.Kind == TrackKind.Audio).Keep = false;
        var window = new Window { Content = new BatchView { DataContext = fixture.Batch }, Width = 640, Height = 480 };
        window.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var again = Assert.Single(window.GetVisualDescendants().OfType<Button>(), control => control.Name == "ReviewAgainButton");
            Assert.True(again.IsEffectivelyVisible);
            var point = again.TranslatePoint(new Point(again.Bounds.Width / 2, again.Bounds.Height / 2), window)!.Value;
            Assert.True(again.Focus());

            for (var activation = 0; activation < 2; activation++)
            {
                if (pointer)
                {
                    window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
                    window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
                    continue;
                }

                window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            }

            await fixture.Batch.ReviewCommand.ExecutionTask!;
            Dispatcher.UIThread.RunJobs();
            Assert.True(fixture.Batch.HasCurrentReview);
            Assert.Empty(fixture.Backend.RemuxRequests);
            var run = Assert.Single(window.GetVisualDescendants().OfType<Button>(), control => control.Name == "StartBatchButton");
            Assert.False(run.IsFocused);
            var runOrigin = run.TranslatePoint(default, window)!.Value;
            Assert.False(new Rect(runOrigin, run.Bounds.Size).Contains(point));
        }
        finally
        {
            window.Close();
        }
    }

}
