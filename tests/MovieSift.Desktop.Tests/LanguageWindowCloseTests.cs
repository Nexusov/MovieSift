using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MovieSift.Application;
using MovieSift.Application.Localization;
using MovieSift.Core.Failures;
using MovieSift.Desktop.Views;
using Xunit;

namespace MovieSift.Desktop.Tests;

/// <summary>Correlates window close with cosmetic download draining and existing mandatory media decisions.</summary>
public sealed class LanguageWindowCloseTests
{
    /// <summary>Explicit warning discard drains a cosmetic download and performs owned cleanup only once.</summary>
    [AvaloniaFact]
    public async Task WarningDiscardCloseWaitsForTranslationDrainWithoutStartingAnotherJob()
    {
        var store = new DrainingTranslations();
        var fixture = new BatchViewModelFixture(translationPacks: store);
        await fixture.PrepareAsync();
        fixture.Backend.ExitCodes[fixture.Batch.Items[0].SourcePath] = 1;
        var window = new MainWindow { DataContext = fixture.Main };
        var closed = BatchViewModelFixture.Signal();
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        await fixture.Batch.RunCommand.ExecuteAsync(null);
        Assert.True(fixture.Main.HasWarningCandidate);
        var selecting = fixture.Main.Languages.SelectAsync("ru");

        try
        {
            await BatchViewModelFixture.Await(store.Started.Task);
            window.Close();
            Assert.True(fixture.Main.HasWarningCloseDecision);
            var discard = window.FindControl<Button>("DiscardWarningsAndCloseButton")!;
            discard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await BatchViewModelFixture.Await(store.CancelObserved.Task);
            await BatchViewModelFixture.Await(fixture.Outputs.CleanupStarted.Task);
            Assert.True(window.IsVisible);
            Assert.False(closed.Task.IsCompleted);
            Assert.True(fixture.Main.Languages.IsBusy);
            discard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.Close();
            Assert.Equal(1, fixture.Outputs.CleanupCount);
            Assert.Single(fixture.Backend.RemuxRequests);
            store.FinishCancellation.TrySetResult();
            await BatchViewModelFixture.Await(selecting);
            await BatchViewModelFixture.Await(closed.Task);
            Assert.False(fixture.Main.HasWarningCandidate);
            Assert.Equal(1, fixture.Outputs.CleanupCount);
            Assert.Single(fixture.Backend.RemuxRequests);
            Assert.Equal("en", fixture.Localization.Language);
        }
        finally
        {
            store.FinishCancellation.TrySetResult();
            await fixture.Main.Languages.ShutdownAsync();

            if (fixture.Main.HasWarningCandidate)
            {
                await fixture.Batch.DiscardWarningsCommand.ExecuteAsync(null);
            }

            window.Close();
        }
    }

