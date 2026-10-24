using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TrackTrim.Application.Batches;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Media;
using TrackTrim.Desktop.Views;
using Xunit;

namespace TrackTrim.Desktop.Tests;

/// <summary>Checks compiled batch bindings and per-file edits without claiming native desktop acceptance.</summary>
public sealed class BatchEditorTests
{
    private const int MinimumWindowWidth = 640;
    private const int MinimumWindowHeight = 480;
    private const double LayoutTolerance = 1;

    /// <summary>Preserves independently approved IDs and selections through row and language changes.</summary>
    [AvaloniaFact]
    public async Task IndependentEditorsPreservePlansAcrossRowAndLanguageSwitches()
    {
        var fixture = new BatchViewModelFixture();
        var view = new BatchView { DataContext = fixture.Batch };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            await fixture.LoadAsync();
            var first = fixture.Batch.Items[0];
            var second = fixture.Batch.Items[1];
            Assert.All(first.Tracks.Concat(second.Tracks), track => Assert.True(track.Keep));
            fixture.Batch.SelectedItem = first;
            first.Tracks.Single(track => track.Track.Id == 1).Keep = false;
            fixture.Batch.ApproveSelectedCommand.Execute(null);
            fixture.Batch.SelectedItem = second;
            second.Tracks.Single(track => track.Track.Id == 25).Keep = false;
            fixture.Batch.ApproveSelectedCommand.Execute(null);
            var firstPlan = first.ApprovedPlan;
            var secondPlan = second.ApprovedPlan;
            var firstRows = first.Tracks.ToArray();
            var secondRows = second.Tracks.ToArray();

            Assert.Equal([0, 2, 3], firstPlan!.RetainedTracks.Select(track => track.Id));
            Assert.Equal([77, 8, 61], secondPlan!.RetainedTracks.Select(track => track.Id));
            await fixture.Batch.ReviewCommand.ExecuteAsync(null);
            var review = fixture.Batch.Snapshot.Review;
            Assert.True(fixture.Batch.RunCommand.CanExecute(null));
            fixture.Batch.SelectedItem = first;
            await fixture.Main.ChangeLanguageCommand.ExecuteAsync("ru");
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("ru", fixture.Settings.Language);
            Assert.Equal(fixture.Batch.RunActionText, Assert.Single(view.GetVisualDescendants().OfType<Button>(), button => button.Name == "StartBatchButton").Content);
            Assert.Equal(fixture.Localization["BatchAddFiles"], view.FindControl<Button>("AddBatchFilesButton")!.Content);
            Assert.Same(firstPlan, first.ApprovedPlan);
            Assert.Same(secondPlan, second.ApprovedPlan);
            Assert.Same(review, fixture.Batch.Snapshot.Review);
            Assert.Equal(firstRows, first.Tracks);
            Assert.Equal(secondRows, second.Tracks);
            Assert.False(first.Tracks.Single(track => track.Track.Id == 1).Keep);
            Assert.False(second.Tracks.Single(track => track.Track.Id == 25).Keep);
            fixture.Batch.SelectedItem = second;
            Assert.Same(second, fixture.Batch.SelectedItem);
            Assert.Equal("Аудио", second.Tracks.Single(track => track.Track.Id == 61).KindText);
            await fixture.Main.ChangeLanguageCommand.ExecuteAsync("en");
            Assert.Same(review, fixture.Batch.Snapshot.Review);
            Assert.Equal("Audio", second.Tracks.Single(track => track.Track.Id == 61).KindText);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Invalidates only a local edit, then every affected approval after an explicit shared-folder change.</summary>
    [AvaloniaFact]
    public async Task SharedFolderInvalidatesAllPlansWhileLocalDestinationInvalidatesOne()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.PrepareAsync();
        var first = fixture.Batch.Items[0];
        var second = fixture.Batch.Items[1];
        var secondPlan = second.ApprovedPlan;
        var firstSelection = first.Tracks.Select(track => track.Keep).ToArray();
        var secondSelection = second.Tracks.Select(track => track.Keep).ToArray();
        first.OutputPath = Path.Combine(Path.GetTempPath(), "independent output.mkv");

        Assert.Null(first.ApprovedPlan);
        Assert.Same(secondPlan, second.ApprovedPlan);
        Assert.False(fixture.Batch.HasReview);
        fixture.Batch.SelectedItem = first;
        fixture.Batch.ApproveSelectedCommand.Execute(null);
        await fixture.Batch.ReviewCommand.ExecuteAsync(null);
        Assert.True(fixture.Batch.HasReview);

        fixture.Dialogs.OutputFolder = Path.Combine(Path.GetTempPath(), "changed общая папка");
        await fixture.Batch.ChooseOutputCommand.ExecuteAsync(null);
        Assert.False(fixture.Batch.HasReview);
        Assert.All(fixture.Batch.Items, item => Assert.Null(item.ApprovedPlan));
        Assert.All(fixture.Batch.Items, item => Assert.Equal(fixture.Dialogs.OutputFolder, Path.GetDirectoryName(item.OutputPath)));
        Assert.Equal(firstSelection, first.Tracks.Select(track => track.Keep));
        Assert.Equal(secondSelection, second.Tracks.Select(track => track.Keep));
        Assert.False(fixture.Batch.RunCommand.CanExecute(null));
    }

