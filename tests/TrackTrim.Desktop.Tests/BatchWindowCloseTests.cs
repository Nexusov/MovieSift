using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using TrackTrim.Application.Batches;
using TrackTrim.Desktop.Views;
using Xunit;

namespace TrackTrim.Desktop.Tests;

/// <summary>Checks global window ownership across all files in the unified workspace using deterministic ports.</summary>
public sealed class BatchWindowCloseTests
{
    /// <summary>Routes warning review to its frozen batch owner while editing stays disabled.</summary>
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public async Task BatchWarningOwnerRemainsGlobalAcrossSelectedRows(string language)
    {
        var fixture = new BatchViewModelFixture();
        var window = await ShowBatchWarningAsync(fixture);

        try
        {
            var owner = fixture.Batch.Items[0];
            fixture.Batch.SelectedItem = fixture.Batch.Items[1];
            await fixture.Main.ChangeLanguageCommand.ExecuteAsync(language);
            Assert.True(fixture.Main.HasWarningCandidate);
            Assert.False(fixture.Batch.AddFilesCommand.CanExecute(null));
            Assert.Equal(owner.SourcePath, fixture.Batch.CurrentItemText);
            window.Close();
            Assert.True(window.IsVisible);
            Assert.True(fixture.Main.HasWarningCloseDecision);
            Assert.Equal(owner.SourcePath, fixture.Main.WarningCloseSource);
            var remain = window.FindControl<Button>("RemainInWarningReviewButton")!;
            Assert.Equal(fixture.Localization["RemainInWarningReview"], remain.Content);
            Assert.True(remain.Command!.CanExecute(null));
            remain.Command.Execute(null);
            Assert.False(fixture.Main.HasWarningCloseDecision);
            Assert.True(fixture.Batch.HasWarningCandidate);
            Assert.Equal(0, fixture.Outputs.CleanupCount);
            Assert.Single(fixture.Backend.RemuxRequests);
            await fixture.Batch.PublishWarningsCommand.ExecuteAsync(null);
            Assert.Equal(BatchItemState.Completed, owner.Snapshot.State);
            Assert.Equal(1, owner.Snapshot.Result!.Tool!.ExitCode);
            Assert.NotEmpty(owner.PublishedPath);
            Assert.Equal(fixture.Localization["BatchItemCompletedWithWarnings"], owner.StateText);
            Assert.NotEqual("BatchItemCompletedWithWarnings", owner.StateText);
            Assert.Equal(BatchItemState.Ready, fixture.Batch.Items[1].Snapshot.State);
            Assert.Same(fixture.Batch.Items[1], fixture.Batch.SelectedItem);
            Assert.Equal(BatchPhase.Paused, fixture.Batch.Snapshot.Phase);
            Assert.False(fixture.Batch.HasReview);
            Assert.False(fixture.Main.HasWarningCandidate);
            Assert.Single(fixture.Backend.RemuxRequests);
        }
        finally
        {
            if (fixture.Batch.HasWarningCandidate)
            {
                await fixture.Batch.DiscardWarningsCommand.ExecuteAsync(null);
            }

            window.Close();
        }
    }

