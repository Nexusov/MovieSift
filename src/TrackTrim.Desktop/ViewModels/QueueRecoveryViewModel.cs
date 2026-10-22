using System.Collections.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrackTrim.Application.QueueLists;
using TrackTrim.Desktop.Localization;

namespace TrackTrim.Desktop.ViewModels;

/// <summary>Offers explicit list recovery and awaits serialized metadata checkpoints, independently of media ownership.</summary>
public sealed class QueueRecoveryViewModel : ObservableObject
{
    private readonly QueueRecoveryOptions options;
    private QueueList? recoverable;
    private QueueList? persisted;
    private QueueList? pending;
    private Guid? storedRevision;
    private QueueListFailure? failure;
    private ImmutableArray<QueueListFailure> cleanupIssues = [];
    private Task saving = Task.CompletedTask;
    private Task catalogOperation = Task.CompletedTask;
    private bool initialized;
    private bool canWrite;
    private bool invalidDraft;
    private bool isSaving;

    /// <summary>Connects the narrow listing port without loading, inspecting or starting anything.</summary>
    public QueueRecoveryViewModel(QueueRecoveryOptions options)
    {
        this.options = options;
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, () => CanManage && HasRecoveryChoice && options.Session.Snapshot.Items.IsEmpty);
        DiscardCommand = new AsyncRelayCommand(DiscardAsync, () => CanManage && HasRecoveryChoice && options.DataWritable);
        ReloadCommand = new AsyncRelayCommand(ReloadAsync, () => CanManage && options.Session.Snapshot.Items.IsEmpty);
    }

    public LocalizationService Localization => options.Localization;
    public bool IsBusy { get; private set; }
    public bool HasPendingWork => IsBusy || isSaving;
    public bool HasRecoveryChoice => recoverable is { Entries.IsEmpty: false };
    public bool HasNotice => HasRecoveryChoice || HasFailure || IsBusy;
    public bool CanEditBatch => initialized && !IsBusy && !HasRecoveryChoice;
    public bool HasFailure => failure is not null || !cleanupIssues.IsEmpty;
    public string ErrorText => invalidDraft ? Localization["QueueCurrentListInvalid"] : failure is { } current
        ? Localization[$"QueueError{current.Code}"] : !cleanupIssues.IsEmpty ? Localization["QueueCleanupIssue"] : string.Empty;
    public string Diagnostics => string.Join(Environment.NewLine, new[] { failure?.Detail }.Concat(cleanupIssues.Select(issue => issue.Detail))
        .Where(detail => !string.IsNullOrWhiteSpace(detail)));
    public string SavedEntriesText => string.Join(Environment.NewLine, recoverable?.Entries.Select(entry =>
        $"{entry.SourcePath}{Environment.NewLine}{Localization["Output"]}: {entry.DestinationPath ?? Localization["QueueDestinationNeeded"]}" +
        (entry.Included ? string.Empty : $" — {Localization["QueueExcluded"]}")) ?? []);
    public string StatusText => Localization[!initialized ? "QueueLoading" : HasRecoveryChoice ? "QueueRecoveryAvailable" :
        HasFailure ? "QueueNotSaved" : HasPendingWork ? "QueueSaving" : "QueueCheckpointReady"];
    private bool CanManage => !IsBusy && options.CanManage();

    public IAsyncRelayCommand RestoreCommand { get; }
    public IAsyncRelayCommand DiscardCommand { get; }
    public IAsyncRelayCommand ReloadCommand { get; }

    /// <summary>Reads once at startup and exposes a choice without populating the live batch.</summary>
    public Task InitializeAsync() => initialized ? Task.CompletedTask : IsBusy ? catalogOperation : ReadAsync();

    /// <summary>Rereads an explicitly corrected record only while the live list is empty.</summary>
    private Task ReloadAsync() => CanManage && options.Session.Snapshot.Items.IsEmpty ? ReadAsync() : Task.CompletedTask;

    /// <summary>Rejects a delayed read after another operation has changed the current session.</summary>
    private Task ReadAsync() => ExecuteCatalogAsync(async () =>
    {
        var sessionRevision = options.Session.Snapshot.Revision;
        var result = await options.Store.ReadAsync();
        initialized = true;

        if (sessionRevision != options.Session.Snapshot.Revision)
        {
            failure = new(QueueListErrorCode.Conflict, "The current queue changed while its saved list was being read.");
            canWrite = false;
            return;
        }

        AcceptResult(result);
        recoverable = result.Failure is null && result.List is { Entries.IsEmpty: false } ? result.List : null;
        canWrite = result.Failure is null && options.DataWritable;

        if (result.Failure is null && !options.DataWritable)
        {
            failure = new(QueueListErrorCode.PersistenceUnavailable, "Portable queue storage is not writable.");
        }
    });

    /// <summary>Creates fresh uninspected identities through the existing ownership-gated Application operation.</summary>
    private Task RestoreAsync()
    {
        if (!CanManage || recoverable is not { } list || !options.Session.Snapshot.Items.IsEmpty)
        {
            return Task.CompletedTask;
        }

        var revision = options.Session.Snapshot.Revision;
        return ExecuteCatalogAsync(() =>
        {
            var restored = options.Coordinator.RestoreQueueList(list, revision);

            if (!restored.Applied)
            {
                failure = new(QueueListErrorCode.Conflict, restored.Failure?.ToString() ?? "The current workspace cannot restore this list.");
                return Task.CompletedTask;
            }

            recoverable = null;
            failure = null;
            return Task.CompletedTask;
        });
    }

    /// <summary>Discards listing data through a version-checked empty replacement; never deletes media or ownership records.</summary>
    private Task DiscardAsync()
    {
        if (!CanManage || !HasRecoveryChoice || !options.DataWritable)
        {
            return Task.CompletedTask;
        }

        return ExecuteCatalogAsync(async () =>
        {
            var result = await options.Store.WriteAsync(new(new([]), storedRevision));
            AcceptResult(result);

            if (result.Committed)
            {
                recoverable = null;
            }

            canWrite = result.Failure is null && result.CleanupIssues.IsDefaultOrEmpty;
        });
    }

    /// <summary>Coalesces current listing content without persisting inspections, selections, approvals or live processes.</summary>
    public void Checkpoint()
    {
        var available = initialized && canWrite && !IsBusy && !HasRecoveryChoice;

        if (!available)
        {
            return;
        }

        pending = options.Session.CaptureQueueList();

        if (!isSaving)
        {
            isSaving = true;
            saving = SavePendingAsync();
        }

        Notify();
    }

    /// <summary>Serializes asynchronous saves so an old completion cannot replace a later reset or edit.</summary>
    private async Task SavePendingAsync()
    {
        try
        {
            while (pending is { } list)
            {
                pending = null;
                var validation = QueueListPolicy.Validate(list);

                if (validation is not null)
                {
                    invalidDraft = true;
                    failure = validation;
                    continue;
                }

                if (QueueListPolicy.ContentEquals(list, persisted))
                {
                    invalidDraft = false;
                    failure = null;
                    continue;
                }

                var result = await options.Store.WriteAsync(new(list, storedRevision));
                AcceptResult(result);

                if (result.Failure is not null || !result.CleanupIssues.IsDefaultOrEmpty)
                {
                    canWrite = false;
                    pending = null;
                }
            }
        }
        catch (Exception exception)
        {
            options.Log.Error(exception, "Queue listing checkpoint failed");
            failure = new(QueueListErrorCode.PersistenceUnavailable, exception.Message);
            canWrite = false;
            pending = null;
        }
        finally
        {
            isSaving = false;
            Notify();
        }
    }

    /// <summary>Awaits every owned listing operation before Run or normal shutdown proceeds.</summary>
    public async Task WhenIdleAsync()
    {
        await catalogOperation;

        while (true)
        {
            var current = saving;
            await current;

            if (ReferenceEquals(current, saving))
            {
                return;
            }
        }
    }

    /// <summary>Owns a bounded catalog operation without sharing media-job failure or cleanup authority.</summary>
    private async Task ExecuteCatalogAsync(Func<Task> operation)
    {
        if (IsBusy)
        {
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        catalogOperation = completion.Task;
        IsBusy = true;
        Notify();

        try
        {
            await saving;
            await operation();
        }
        catch (Exception exception)
        {
            options.Log.Error(exception, "Queue listing recovery failed");
            initialized = true;
            canWrite = false;
            failure = new(QueueListErrorCode.PersistenceUnavailable, exception.Message);
        }
        finally
        {
            IsBusy = false;
            completion.TrySetResult();
            Checkpoint();
            Notify();
        }
    }

    /// <summary>Preserves commit and cleanup facts separately from a failed storage attempt.</summary>
    private void AcceptResult(QueueListResult result)
    {
        failure = result.Failure;
        invalidDraft = false;
        cleanupIssues = result.CleanupIssues.IsDefault ? [] : result.CleanupIssues;

        if (result.Failure is null || result.Committed)
        {
            persisted = result.List;
            storedRevision = result.Revision;
        }
    }

    /// <summary>Refreshes localized presentation without changing saved or live list content.</summary>
    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
        RestoreCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
        ReloadCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Notifies the containing workspace after a persistence-owned state transition.</summary>
    private void Notify()
    {
        Refresh();
        options.Changed();
    }
}