    /// <summary>Routes delayed serial inspection responses by item identity instead of the selected editor.</summary>
    [AvaloniaFact]
    public async Task LateInspectionDoesNotReplaceAnotherSelectedRowsTracks()
    {
        var fixture = new BatchViewModelFixture();
        fixture.Session.AddSources(fixture.Backend.Inspections.Values.Select(inspection => inspection.Source));
        var first = fixture.Batch.Items[0];
        var second = fixture.Batch.Items[1];
        var firstEntered = BatchViewModelFixture.Signal();
        var firstRelease = BatchViewModelFixture.Signal();
        var secondEntered = BatchViewModelFixture.Signal();
        var secondRelease = BatchViewModelFixture.Signal();
        fixture.Backend.OnInspect = async request =>
        {
            if (request.SourcePath == first.SourcePath)
            {
                firstEntered.TrySetResult();
                await firstRelease.Task;
                return;
            }

            secondEntered.TrySetResult();
            await secondRelease.Task;
        };
        var inspection = fixture.Batch.InspectPendingCommand.ExecuteAsync(null);

        try
        {
            await BatchViewModelFixture.Await(firstEntered.Task);
            fixture.Batch.SelectedItem = second;
            firstRelease.TrySetResult();
            await BatchViewModelFixture.Await(secondEntered.Task);
            Assert.Same(second, fixture.Batch.SelectedItem);
            Assert.Empty(second.Tracks);
            Assert.Equal([0, 1, 2, 3], first.Tracks.Select(track => track.Track.Id));
        }
        finally
        {
            firstRelease.TrySetResult();
            secondRelease.TrySetResult();
        }

        await BatchViewModelFixture.Await(inspection);
        Assert.Same(second, fixture.Batch.SelectedItem);
        Assert.Equal([77, 25, 8, 61], second.Tracks.Select(track => track.Track.Id));
        Assert.Equal(first.SourcePath, first.Snapshot.Inspection!.Source.FullPath);
        Assert.Equal(second.SourcePath, second.Snapshot.Inspection!.Source.FullPath);
    }

