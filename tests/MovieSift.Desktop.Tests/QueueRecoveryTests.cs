using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MovieSift.Application.Batches;
using MovieSift.Application.QueueLists;
using MovieSift.Core.Media;
using MovieSift.Core.Planning;
using MovieSift.Desktop.Views;
using MovieSift.Infrastructure.Files;
using MovieSift.Infrastructure.Persistence;
using Xunit;

namespace MovieSift.Desktop.Tests;

/// <summary>Separates restored listing data, explicit UI decisions and independently owned media work.</summary>
public sealed class QueueRecoveryTests
{
    /// <summary>Startup never adds, inspects or dispatches a saved file before explicit Restore.</summary>
    [AvaloniaFact]
    public async Task StartupOffersChoiceAndRestoreRequiresFreshInspectionAndPlans()
    {
        var store = SavedStore();
        var fixture = new BatchViewModelFixture(queueStore: store);
        var inspections = 0;
        fixture.Backend.OnInspect = _ => { inspections++; return Task.CompletedTask; };
        await fixture.Main.InitializeAsync();
        Assert.True(fixture.Batch.Recovery.HasRecoveryChoice);
        Assert.Empty(fixture.Batch.Items);
        Assert.False(fixture.Batch.AddFilesCommand.CanExecute(null));
        Assert.False(fixture.Batch.RunCommand.CanExecute(null));
        Assert.Equal(0, inspections);
        Assert.Empty(fixture.Backend.RemuxRequests);
        var saved = store.List;
        await fixture.Batch.Recovery.RestoreCommand.ExecuteAsync(null);
        Assert.False(fixture.Batch.Recovery.HasRecoveryChoice);
        Assert.Equal(saved.Entries.Select(entry => entry.SourcePath), fixture.Batch.Items.Select(item => item.SourcePath));
        Assert.Equal(saved.Entries.Select(entry => entry.DestinationPath), fixture.Batch.Items.Select(item => item.OutputPath));
        Assert.All(fixture.Session.Snapshot.Items, item =>
        {
            Assert.Equal(BatchItemState.PendingInspection, item.State);
            Assert.Null(item.Inspection);
            Assert.Null(item.ApprovedPlan);
            Assert.Empty(item.CurrentRetainedTrackIds);
        });
        Assert.Equal(0, inspections);
        await fixture.Batch.ReviewCommand.ExecuteAsync(null);
        Assert.False(fixture.Batch.HasReview);
        await fixture.Batch.InspectPendingCommand.ExecuteAsync(null);
        Assert.Equal(2, inspections);

        foreach (var item in fixture.Batch.Items)
        {
            fixture.Batch.SelectedItem = item;
            Assert.All(item.Tracks, track => Assert.True(track.Keep));
            Assert.False(item.HasPlan);
            Assert.False(item.ZeroAudioConfirmed);
            item.Tracks.Single(track => track.Track.Kind == TrackKind.Subtitles).Keep = false;
        }

        Assert.False(fixture.Batch.CanRun);
        await fixture.Batch.ReviewCommand.ExecuteAsync(null);
        Assert.True(fixture.Batch.CanRun);
        Assert.Empty(fixture.Backend.RemuxRequests);
        Assert.Equal(0, fixture.Outputs.CleanupCount);
    }