    /// <summary>Closing with retained uncertain ownership drains only the translation, leaving all media evidence untouched.</summary>
    [AvaloniaFact]
    public async Task CloseDespiteUncertaintyWaitsForTranslationWithoutRetryingMediaCleanup()
    {
        var store = new DrainingTranslations();
        var fixture = new BatchViewModelFixture(translationPacks: store);
        await fixture.PrepareAsync();
        fixture.Backend.OnRemux = _ => throw new OperationFailureException(new(ErrorCode.ProcessTerminationFailed, "controlled uncertain worker"));
        var window = new MainWindow { DataContext = fixture.Main };
        var closed = BatchViewModelFixture.Signal();
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        await fixture.Batch.RunCommand.ExecuteAsync(null);
        Assert.True(fixture.Main.RequiresCloseDecision);
        Assert.Equal(0, fixture.Outputs.CleanupCount);
        var retainedEvidence = fixture.Batch.Snapshot;
        var selecting = fixture.Main.Languages.SelectAsync("ru");

        try
        {
            await BatchViewModelFixture.Await(store.Started.Task);
            window.Close();
            var close = window.FindControl<Button>("CloseDespiteUncertaintyButton")!;
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await BatchViewModelFixture.Await(store.CancelObserved.Task);
            Assert.True(window.IsVisible);
            Assert.False(closed.Task.IsCompleted);
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.Close();
            Assert.Same(retainedEvidence, fixture.Batch.Snapshot);
            Assert.Equal(0, fixture.Outputs.CleanupCount);
            Assert.Single(fixture.Backend.RemuxRequests);
            store.FinishCancellation.TrySetResult();
            await BatchViewModelFixture.Await(selecting);
            await BatchViewModelFixture.Await(closed.Task);
            Assert.Same(retainedEvidence, fixture.Batch.Snapshot);
            Assert.Equal(0, fixture.Outputs.CleanupCount);
            Assert.Single(fixture.Backend.RemuxRequests);
            Assert.Equal("en", fixture.Localization.Language);
        }
        finally
        {
            store.FinishCancellation.TrySetResult();
            await fixture.Main.Languages.ShutdownAsync();

            if (window.IsVisible)
            {
                window.FindControl<Button>("CloseDespiteUncertaintyButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
        }
    }

    /// <summary>A slow cosmetic cancellation cannot postpone cancellation of the actual media owner.</summary>
    [AvaloniaFact]
    public async Task TranslationDrainDoesNotDelayActiveMediaCancellation()
    {
        var store = new DrainingTranslations();
        var fixture = new BatchViewModelFixture(translationPacks: store);
        await fixture.PrepareAsync();
        var mediaStarted = BatchViewModelFixture.Signal();
        var mediaCancelled = BatchViewModelFixture.Signal();
        var mediaRelease = BatchViewModelFixture.Signal();
        fixture.Backend.OnRemux = async request =>
        {
            mediaStarted.TrySetResult();

            try
            {
                await mediaRelease.Task.WaitAsync(request.CancellationToken);
            }
            catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
            {
                mediaCancelled.TrySetResult();
                throw;
            }
        };
        var running = fixture.Batch.RunCommand.ExecuteAsync(null);
        await BatchViewModelFixture.Await(mediaStarted.Task);
        var selecting = fixture.Main.Languages.SelectAsync("ru");
        await BatchViewModelFixture.Await(store.Started.Task);

        try
        {
            var shutdown = fixture.Main.ShutdownAsync();
            await BatchViewModelFixture.Await(mediaCancelled.Task);
            await BatchViewModelFixture.Await(store.CancelObserved.Task);
            Assert.False(shutdown.IsCompleted);
            Assert.False(store.FinishCancellation.Task.IsCompleted);
            store.FinishCancellation.TrySetResult();
            await BatchViewModelFixture.Await(shutdown);
            await BatchViewModelFixture.Await(running);
            await BatchViewModelFixture.Await(selecting);
            Assert.Equal("en", fixture.Localization.Language);
            Assert.Single(fixture.Backend.RemuxRequests);
        }
        finally
        {
            store.FinishCancellation.TrySetResult();
            mediaRelease.TrySetResult();
            await fixture.Main.ShutdownAsync();
        }
    }

    /// <summary>Waits for an active translation request to finish cancellation before the window and HTTP lifetime end.</summary>
    [AvaloniaFact]
    public async Task CloseWaitsForTranslationCancellationWithoutApplyingItsLateResult()
    {
        var store = new DrainingTranslations();
        var fixture = new BatchViewModelFixture(translationPacks: store);
        var window = new MainWindow { DataContext = fixture.Main };
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        var selecting = fixture.Main.Languages.SelectAsync("ru");

        try
        {
            await BatchViewModelFixture.Await(store.Started.Task);
            Assert.True(fixture.Main.HasActiveOperation);
            window.Close();
            await BatchViewModelFixture.Await(store.CancelObserved.Task);
            Assert.True(window.IsVisible);
            Assert.False(closed.Task.IsCompleted);
            Assert.True(fixture.Main.Languages.IsBusy);
            window.Close();
            Assert.Equal(1, store.DownloadCount);
            store.FinishCancellation.TrySetResult();
            await BatchViewModelFixture.Await(selecting);
            await BatchViewModelFixture.Await(closed.Task);
            Assert.False(window.IsVisible);
            Assert.False(fixture.Main.Languages.IsBusy);
            Assert.Equal("en", fixture.Localization.Language);
            Assert.False(fixture.Main.Languages.IsInstalled("ru"));
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            store.FinishCancellation.TrySetResult();
            await fixture.Main.ShutdownAsync();
            window.Close();
        }
    }

    /// <summary>Closes a newly reopened cosmetic panel even when the warning was already pending before it opened.</summary>
    [AvaloniaFact]
    public async Task ClosingExistingWarningReviewDismissesLanguagePanelAndFocusesItsDecision()
    {
        var fixture = new ViewModelFixture();
        var viewModel = fixture.ViewModel;
        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        try
        {
            await fixture.LoadAsync();
            fixture.Backend.ExitCode = 1;
            fixture.Item.Tracks[1].Keep = false;
            await fixture.Workspace.ReviewCommand.ExecuteAsync(null);
            await fixture.Workspace.RunCommand.ExecuteAsync(null);
            Assert.True(viewModel.HasWarningCandidate);
            Assert.True(viewModel.HasPendingMediaDecision);
            viewModel.ToggleLanguageCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(viewModel.IsLanguageOpen);
            window.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.IsVisible);
            Assert.False(viewModel.IsLanguageOpen);
            Assert.False(viewModel.IsAppearanceOpen);
            Assert.True(viewModel.HasWarningCloseDecision);
            Assert.True(viewModel.HasWarningCandidate);
            Assert.Equal(0, fixture.Outputs.CleanupCount);
            Assert.Same(window.FindControl<Button>("RemainInWarningReviewButton"), window.FocusManager!.GetFocusedElement());
        }
        finally
        {
            await fixture.Workspace.DiscardWarningsCommand.ExecuteAsync(null);
            window.Close();
        }
    }

    /// <summary>Provides a deterministic cancellation acknowledgement boundary without network or media processes.</summary>
    private sealed class DrainingTranslations : ITranslationPackStore
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancelObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FinishCancellation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DownloadCount { get; private set; }

        /// <summary>Starts from an empty portable cache without making a network request.</summary>
        public Task<TranslationPack?> LoadCachedAsync(string locale, CancellationToken cancellationToken = default) =>
            Task.FromResult<TranslationPack?>(null);

        /// <summary>Awaits a controlled drain after observing cancellation so the test can prove close waits.</summary>
        public async Task<TranslationPack> DownloadAsync(string locale, IProgress<TranslationDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            DownloadCount++;
            using var registration = cancellationToken.Register(() => CancelObserved.TrySetResult());
            Started.TrySetResult();
            await FinishCancellation.Task.WaitAsync(TestContext.Current.CancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return ControlledTranslations.Pack(locale);
        }
    }
}