    /// <summary>Disables both close choices during one awaited cleanup despite repeated requests.</summary>
    [AvaloniaFact]
    public async Task BatchDiscardCloseAwaitsCleanupAndCannotRemainOrDisposeTwice()
    {
        var fixture = new BatchViewModelFixture();
        fixture.Outputs.ContinueCleanup = BatchViewModelFixture.Signal();
        var window = await ShowBatchWarningAsync(fixture);
        var closed = BatchViewModelFixture.Signal();
        window.Closed += (_, _) => closed.TrySetResult();

        try
        {
            window.Close();
            var discard = window.FindControl<Button>("DiscardWarningsAndCloseButton")!;
            discard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await BatchViewModelFixture.Await(fixture.Outputs.CleanupStarted.Task);
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.IsVisible);
            Assert.True(fixture.Main.HasActiveOperation);
            Assert.False(fixture.Main.IsBusy);
            Assert.True(fixture.Batch.IsBusy);
            Assert.False(fixture.Main.CanRespondToWarningClose);
            Assert.False(fixture.Main.RemainInWarningReviewCommand.CanExecute(null));
            Assert.False(window.FindControl<Button>("RemainInWarningReviewButton")!.IsEffectivelyEnabled);
            Assert.False(discard.IsEffectivelyEnabled);
            Assert.False(fixture.Batch.DiscardWarningsCommand.CanExecute(null));
            window.Close();
            window.Close();
            discard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, fixture.Outputs.CleanupCount);
            fixture.Outputs.ContinueCleanup.TrySetResult();
            await BatchViewModelFixture.Await(closed.Task);
            Assert.False(window.IsVisible);
            Assert.False(fixture.Main.HasWarningCandidate);
            Assert.Equal(BatchItemState.Cancelled, fixture.Batch.Items[0].Snapshot.State);
            Assert.Equal(BatchItemState.Ready, fixture.Batch.Items[1].Snapshot.State);
            Assert.Equal(1, fixture.Outputs.CleanupCount);
            Assert.Single(fixture.Backend.RemuxRequests);
        }
        finally
        {
            fixture.Outputs.ContinueCleanup.TrySetResult();
            await fixture.Main.ShutdownAsync();
            window.Close();
        }
    }

    /// <summary>Keeps a failed warning cleanup visible and requires an explicit safety close decision.</summary>
    [AvaloniaFact]
    public async Task BatchDiscardCleanupFailureRemainsVisibleWithoutRepeatedCleanup()
    {
        const string rawFailure = "Synthetic batch warning cleanup access denied.";
        var fixture = new BatchViewModelFixture();
        fixture.Outputs.CleanupIssues = [new("fixture-owned", rawFailure)];
        var window = await ShowBatchWarningAsync(fixture);

        try
        {
            await fixture.Main.ChangeLanguageCommand.ExecuteAsync("ru");
            window.Close();
            var discard = window.FindControl<Button>("DiscardWarningsAndCloseButton")!;
            discard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await BatchViewModelFixture.Await(fixture.Batch.DiscardWarningsCommand.ExecutionTask!);
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.IsVisible);
            Assert.True(fixture.Main.RequiresCloseDecision);
            Assert.True(fixture.Main.HasCleanupIssue);
            Assert.True(fixture.Batch.HasSafetyBlock);
            Assert.Contains(rawFailure, fixture.Batch.Diagnostics);
            Assert.Contains("временных", fixture.Main.CloseDecisionText, StringComparison.OrdinalIgnoreCase);
            Assert.False(fixture.Batch.RunCommand.CanExecute(null));
            discard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.Close();
            Assert.True(window.IsVisible);
            Assert.Equal(1, fixture.Outputs.CleanupCount);
            Assert.Single(fixture.Backend.RemuxRequests);
        }
        finally
        {
            window.FindControl<Button>("CloseDespiteUncertaintyButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
    }

    /// <summary>Cancels active batch work on close and waits for its cleanup before closing or dispatching more work.</summary>
    [AvaloniaFact]
    public async Task ActiveBatchWindowCloseCancelsAndWaitsWithoutStartingNextFile()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.PrepareAsync();
        var remuxEntered = BatchViewModelFixture.Signal();
        fixture.Backend.OnRemux = async request =>
        {
            remuxEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, request.CancellationToken);
        };
        fixture.Outputs.ContinueCleanup = BatchViewModelFixture.Signal();
        var window = new MainWindow { DataContext = fixture.Main };
        var closed = BatchViewModelFixture.Signal();
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        var run = fixture.Batch.RunCommand.ExecuteAsync(null);

        try
        {
            await BatchViewModelFixture.Await(remuxEntered.Task);
            Assert.True(fixture.Main.HasActiveOperation);
            Assert.False(fixture.Main.IsBusy);
            window.Close();
            await BatchViewModelFixture.Await(fixture.Outputs.CleanupStarted.Task);
            Assert.True(window.IsVisible);
            Assert.True(fixture.Batch.IsBusy);
            Assert.False(fixture.Batch.AddFilesCommand.CanExecute(null));
            Assert.Equal(BatchItemState.Ready, fixture.Batch.Items[1].Snapshot.State);
            window.Close();
            Assert.Single(fixture.Backend.RemuxRequests);
            Assert.Equal(1, fixture.Outputs.CleanupCount);
            fixture.Outputs.ContinueCleanup.TrySetResult();
            await BatchViewModelFixture.Await(closed.Task);
            await BatchViewModelFixture.Await(run);
            Assert.False(window.IsVisible);
            Assert.Equal(BatchItemState.Cancelled, fixture.Batch.Items[0].Snapshot.State);
            Assert.Equal(BatchItemState.Ready, fixture.Batch.Items[1].Snapshot.State);
            Assert.Equal(BatchPhase.Paused, fixture.Batch.Snapshot.Phase);
            Assert.Single(fixture.Backend.RemuxRequests);
        }
        finally
        {
            fixture.Outputs.ContinueCleanup.TrySetResult();
            await fixture.Main.ShutdownAsync();
            window.Close();
        }
    }

    /// <summary>Reaches batch warning review through real application owners with a simulated exit-one worker.</summary>
    private static async Task<MainWindow> ShowBatchWarningAsync(BatchViewModelFixture fixture)
    {
        await fixture.PrepareAsync();
        fixture.Backend.ExitCodes[fixture.Batch.Items[0].SourcePath] = 1;
        var window = new MainWindow { DataContext = fixture.Main };
        window.Show();
        await fixture.Batch.RunCommand.ExecuteAsync(null);
        Assert.True(fixture.Batch.HasWarningCandidate);
        Assert.Equal(BatchPhase.Paused, fixture.Batch.Snapshot.Phase);
        return window;
    }
}