    /// <summary>Requires explicit exclusion of invalid discovery rows before the reviewed valid siblings can run.</summary>
    [AvaloniaFact]
    public async Task FailedDiscoveryRowBlocksReviewUntilExplicitlyExcluded()
    {
        var fixture = new BatchViewModelFixture();
        var missingPath = Path.Combine(Path.GetTempPath(), "missing source.mkv");
        fixture.Discovery.Result = new(fixture.Backend.Inspections.Values.Select(inspection => inspection.Source).ToImmutableArray(),
            [new(missingPath, new(ErrorCode.InvalidSource, "raw discovery failure"))]);
        await fixture.PrepareAsync();
        var missing = Assert.Single(fixture.Batch.Items, item => item.SourcePath == missingPath);

        Assert.True(missing.Included);
        Assert.True(missing.HasError);
        Assert.Equal(BatchItemState.Failed, missing.Snapshot.State);
        Assert.Equal(fixture.Dialogs.OutputFolder, Path.GetDirectoryName(missing.OutputPath));
        Assert.True(fixture.Batch.HasError);
        Assert.Contains(missingPath, fixture.Batch.ReviewIssuesText);
        Assert.False(fixture.Batch.HasReview);
        Assert.False(fixture.Batch.RunCommand.CanExecute(null));
        missing.Included = false;
        await fixture.Batch.ReviewCommand.ExecuteAsync(null);
        Assert.True(fixture.Batch.HasReview);
        Assert.True(fixture.Batch.HasExcluded);
        Assert.Contains(missingPath, fixture.Batch.ExcludedText);
        Assert.Equal(2, fixture.Batch.Snapshot.Review!.Items.Length);
        var review = fixture.Batch.Snapshot.Review;
        await fixture.Main.ChangeLanguageCommand.ExecuteAsync("ru");
        Assert.False(missing.Included);
        Assert.Same(review, fixture.Batch.Snapshot.Review);
        Assert.Equal(fixture.Localization["ErrorInvalidSource"], missing.ErrorText);
        Assert.Equal("raw discovery failure", missing.Diagnostics);
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>Removes obsolete aggregate errors while requiring a new review of the remaining approved files.</summary>
    [AvaloniaFact]
    public async Task RemovingFailedDiscoveryRowClearsObsoleteReviewErrorsAndRequiresFreshReview()
    {
        var fixture = new BatchViewModelFixture();
        var missingPath = Path.Combine(Path.GetTempPath(), "missing batch source.mkv");
        fixture.Discovery.Result = new(fixture.Backend.Inspections.Values.Select(inspection => inspection.Source).ToImmutableArray(),
            [new(missingPath, new(ErrorCode.InvalidSource, "raw discovery failure"))]);
        await fixture.PrepareAsync();
        var missing = Assert.Single(fixture.Batch.Items, item => item.SourcePath == missingPath);
        var remainingPlans = fixture.Batch.Items.Where(item => item.Id != missing.Id).Select(item => item.ApprovedPlan).ToArray();
        Assert.True(fixture.Batch.HasError);
        Assert.Contains(missingPath, fixture.Batch.ReviewIssuesText);
        Assert.False(fixture.Batch.HasReview);
        fixture.Batch.SelectedItem = missing;
        Assert.True(fixture.Batch.RemoveSelectedCommand.CanExecute(null));

        fixture.Batch.RemoveSelectedCommand.Execute(null);

        Assert.Equal(2, fixture.Batch.Items.Count);
        Assert.DoesNotContain(fixture.Batch.Items, item => item.Id == missing.Id);
        Assert.Empty(fixture.Batch.ReviewIssuesText);
        Assert.False(fixture.Batch.HasError);
        Assert.Equal(remainingPlans, fixture.Batch.Items.Select(item => item.ApprovedPlan));
        Assert.False(fixture.Batch.HasReview);
        Assert.False(fixture.Batch.RunCommand.CanExecute(null));
        Assert.Empty(fixture.Backend.RemuxRequests);

        await fixture.Batch.ReviewCommand.ExecuteAsync(null);

        Assert.True(fixture.Batch.HasReview);
        Assert.False(fixture.Batch.HasError);
        Assert.Empty(fixture.Batch.ReviewIssuesText);
        Assert.Equal(remainingPlans, fixture.Batch.Snapshot.Review!.Items.Select(item => item.Plan));
        Assert.True(fixture.Batch.RunCommand.CanExecute(null));
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>Keeps failed evidence visible while an explicitly excluded item no longer blocks remaining work.</summary>
    [AvaloniaFact]
    public async Task FailedRunCanBeExplicitlyExcludedAndCompletedRowsCannotBeEdited()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.PrepareAsync();
        var first = fixture.Batch.Items[0];
        var second = fixture.Batch.Items[1];
        fixture.Backend.ExitCodes[first.SourcePath] = 2;
        await fixture.Batch.RunCommand.ExecuteAsync(null);

        Assert.Equal(BatchItemState.Failed, first.Snapshot.State);
        Assert.True(first.HasError);
        Assert.Equal(BatchItemState.Ready, second.Snapshot.State);
        Assert.Single(fixture.Backend.RemuxRequests);
        Assert.False(fixture.Batch.HasReview);
        Assert.False(fixture.Batch.RunCommand.CanExecute(null));
        first.Included = false;
        await fixture.Batch.ReviewCommand.ExecuteAsync(null);
        await fixture.Batch.RunCommand.ExecuteAsync(null);
        Assert.Equal(BatchPhase.Completed, fixture.Batch.Snapshot.Phase);
        Assert.Equal(BatchItemState.Failed, first.Snapshot.State);
        Assert.True(first.HasError);
        Assert.Equal(BatchItemState.Completed, second.Snapshot.State);
        Assert.False(second.CanChangeInclusion);
        Assert.False(second.CanEditPlan);
        Assert.Equal(2, fixture.Backend.RemuxRequests.Count);
        Assert.NotEmpty(second.PublishedPath);
    }

    /// <summary>Localizes a running batch without cancelling it and rejects delayed progress from the previous file.</summary>
    [AvaloniaFact]
    public async Task RunningLanguageAndRowChangesPreserveOwnershipAndRejectOldProgress()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.PrepareAsync();
        var first = fixture.Batch.Items[0];
        var second = fixture.Batch.Items[1];
        var firstPlan = first.ApprovedPlan;
        var secondPlan = second.ApprovedPlan;
        var firstEntered = BatchViewModelFixture.Signal();
        var firstRelease = BatchViewModelFixture.Signal();
        var secondEntered = BatchViewModelFixture.Signal();
        var secondRelease = BatchViewModelFixture.Signal();
        fixture.Backend.OnRemux = async request =>
        {
            if (request.Plan.Inspection.Source.FullPath == first.SourcePath)
            {
                firstEntered.TrySetResult();
                await firstRelease.Task.WaitAsync(request.CancellationToken);
                return;
            }

            secondEntered.TrySetResult();
            await secondRelease.Task.WaitAsync(request.CancellationToken);
        };
        var run = fixture.Batch.RunCommand.ExecuteAsync(null);

        try
        {
            await BatchViewModelFixture.Await(firstEntered.Task);
            Dispatcher.UIThread.RunJobs();
            var progress = fixture.Batch.ProgressValue;
            fixture.Batch.SelectedItem = second;
            await fixture.Main.ChangeLanguageCommand.ExecuteAsync("ru");
            Assert.True(fixture.Batch.IsBusy);
            Assert.Equal(progress, fixture.Batch.ProgressValue);
            Assert.Equal(first.SourcePath, fixture.Batch.CurrentItemText);
            Assert.Same(firstPlan, first.ApprovedPlan);
            Assert.Same(secondPlan, second.ApprovedPlan);
            firstRelease.TrySetResult();
            await BatchViewModelFixture.Await(secondEntered.Task);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(37, fixture.Batch.ProgressValue);
            fixture.Backend.RemuxRequests[0].Progress!.Report(99);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(37, fixture.Batch.ProgressValue);
            Assert.Equal(second.SourcePath, fixture.Batch.CurrentItemText);
            Assert.Equal(fixture.Localization["Remuxing"], fixture.Batch.StageText);
        }
        finally
        {
            firstRelease.TrySetResult();
            secondRelease.TrySetResult();
        }

        await BatchViewModelFixture.Await(run);
        Assert.Equal(BatchPhase.Completed, fixture.Batch.Snapshot.Phase);
        Assert.Equal(2, fixture.Backend.RemuxRequests.Count);
        Assert.False(fixture.Batch.IsBusy);
    }

