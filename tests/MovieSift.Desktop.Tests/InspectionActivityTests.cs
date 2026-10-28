using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using MovieSift.Application.Batches;
using MovieSift.Application.Inspection;
using MovieSift.Desktop.Localization;
using MovieSift.Desktop.ViewModels;
using MovieSift.Desktop.Views;
using Xunit;

namespace MovieSift.Desktop.Tests;

/// <summary>Distinguishes real stage/elapsed observations from unavailable duration predictions.</summary>
public sealed class InspectionActivityTests
{
    /// <summary>Advances a monotonic test clock rather than sleeping to trigger the slow-operation notice.</summary>
    [AvaloniaFact]
    public void ElapsedAndSlowNoticeFollowMonotonicTimeWithoutAnInventedEstimate()
    {
        var localization = new LocalizationService();
        var clock = new InspectionClock();
        var activity = new InspectionActivityViewModel(localization, clock);
        var firstItem = Guid.NewGuid();
        var nextItem = Guid.NewGuid();

        try
        {
            activity.Begin(firstItem);
            activity.Report(new(firstItem, InspectionStage.ReadingMetadata));
            clock.Advance(InspectionActivityViewModel.SlowNoticeThreshold);
            activity.Refresh();
            Assert.True(activity.HasSlowNotice);
            Assert.Equal(InspectionActivityViewModel.SlowNoticeThreshold, activity.Elapsed);
            Assert.Contains("00:00:30", activity.ElapsedText, StringComparison.Ordinal);
            Assert.Equal(localization["InspectionRemainingUnknown"], activity.RemainingText);
            Assert.Equal(localization["InspectionStageReadingMetadata"], activity.StageText);
            localization.SetLanguage("ru");
            Assert.Equal(localization["InspectionStageReadingMetadata"], activity.StageText);
            Assert.Equal(InspectionActivityViewModel.SlowNoticeThreshold, activity.Elapsed);
            activity.End();
            activity.Begin(nextItem);
            activity.Report(new(firstItem, InspectionStage.ReadingMetadata));
            Assert.Equal(localization["InspectionStageCheckingSource"], activity.StageText);
            Assert.Equal(TimeSpan.Zero, activity.Elapsed);
            Assert.False(activity.HasSlowNotice);
        }
        finally
        {
            activity.End();
        }
    }

    /// <summary>Holds the actual inspection command at a deterministic gate and cancels through its existing owner.</summary>
    [AvaloniaFact]
    public async Task VisibleInspectionStageCanBeCancelledWithoutStartingMediaWork()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.QueueInitialization;
        var started = BatchViewModelFixture.Signal();
        var release = BatchViewModelFixture.Signal();
        fixture.Backend.OnInspect = async request =>
        {
            request.Progress?.Report(InspectionStage.ReadingContainer);
            started.TrySetResult();
            await release.Task.WaitAsync(request.CancellationToken);
        };
        var view = new BatchView { DataContext = fixture.Batch };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            var operation = fixture.Batch.AddFilesCommand.ExecuteAsync(null);
            await BatchViewModelFixture.Await(started.Task);
            Assert.True(fixture.Batch.IsBusy);
            Assert.True(fixture.Batch.InspectionActivity.IsActive);
            Assert.True(view.FindControl<StackPanel>("InspectionActivityNotice")!.IsVisible);
            Assert.Equal(fixture.Localization["InspectionStageReadingContainer"], fixture.Batch.InspectionActivity.StageText);
            Assert.True(fixture.Batch.CancelCommand.CanExecute(null));
            fixture.Batch.CancelCommand.Execute(null);
            await BatchViewModelFixture.Await(operation);
            Assert.False(fixture.Batch.InspectionActivity.IsActive);
            Assert.False(fixture.Batch.IsBusy);
            Assert.Contains(fixture.Batch.Items, item => item.Snapshot.State == BatchItemState.Cancelled);
            Assert.Empty(fixture.Backend.RemuxRequests);
            Assert.Null(fixture.Session.Snapshot.Review);
        }
        finally
        {
            release.TrySetResult();
            window.Close();
        }
    }

    /// <summary>Provides timestamp-only control; wall-clock changes cannot affect elapsed inspection time.</summary>
    private sealed class InspectionClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        /// <summary>Returns the deterministic monotonic tick counter.</summary>
        public override long GetTimestamp() => timestamp;

        /// <summary>Advances elapsed time without scheduling a delay or media operation.</summary>
        public void Advance(TimeSpan duration) => timestamp += duration.Ticks;
    }
}
