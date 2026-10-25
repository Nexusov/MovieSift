using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using TrackTrim.Application;
using TrackTrim.Application.Batches;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Media;
using TrackTrim.Desktop.Views;
using Xunit;

namespace TrackTrim.Desktop.Tests;

/// <summary>Checks explicit list reset and presentation ownership without claiming native execution.</summary>
public sealed class BatchNewBatchTests
{
    /// <summary>Starts empty after completed work while retaining preferences and requiring new plans.</summary>
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public async Task CompletedBatchClearsOnlyAfterLocalizedConfirmation(string language)
    {
        var fixture = new BatchViewModelFixture();
        var view = new BatchView { DataContext = fixture.Batch };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            await fixture.PrepareAsync();
            await fixture.Batch.RunCommand.ExecuteAsync(null);
            await fixture.Main.ChangeLanguageCommand.ExecuteAsync(language);
            var completed = fixture.Batch.Snapshot;
            var identifiers = completed.Items.Select(item => item.Id).ToArray();
            var folder = fixture.Batch.OutputFolder;
            var cleanupCount = fixture.Outputs.CleanupCount;
            var workerCount = fixture.Backend.RemuxRequests.Count;
            Assert.All(completed.Items, item => Assert.NotNull(item.Result!.PublishedPath));
            var options = view.FindControl<Button>("WorkspaceOptions")!;
            options.Flyout!.ShowAt(options);
            Dispatcher.UIThread.RunJobs();
            var newBatch = view.FindControl<Button>("NewBatchButton")!;
            Assert.Equal(fixture.Localization["BatchNew"], newBatch.Content);
            Assert.True(newBatch.IsEffectivelyEnabled);
            newBatch.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.FindControl<Border>("NewBatchConfirmation")!.IsVisible);
            Assert.Same(completed, fixture.Batch.Snapshot);
            Assert.False(fixture.Batch.AddFilesCommand.CanExecute(null));
            Assert.False(fixture.Batch.AddFilesCommand.CanExecute(null));
            var confirm = view.FindControl<Button>("ConfirmNewBatchButton")!;
            Assert.Equal(fixture.Localization["BatchNewConfirm"], confirm.Content);
            Assert.Equal(fixture.Localization["BatchKeepCurrent"], view.FindControl<Button>("KeepCurrentBatchButton")!.Content);
            confirm.Command!.Execute(null);
            Assert.Empty(fixture.Batch.Items);
            Assert.Null(fixture.Batch.SelectedItem);
            Assert.False(fixture.Batch.HasReview);
            Assert.False(fixture.Batch.HasNewBatchConfirmation);
            Assert.False(fixture.Batch.HasError);
            Assert.Empty(fixture.Batch.StageText);
            Assert.Empty(fixture.Batch.CurrentItemText);
            Assert.Equal(0, fixture.Batch.ProgressValue);
            Assert.False(fixture.Batch.IsProgressIndeterminate);
            Assert.False(fixture.Batch.RunCommand.CanExecute(null));
            Assert.True(fixture.Batch.AddFilesCommand.CanExecute(null));
            Assert.True(fixture.Batch.AddFilesCommand.CanExecute(null));
            Assert.Equal(folder, fixture.Batch.OutputFolder);
            Assert.Equal(language, fixture.Localization.Language);
            Assert.Equal(language, fixture.Settings.Language);
            Assert.Equal(cleanupCount, fixture.Outputs.CleanupCount);
            Assert.Equal(workerCount, fixture.Backend.RemuxRequests.Count);
            await fixture.Batch.AddFilesCommand.ExecuteAsync(null);
            Assert.Equal(completed.Items.Select(item => item.DiscoveredSource), fixture.Batch.Items.Select(item => item.Snapshot.DiscoveredSource));
            Assert.All(fixture.Batch.Items, item =>
            {
                Assert.DoesNotContain(item.Id, identifiers);
                Assert.Null(item.ApprovedPlan);
                Assert.All(item.Tracks, track => Assert.True(track.Keep));
                Assert.Equal(folder, Path.GetDirectoryName(item.OutputPath));
            });
            Assert.False(fixture.Batch.HasReview);
            Assert.False(fixture.Batch.RunCommand.CanExecute(null));
            await fixture.Batch.ReviewCommand.ExecuteAsync(null);
            Assert.True(fixture.Batch.HasReview);
            Assert.True(fixture.Batch.RunCommand.CanExecute(null));
            Assert.All(fixture.Batch.Items, item => Assert.True(item.ApprovedPlan!.IsNoChange));
            Assert.Equal(workerCount, fixture.Backend.RemuxRequests.Count);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Declining consent preserves the exact review, editors and an ordinary current error.</summary>
    [AvaloniaFact]
    public async Task DecliningConfirmationPreservesExistingPresentationAndReview()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.PrepareAsync();
        fixture.Discovery.Result = new([], [], new(ErrorCode.InvalidSelection, Reason: ErrorReason.BatchFileLimit));
        await fixture.Batch.AddFilesCommand.ExecuteAsync(null);
        var snapshot = fixture.Batch.Snapshot;
        var selected = fixture.Batch.SelectedItem;
        var editors = fixture.Batch.Items.ToArray();
        var error = fixture.Batch.ErrorReasonText;
        var progress = fixture.Batch.ProgressValue;
        Assert.True(fixture.Batch.HasError);
        Assert.True(fixture.Batch.HasReview);
        fixture.Batch.NewBatchCommand.Execute(null);
        Assert.True(fixture.Batch.HasNewBatchConfirmation);
        fixture.Batch.KeepCurrentBatchCommand.Execute(null);
        Assert.False(fixture.Batch.HasNewBatchConfirmation);
        Assert.Same(snapshot, fixture.Batch.Snapshot);
        Assert.Same(selected, fixture.Batch.SelectedItem);
        Assert.Equal(editors, fixture.Batch.Items);
        Assert.Equal(error, fixture.Batch.ErrorReasonText);
        Assert.True(fixture.Batch.HasError);
        Assert.Equal(progress, fixture.Batch.ProgressValue);
        Assert.True(fixture.Batch.RunCommand.CanExecute(null));
        fixture.Batch.NewBatchCommand.Execute(null);
        fixture.Batch.ConfirmNewBatchCommand.Execute(null);
        Assert.False(fixture.Batch.HasError);
        Assert.Empty(fixture.Batch.ReviewIssuesText);
        Assert.Empty(fixture.Backend.RemuxRequests);
        Assert.Equal(0, fixture.Outputs.CleanupCount);
    }

    /// <summary>Rejects consent captured before an Application edit rather than clearing the changed list.</summary>
    [AvaloniaFact]
    public async Task StaleConfirmationCannotClearChangedSession()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.PrepareAsync();
        fixture.Batch.NewBatchCommand.Execute(null);
        Assert.True(fixture.Session.SetIncluded(fixture.Batch.Items[0].Id, false).Applied);
        var changed = fixture.Session.Snapshot;
        fixture.Batch.ConfirmNewBatchCommand.Execute(null);
        Assert.Same(changed, fixture.Session.Snapshot);
        Assert.Equal(2, fixture.Batch.Items.Count);
        Assert.False(fixture.Batch.HasNewBatchConfirmation);
        Assert.Equal(fixture.Localization["ErrorStalePlan"], fixture.Batch.ErrorText);
        Assert.Empty(fixture.Backend.RemuxRequests);
        Assert.Equal(0, fixture.Outputs.CleanupCount);
    }

    /// <summary>Blocks reset during owned discovery and reinspection without detaching either operation.</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveInspectionDisablesNewBatchWithoutDetachingWork(bool discovering)
    {
        var fixture = new BatchViewModelFixture();
        await fixture.PrepareAsync();
        var entered = BatchViewModelFixture.Signal();
        var release = BatchViewModelFixture.Signal();
        fixture.Backend.OnInspect = async request =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(request.CancellationToken);
        };
        fixture.Discovery.OnDiscover = async request =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(request.CancellationToken);
            return new([], []);
        };
        var operation = discovering ? fixture.Batch.AddFilesCommand.ExecuteAsync(null) : fixture.Batch.InspectSelectedCommand.ExecuteAsync(null);

        try
        {
            await BatchViewModelFixture.Await(entered.Task);
            var snapshot = fixture.Batch.Snapshot;
            Assert.False(fixture.Batch.NewBatchCommand.CanExecute(null));
            fixture.Batch.NewBatchCommand.Execute(null);
            Assert.False(fixture.Batch.HasNewBatchConfirmation);
            Assert.Same(snapshot, fixture.Batch.Snapshot);
            Assert.Equal(2, fixture.Batch.Items.Count);
        }
        finally
        {
            release.TrySetResult();
        }

        await BatchViewModelFixture.Await(operation);
        Assert.True(fixture.Batch.NewBatchCommand.CanExecute(null));
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>Keeps warning ownership and failed discard evidence until the established lifecycle resolves it.</summary>
    [AvaloniaFact]
    public async Task WarningCandidateAndFailedCleanupCannotBeReset()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.PrepareAsync();
        fixture.Backend.ExitCodes[fixture.Batch.Items[0].SourcePath] = 1;
        await fixture.Batch.RunCommand.ExecuteAsync(null);
        var warning = fixture.Batch.Snapshot;
        Assert.True(fixture.Batch.HasWarningCandidate);
        Assert.False(fixture.Batch.NewBatchCommand.CanExecute(null));
        fixture.Batch.NewBatchCommand.Execute(null);
        Assert.Same(warning, fixture.Batch.Snapshot);
        Assert.Equal(0, fixture.Outputs.CleanupCount);
        fixture.Outputs.CleanupIssues = [new("fixture-owned", "Synthetic cleanup rejection")];
        await fixture.Batch.DiscardWarningsCommand.ExecuteAsync(null);
        var cleanup = fixture.Batch.Snapshot;
        Assert.True(fixture.Batch.HasSafetyBlock);
        Assert.Contains("Synthetic cleanup rejection", fixture.Batch.Diagnostics);
        Assert.False(fixture.Batch.NewBatchCommand.CanExecute(null));
        fixture.Batch.NewBatchCommand.Execute(null);
        Assert.Same(cleanup, fixture.Batch.Snapshot);
        Assert.Equal(1, fixture.Outputs.CleanupCount);
        Assert.Single(fixture.Backend.RemuxRequests);
    }

    /// <summary>Keeps uncertain publication evidence in its unified queue until an explicit safety decision.</summary>
    [AvaloniaFact]
    public async Task UncertainPublicationBlocksResetOfExistingBatch()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.PrepareAsync();
        var activeItem = fixture.Batch.Items[0];
        activeItem.OutputPath = Path.Combine(fixture.Batch.OutputFolder, "uncertain-publication.mkv");
        await fixture.Batch.ReviewCommand.ExecuteAsync(null);
        fixture.Outputs.PublicationFailure = new(ErrorCode.PublicationUncertain, "Synthetic publication uncertainty");
        await fixture.Batch.RunCommand.ExecuteAsync(null);
        var batch = fixture.Batch.Snapshot;

        Assert.False(fixture.Main.HasCleanupIssue);
        Assert.True(fixture.Main.RequiresCloseDecision);
        Assert.Equal(fixture.Localization["ErrorPublicationUncertain"], fixture.Main.CloseDecisionText);
        Assert.Contains("Synthetic publication uncertainty", activeItem.Diagnostics);
        Assert.False(fixture.Batch.NewBatchCommand.CanExecute(null));
        fixture.Batch.NewBatchCommand.Execute(null);
        Assert.Same(batch, fixture.Batch.Snapshot);
        Assert.False(fixture.Batch.HasNewBatchConfirmation);
        Assert.Equal(BatchItemState.Ready, fixture.Batch.Items[1].Snapshot.State);
        Assert.Single(fixture.Backend.RemuxRequests);
        Assert.Equal(1, fixture.Outputs.CleanupCount);
    }

    /// <summary>Rejects old editor and worker callbacks using the current item and operation identities.</summary>
    [AvaloniaFact]
    public async Task PreviousEditorsAndProgressCannotChangeSubsequentBatch()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.PrepareAsync();
        var previousEditor = fixture.Batch.Items[0];
        await fixture.Batch.RunCommand.ExecuteAsync(null);
        var previousProgress = fixture.Backend.RemuxRequests[0].Progress!;
        fixture.Batch.NewBatchCommand.Execute(null);
        fixture.Batch.ConfirmNewBatchCommand.Execute(null);
        await fixture.PrepareAsync();
        fixture.Discovery.Result = new([], [], new(ErrorCode.InvalidSelection, Reason: ErrorReason.BatchFileLimit));
        await fixture.Batch.AddFilesCommand.ExecuteAsync(null);
        var snapshot = fixture.Batch.Snapshot;
        var error = fixture.Batch.ErrorReasonText;
        previousEditor.Tracks.Single(track => track.Track.Kind == TrackKind.Subtitles).Keep = true;
        previousEditor.OutputPath = "previous-editor-output.mkv";
        previousEditor.ApplyOutputFolder("previous-editor-folder");
        previousEditor.Approve();
        previousEditor.Included = false;
        Assert.Same(snapshot, fixture.Batch.Snapshot);
        Assert.Equal(error, fixture.Batch.ErrorReasonText);
        Assert.True(fixture.Batch.HasError);
        var entered = BatchViewModelFixture.Signal();
        var release = BatchViewModelFixture.Signal();
        fixture.Backend.OnRemux = async request =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(request.CancellationToken);
        };
        var run = fixture.Batch.RunCommand.ExecuteAsync(null);

        try
        {
            await BatchViewModelFixture.Await(entered.Task);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(37, fixture.Batch.ProgressValue);
            previousProgress.Report(99);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(37, fixture.Batch.ProgressValue);
            Assert.False(fixture.Batch.NewBatchCommand.CanExecute(null));
        }
        finally
        {
            release.TrySetResult();
        }

        await BatchViewModelFixture.Await(run);
        Assert.Equal(BatchPhase.Completed, fixture.Batch.Snapshot.Phase);
        Assert.Equal(4, fixture.Backend.RemuxRequests.Count);
    }
}