    /// <summary>Requires separate zero-audio consent for every file rather than inheriting another row's decision.</summary>
    [AvaloniaFact]
    public async Task ZeroAudioConsentBelongsOnlyToItsOwnFile()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.LoadAsync();
        var first = fixture.Batch.Items[0];
        var second = fixture.Batch.Items[1];

        foreach (var track in first.Tracks.Concat(second.Tracks).Where(track => track.IsAudio))
        {
            track.Keep = false;
        }

        fixture.Batch.SelectedItem = first;
        first.ZeroAudioConfirmed = true;
        fixture.Batch.ApproveSelectedCommand.Execute(null);
        Assert.NotNull(first.ApprovedPlan);
        fixture.Batch.SelectedItem = second;
        fixture.Batch.ApproveSelectedCommand.Execute(null);
        Assert.Null(second.ApprovedPlan);
        Assert.False(second.ZeroAudioConfirmed);
        Assert.True(second.NeedsZeroAudioConfirmation);
        Assert.Equal(fixture.Localization["ErrorZeroAudioConfirmationRequired"], second.ErrorText);
        Assert.True(first.ZeroAudioConfirmed);
        await fixture.Main.ChangeLanguageCommand.ExecuteAsync("ru");
        Assert.NotNull(first.ApprovedPlan);
        Assert.Null(second.ApprovedPlan);
        Assert.False(second.ZeroAudioConfirmed);
        second.ZeroAudioConfirmed = true;
        fixture.Batch.ApproveSelectedCommand.Execute(null);
        await fixture.Batch.ReviewCommand.ExecuteAsync(null);
        Assert.True(fixture.Batch.HasReview);
        Assert.All(fixture.Batch.Snapshot.Review!.Items, reviewed =>
        {
            Assert.True(reviewed.Plan.ZeroAudioConfirmed);
            Assert.DoesNotContain(reviewed.Plan.RetainedTracks, track => track.Kind == TrackKind.Audio);
        });
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>Preserves existing approvals and review when pickers cancel, an addition is rejected, or every row is duplicate.</summary>
    [AvaloniaFact]
    public async Task CancelledRejectedAndDuplicateAdditionsPreserveApprovedQueue()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.PrepareAsync();
        var review = fixture.Batch.Snapshot.Review;
        var plans = fixture.Batch.Items.Select(item => item.ApprovedPlan).ToArray();
        var identifiers = fixture.Batch.Items.Select(item => item.Id).ToArray();
        var sources = fixture.Dialogs.Sources;
        fixture.Dialogs.Sources = [];
        await fixture.Batch.AddFilesCommand.ExecuteAsync(null);
        fixture.Dialogs.OutputFolder = null;
        await fixture.Batch.ChooseOutputCommand.ExecuteAsync(null);
        Assert.Same(review, fixture.Batch.Snapshot.Review);
        Assert.Equal(plans, fixture.Batch.Items.Select(item => item.ApprovedPlan));
        fixture.Dialogs.Sources = sources;
        fixture.Discovery.Result = new([], [], new(ErrorCode.InvalidSource, Reason: ErrorReason.BatchFileLimit));
        await fixture.Batch.AddFilesCommand.ExecuteAsync(null);
        Assert.True(fixture.Batch.HasError);
        Assert.Equal(fixture.Localization["ErrorReasonBatchFileLimit"], fixture.Batch.ErrorReasonText);
        Assert.Same(review, fixture.Batch.Snapshot.Review);
        Assert.Equal(identifiers, fixture.Batch.Items.Select(item => item.Id));