    /// <summary>Ordinary destination edits and approvals checkpoint only listing content across fresh UI instances.</summary>
    [AvaloniaFact]
    public async Task AutosaveRoundtripRetainsDestinationsAndInclusionWithoutApprovalAuthority()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.PrepareAsync();
        fixture.Batch.Items[1].Included = false;
        await fixture.Batch.Recovery.WhenIdleAsync();
        var saved = fixture.QueueStore.List;
        Assert.Equal(2, saved.Entries.Length);
        Assert.False(saved.Entries[1].Included);
        Assert.Equal(fixture.Batch.Items.Select(item => item.OutputPath), saved.Entries.Select(entry => entry.DestinationPath));
        var restored = new BatchViewModelFixture(queueStore: fixture.QueueStore);
        Assert.Empty(restored.Batch.Items);
        await restored.Batch.Recovery.RestoreCommand.ExecuteAsync(null);
        Assert.False(restored.Batch.Items[1].Included);
        Assert.All(restored.Batch.Items, item => Assert.Null(item.ApprovedPlan));
        Assert.False(restored.Batch.HasReview);
        Assert.Empty(restored.Backend.RemuxRequests);
    }

    /// <summary>Real queue-file discard preserves a real owned candidate/journal and every unrelated media byte.</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreOrDiscardNeverDeletesOwnershipOrMedia(bool restore)
    {
        var root = Path.Combine(Path.GetTempPath(), "MovieSift queue recovery " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var data = Path.Combine(root, "data");
            var store = new PortableQueueListStore(data);
            var saved = SavedStore().List;
            var written = await store.WriteAsync(new(saved, null, TestContext.Current.CancellationToken));
            Assert.True(written.Committed);
            var fixture = new BatchViewModelFixture(queueStore: new PortableQueueListStore(data));
            var inspection = fixture.Backend.Inspections.Values.First();
            var destination = Path.Combine(root, "previous output.mkv");
            var plan = PlanBuilder.Build(new(inspection, [0, 2, 3], destination, false)).Plan!;
            var outputs = new OwnedOutputStore(data);
            var owned = await outputs.ReserveAsync(new(plan, TestContext.Current.CancellationToken));
            await File.WriteAllTextAsync(owned.CandidatePath, "owned incomplete media", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(destination, "published sentinel", TestContext.Current.CancellationToken);
            var record = Assert.Single(Directory.GetFiles(Path.Combine(data, "ownership")));
            var recordBefore = await File.ReadAllBytesAsync(record, TestContext.Current.CancellationToken);
            var candidateBefore = await File.ReadAllBytesAsync(owned.CandidatePath, TestContext.Current.CancellationToken);
            fixture.Outputs.Interrupted = await outputs.FindInterruptedAsync(TestContext.Current.CancellationToken);
            await fixture.Main.InitializeAsync();
            Assert.True(fixture.Main.HasInterrupted);
            var ownershipNotice = fixture.Main.InterruptedDetails;
            var command = restore ? fixture.Batch.Recovery.RestoreCommand : fixture.Batch.Recovery.DiscardCommand;
            await command.ExecuteAsync(null);
            await fixture.Batch.Recovery.WhenIdleAsync();
            Assert.Equal(recordBefore, await File.ReadAllBytesAsync(record, TestContext.Current.CancellationToken));
            Assert.Equal(candidateBefore, await File.ReadAllBytesAsync(owned.CandidatePath, TestContext.Current.CancellationToken));
            Assert.Equal("published sentinel", await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken));
            Assert.Equal(ownershipNotice, fixture.Main.InterruptedDetails);
            Assert.Equal(0, fixture.Outputs.CleanupCount);
            Assert.Empty(fixture.Backend.RemuxRequests);
            Assert.Equal(restore ? 2 : 0, fixture.Batch.Items.Count);
            Assert.Equal(restore ? 2 : 0, (await store.ReadAsync(TestContext.Current.CancellationToken)).List!.Entries.Length);
        }
        finally
        {
            // This unique test-owned directory was created above and never contains user media.
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Queue-list errors remain visible while manual rules/jobs keep their independent ownership gate.</summary>
    [AvaloniaTheory]
    [InlineData(QueueListErrorCode.InvalidRecord)]
    [InlineData(QueueListErrorCode.UnsupportedVersion)]
    public async Task InvalidSavedListDoesNotBecomeAnOwnershipFailure(QueueListErrorCode error)
    {
        var store = new BatchUiQueueListStore { ReadFailure = new(error) };
        var fixture = new BatchViewModelFixture(queueStore: store);
        await fixture.Main.InitializeAsync();
        Assert.True(fixture.Batch.Recovery.HasFailure);
        Assert.False(fixture.Main.PersistenceFailed);
        Assert.False(fixture.Batch.WritesBlocked);
        Assert.True(fixture.Batch.AddFilesCommand.CanExecute(null));
        await fixture.PrepareAsync();
        Assert.True(fixture.Batch.CanRun);
        Assert.Equal(0, store.Writes);
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>A failed checkpoint does not claim restart recovery or erase the prior list.</summary>
    [AvaloniaFact]
    public async Task FailedWritePreservesListAndAllowsSafeManualWork()
    {
        var store = new BatchUiQueueListStore { WriteFailure = new(QueueListErrorCode.PersistenceUnavailable, "controlled write failure") };
        var fixture = new BatchViewModelFixture(queueStore: store);
        await fixture.PrepareAsync();
        await fixture.Batch.Recovery.WhenIdleAsync();
        Assert.True(fixture.Batch.Recovery.HasFailure);
        Assert.Contains("controlled write failure", fixture.Batch.Recovery.Diagnostics);
        Assert.Empty(store.List.Entries);
        Assert.True(fixture.Batch.CanRun);
        Assert.False(fixture.Main.PersistenceFailed);
        fixture.Batch.NewBatchCommand.Execute(null);
        fixture.Batch.ConfirmNewBatchCommand.Execute(null);
        Assert.Empty(fixture.Batch.Items);
        Assert.True(fixture.Batch.Recovery.HasFailure);
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>A retained asynchronous old checkpoint cannot repopulate the saved list after New batch.</summary>
    [AvaloniaFact]
    public async Task DelayedCheckpointIsFollowedByTheNewEmptyList()
    {
        var entered = BatchViewModelFixture.Signal();
        var release = BatchViewModelFixture.Signal();
        var fixture = new BatchViewModelFixture();
        fixture.QueueStore.OnWrite = async () => { entered.TrySetResult(); await release.Task; };
        await fixture.LoadAsync();
        await BatchViewModelFixture.Await(entered.Task);
        Assert.True(fixture.Main.HasActiveOperation);
        fixture.Batch.NewBatchCommand.Execute(null);
        fixture.Batch.ConfirmNewBatchCommand.Execute(null);
        Assert.Empty(fixture.Batch.Items);
        release.TrySetResult();
        await fixture.Batch.Recovery.WhenIdleAsync();
        Assert.Empty(fixture.QueueStore.List.Entries);
        Assert.False(fixture.Main.HasActiveOperation);
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>A delayed startup read cannot insert an old list after an Application revision change.</summary>
    [AvaloniaFact]
    public async Task DelayedReadCannotReplaceANewerSession()
    {
        var entered = BatchViewModelFixture.Signal();
        var release = BatchViewModelFixture.Signal();
        var store = SavedStore();
        store.OnRead = async () => { entered.TrySetResult(); await release.Task; };
        var fixture = new BatchViewModelFixture(queueStore: store, initializeQueue: false);
        var reading = fixture.Batch.Recovery.InitializeAsync();
        await BatchViewModelFixture.Await(entered.Task);
        Assert.False(fixture.Batch.NewBatchCommand.CanExecute(null));
        Assert.True(fixture.Coordinator.StartNewBatch(fixture.Session.Snapshot.Revision).Applied);
        release.TrySetResult();
        await reading;
        Assert.Empty(fixture.Batch.Items);
        Assert.False(fixture.Batch.Recovery.HasRecoveryChoice);
        Assert.True(fixture.Batch.Recovery.HasFailure);
        Assert.Equal(2, store.List.Entries.Length);
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>Compiled Restore/Discard controls translate without changing the recoverable list or media metadata.</summary>
    [AvaloniaFact]
    public async Task RecoveryBindingsAndWorkspaceGatesSurviveLanguageChanges()
    {
        var fixture = new BatchViewModelFixture(queueStore: SavedStore());
        var view = new QueueRecoveryView { DataContext = fixture.Batch.Recovery };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            var original = fixture.Batch.Recovery.SavedEntriesText;
            var restore = view.FindControl<Button>("RestoreSavedQueueButton")!;
            var discard = view.FindControl<Button>("DiscardSavedQueueButton")!;
            Assert.Equal("Restore saved list", restore.Content);
            fixture.Main.IsBusy = true;
            Assert.False(restore.IsEffectivelyEnabled);
            Assert.False(discard.IsEffectivelyEnabled);
            await fixture.Batch.Recovery.RestoreCommand.ExecuteAsync(null);
            Assert.Empty(fixture.Batch.Items);
            fixture.Main.IsBusy = false;
            await fixture.Main.ChangeLanguageCommand.ExecuteAsync("ru");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Восстановить список", restore.Content);
            Assert.Equal("Отклонить сохранённый список", discard.Content);
            Assert.True(fixture.Batch.Recovery.HasRecoveryChoice);
            await fixture.Main.ChangeLanguageCommand.ExecuteAsync("en");
            Assert.Equal(original, fixture.Batch.Recovery.SavedEntriesText);
            Assert.Empty(fixture.Batch.Items);
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>An invalid destination draft removes old approval but cannot overwrite the last valid saved intent.</summary>
    [AvaloniaFact]
    public async Task InvalidDraftIsVisibleAndCanBeCorrectedWithoutLosingTheManualWorkflow()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.PrepareAsync();
        await fixture.Batch.Recovery.WhenIdleAsync();
        var item = fixture.Batch.Items[0];
        var valid = item.OutputPath;
        var saved = fixture.QueueStore.List;
        item.OutputPath = "relative draft";
        await fixture.Batch.Recovery.WhenIdleAsync();
        Assert.Null(item.ApprovedPlan);
        Assert.False(fixture.Batch.HasReview);
        Assert.True(fixture.Batch.Recovery.HasFailure);
        Assert.Same(saved, fixture.QueueStore.List);
        item.OutputPath = valid;
        await fixture.Batch.Recovery.WhenIdleAsync();
        Assert.False(fixture.Batch.Recovery.HasFailure);
        Assert.True(fixture.Batch.CanEdit);
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>Builds only path/provenance listing data from the established independently inspected UI fixture.</summary>
    private static BatchUiQueueListStore SavedStore()
    {
        var sources = new BatchUiBackend().Inspections.Keys;
        var root = Path.Combine(Path.GetTempPath(), "MovieSift restored output Кириллица");
        return new()
        {
            Revision = Guid.NewGuid(),
            List = new(sources.Select(source => new QueueListEntry(source,
                Path.Combine(root, Path.GetFileName(source)), true,
                new(Path.GetDirectoryName(source)!, Path.GetFileName(source)), root)).ToImmutableArray())
        };
    }
}