        fixture.Discovery.Result = null;
        await fixture.Batch.AddFilesCommand.ExecuteAsync(null);
        Assert.False(fixture.Batch.HasError);
        Assert.Same(review, fixture.Batch.Snapshot.Review);
        Assert.Equal(identifiers, fixture.Batch.Items.Select(item => item.Id));
        Assert.Equal(plans, fixture.Batch.Items.Select(item => item.ApprovedPlan));
        Assert.True(fixture.Batch.RunCommand.CanExecute(null));
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>Keeps active Cancel controls inside the minimum headless viewport while long review content scrolls.</summary>
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public async Task ActiveCancelRemainsInMinimumViewportOutsideLongContent(string language)
    {
        var fixture = new BatchViewModelFixture();
        await fixture.PrepareAsync();
        var entered = BatchViewModelFixture.Signal();
        fixture.Backend.OnRemux = async request =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, request.CancellationToken);
        };
        var window = new MainWindow
        {
            DataContext = fixture.Main,
            Width = MinimumWindowWidth,
            Height = MinimumWindowHeight
        };
        window.Show();
        await fixture.Main.ChangeLanguageCommand.ExecuteAsync(language);
        var run = fixture.Batch.RunCommand.ExecuteAsync(null);

        try
        {
            await BatchViewModelFixture.Await(entered.Task);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var view = Assert.Single(window.GetVisualDescendants().OfType<BatchView>());
            var cancel = view.FindControl<Button>("CancelBatchButton")!;
            var controls = view.FindControl<StackPanel>("BatchRunControls")!;
            var point = cancel.TranslatePoint(new(0, 0), window);
            Assert.NotNull(point);
            Assert.True(cancel.IsVisible);
            Assert.True(cancel.IsEnabled);
            Assert.Equal(Dock.Bottom, DockPanel.GetDock(controls));
            Assert.DoesNotContain(cancel.GetVisualAncestors(), ancestor => ancestor is ScrollViewer);
            Assert.True(point.Value.Y >= 0);
            Assert.True(point.Value.Y + cancel.Bounds.Height <= window.ClientSize.Height + LayoutTolerance);
            var scroll = view.FindControl<ScrollViewer>("WorkspaceScrollViewer")!;
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
        }
        finally
        {
            fixture.Batch.CancelCommand.Execute(null);
            await BatchViewModelFixture.Await(run);
            window.Close();
        }
    }

    /// <summary>Moves failed or cancelled rows to the shared folder while retaining their previous outcome until fresh approval.</summary>
    [AvaloniaTheory]
    [InlineData(BatchItemState.Failed)]
    [InlineData(BatchItemState.Cancelled)]
    public async Task SharedFolderCorrectsStoppedRowsWithoutLosingFailureEvidence(BatchItemState terminalState)
    {
        var fixture = new BatchViewModelFixture();
        await fixture.PrepareAsync();
        var first = fixture.Batch.Items[0];
        var second = fixture.Batch.Items[1];
        fixture.Backend.ExitCodes[first.SourcePath] = 2;
        var entered = BatchViewModelFixture.Signal();
        fixture.Backend.OnRemux = request =>
        {
            entered.TrySetResult();
            return terminalState == BatchItemState.Cancelled
                ? Task.Delay(Timeout.InfiniteTimeSpan, request.CancellationToken) : Task.CompletedTask;
        };
        var run = fixture.Batch.RunCommand.ExecuteAsync(null);
        await BatchViewModelFixture.Await(entered.Task);

        if (terminalState == BatchItemState.Cancelled)
        {
            fixture.Batch.CancelCommand.Execute(null);
        }

        await BatchViewModelFixture.Await(run);
        Assert.Equal(terminalState, first.Snapshot.State);
        var priorResult = first.Snapshot.Result;
        var priorFailure = first.Snapshot.Result!.Failure;
        var firstSelection = first.Tracks.Select(track => track.Keep).ToArray();
        var secondSelection = second.Tracks.Select(track => track.Keep).ToArray();
        fixture.Dialogs.OutputFolder = Path.Combine(Path.GetTempPath(), "new stopped destination Кириллица");
        await fixture.Batch.ChooseOutputCommand.ExecuteAsync(null);

        Assert.All(fixture.Batch.Items, item => Assert.Equal(fixture.Dialogs.OutputFolder, Path.GetDirectoryName(item.OutputPath)));
        Assert.All(fixture.Batch.Items, item => Assert.Null(item.ApprovedPlan));
        Assert.All(fixture.Batch.Items, item => Assert.Equal(BatchItemState.NeedsReview, item.Snapshot.State));
        Assert.Same(priorResult, first.Snapshot.Result);
        Assert.Same(priorFailure, first.Snapshot.Result!.Failure);
        Assert.Equal(firstSelection, first.Tracks.Select(track => track.Keep));
        Assert.Equal(secondSelection, second.Tracks.Select(track => track.Keep));
        Assert.Equal(terminalState == BatchItemState.Failed, first.HasError);
        Assert.False(fixture.Batch.HasReview);
        Assert.False(fixture.Batch.RunCommand.CanExecute(null));
        await fixture.Batch.ReviewCommand.ExecuteAsync(null);
        Assert.True(fixture.Batch.HasReview);
        Assert.True(fixture.Batch.RunCommand.CanExecute(null));
        Assert.All(fixture.Batch.Items, item => Assert.Equal(BatchItemState.Ready, item.Snapshot.State));
        Assert.All(fixture.Batch.Items, item => Assert.NotNull(item.ApprovedPlan));
        Assert.Single(fixture.Backend.RemuxRequests);
    }

    /// <summary>Clears obsolete selection errors after explicit reinspection replaces the editor's inspection state.</summary>
    [AvaloniaFact]
    public async Task ReinspectionClearsOldZeroAudioPlanningError()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.LoadAsync();
        var first = fixture.Batch.Items[0];
        fixture.Batch.SelectedItem = first;

        foreach (var track in first.Tracks.Where(track => track.IsAudio))
        {
            track.Keep = false;
        }

        fixture.Batch.ApproveSelectedCommand.Execute(null);
        Assert.True(first.HasError);
        Assert.Equal(fixture.Localization["ErrorZeroAudioConfirmationRequired"], first.ErrorText);
        await fixture.Batch.InspectSelectedCommand.ExecuteAsync(null);
        Assert.Equal(BatchItemState.NeedsReview, first.Snapshot.State);
        Assert.False(first.HasError);
        Assert.Empty(first.ErrorText);
        Assert.False(first.ZeroAudioConfirmed);
        Assert.All(first.Tracks, track => Assert.True(track.Keep));
        Assert.Null(first.ApprovedPlan);
    }
}
