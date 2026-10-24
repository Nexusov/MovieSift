using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrackTrim.Application;
using TrackTrim.Application.Batches;
using TrackTrim.Application.Jobs;
using TrackTrim.Core.Failures;

namespace TrackTrim.Desktop.ViewModels;

/// <summary>Presents a bounded in-memory queue while Application owns every batch transition.</summary>
public sealed partial class BatchViewModel : ObservableObject
{
    private readonly BatchViewModelServices services;
    private readonly Dictionary<Guid, BatchItemViewModel> editors = [];
    private ImmutableArray<BatchItemFailure> reviewFailures = [];
    private CancellationTokenSource? activeCancellation;
    private Task activeOperation = Task.CompletedTask;
    private Failure? failure;
    private long operationRevision;
    private string outputFolder = string.Empty;
    private bool includeSubfolders;
    private bool externalOperationActive;
    private bool writesBlocked;
    private bool warningCloseRequested;
    private Guid? lastProgressItem;
    private long? newBatchConfirmationRevision;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private BatchItemViewModel? selectedItem;

    [ObservableProperty]
    private double progressValue;

    [ObservableProperty]
    private bool isProgressIndeterminate;

    private JobProgress? activeProgress;

    /// <summary>Connects one session to native input and stable per-item presentation.</summary>
    public BatchViewModel(BatchViewModelServices services)
    {
        this.services = services;
        Snapshot = services.Session.Snapshot;
        LanguageRules = new(new(services.Session, services.Coordinator, services.Localization, () => CanEdit, OnEditorChanged));
        Presets = new(new(services.Presets, LanguageRules, services.Session, services.Localization,
            () => IsWorkspaceAvailable && !HasNewBatchConfirmation, () => !writesBlocked, Refresh));
        writesBlocked = !services.DataWritable;
        Recovery = new(new(services.Session, services.Coordinator, services.QueueLists, Localization,
            () => IsWorkspaceAvailable && !HasNewBatchConfirmation && !Presets.IsBusy, services.DataWritable, Refresh, services.Log));
        AggregateTracks = new(new(services.Session, Localization, () => CanEdit, OnAggregateSelectionEdited));
        AddFilesCommand = new AsyncRelayCommand(AddFilesAsync, () => CanEdit);
        AddFolderCommand = new AsyncRelayCommand(AddFolderAsync, () => CanEdit);
        ChooseOutputCommand = new AsyncRelayCommand(ChooseOutputAsync, () => CanEdit);
        InspectPendingCommand = new AsyncRelayCommand(InspectPendingAsync, () => CanEdit && Items.Any(item => item.Snapshot.State == BatchItemState.PendingInspection));
        InspectSelectedCommand = new AsyncRelayCommand(InspectSelectedAsync, () => CanInspectSelected);
        RemoveSelectedCommand = new RelayCommand(RemoveSelected, () => CanRemoveSelected);
        ApproveSelectedCommand = new RelayCommand(ApproveSelected, () => SelectedItem?.CanEditPlan == true);
        CloseReviewCommand = new RelayCommand(CloseReview);
        OpenSelectedDetailsCommand = new RelayCommand(() => IsDetailsOpen = true, () => SelectedItem is not null);
        ReviewCommand = new AsyncRelayCommand(ReviewAsync, () => CanEdit && Items.Count != 0);
        RunCommand = new AsyncRelayCommand(RunAsync, () => CanRun);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        PauseCommand = new RelayCommand(services.Coordinator.Pause, () => Snapshot.Phase == BatchPhase.Running && !Snapshot.PauseRequested);
        PublishWarningsCommand = new AsyncRelayCommand(PublishWarningsAsync, () => HasWarningCandidate && !IsBusy && !externalOperationActive);
        DiscardWarningsCommand = new AsyncRelayCommand(DiscardWarningsAsync, () => HasWarningCandidate && !IsBusy && !externalOperationActive);
        NewBatchCommand = new RelayCommand(RequestNewBatch, () => CanEdit);
        ConfirmNewBatchCommand = new RelayCommand(ConfirmNewBatch, () => HasNewBatchConfirmation && IsWorkspaceAvailable);
        KeepCurrentBatchCommand = new RelayCommand(KeepCurrentBatch, () => HasNewBatchConfirmation);
        services.Session.Changed += OnSessionChanged;
        services.Localization.PropertyChanged += (_, _) => Refresh();
        Synchronize();
    }

    public Localization.LocalizationService Localization => services.Localization;
    public ObservableCollection<BatchItemViewModel> Items { get; } = [];
    public ObservableCollection<BatchReviewRowViewModel> ReviewRows { get; } = [];
    public BatchLanguageRulesViewModel LanguageRules { get; }
    public RulePresetsViewModel Presets { get; }
    public QueueRecoveryViewModel Recovery { get; }
    public AggregateTracksViewModel AggregateTracks { get; }
    public BatchSnapshot Snapshot { get; private set; }
    public string OutputFolder => outputFolder;
    public bool IncludeSubfolders
    {
        get => includeSubfolders;
        set
        {
            if (!CanEdit)
            {
                OnPropertyChanged();
                return;
            }

            SetProperty(ref includeSubfolders, value);
        }
    }
    public bool CanEdit => IsWorkspaceAvailable && !HasNewBatchConfirmation && !Presets.IsBusy && Recovery.CanEditBatch;
    public bool CanRun => CanEdit && !writesBlocked && Snapshot.Review is not null;
    public bool CanRemoveSelected => CanEdit && SelectedItem is { Snapshot.Result: null };
    public bool CanInspectSelected => CanEdit && SelectedItem is { } selected && selected.Snapshot.State is not (BatchItemState.Completed or BatchItemState.NoChange);
    public bool HasSafetyBlock => Snapshot.HasSafetyBlock;
    public bool HasWarningCandidate => WarningOwner is not null;
    public bool HasBlockingOperation => IsBusy || Presets.IsBusy || Recovery.IsBusy || HasWarningCandidate || HasSafetyBlock || HasNewBatchConfirmation;
    public bool HasNewBatchConfirmation => newBatchConfirmationRevision is not null;
    public bool HasCleanupIssue => Snapshot.Items.Any(item => item.Result is { CleanupIssues.IsDefaultOrEmpty: false });
    public bool HasWarningCloseDecision => warningCloseRequested && HasWarningCandidate;
    public bool RequiresTerminationDecision => Snapshot.PauseReason == BatchPauseReason.UnsafeOwnership ||
        Snapshot.Items.Any(item => item.Result?.Failure?.Code is ErrorCode.ProcessTerminationFailed or ErrorCode.PublicationUncertain ||
            item.Failure?.Code is ErrorCode.ProcessTerminationFailed or ErrorCode.PublicationUncertain);
    public bool HasReview => Snapshot.Review is not null;
    public bool HasError => EffectiveFailure is { Code: not ErrorCode.Cancelled } || !reviewFailures.IsEmpty;
    public bool WritesBlocked => writesBlocked;
    public string ErrorText => EffectiveFailure is { } current ? Localization[$"Error{current.Code}"] : string.Empty;
    public string ErrorReasonText => EffectiveFailure is { Reason: not ErrorReason.None } current ? Localization[$"ErrorReason{current.Reason}"] : string.Empty;
    public string StatusText => EffectiveFailure?.Code == ErrorCode.Cancelled ? Localization["Cancelled"] : Localization[$"Batch{Snapshot.Phase}"];
    public string PauseText => Snapshot.Phase == BatchPhase.Running && Snapshot.PauseRequested ? Localization["BatchPauseRequestedPending"] :
        Snapshot.PauseReason == BatchPauseReason.None ? string.Empty : Localization[$"BatchPause{Snapshot.PauseReason}"];
    public string CurrentItemText => Snapshot.Items.FirstOrDefault(item => item.State is BatchItemState.Running or BatchItemState.Inspecting or BatchItemState.AwaitingWarningReview)?.SourcePath ?? string.Empty;
    public string StageText => activeProgress is { } progress ? Localization[PhaseKey(progress.Phase)] : string.Empty;
    public string SummaryText => Localization.Format(new("BatchSummary", [Items.Count, Items.Count(item => item.Included), Items.Count(item => item.Snapshot.State is BatchItemState.Completed or BatchItemState.NoChange)]));
    public string LimitsText => Localization.Format(new("BatchLimits", [BatchDiscoveryLimits.MaximumFiles, BatchDiscoveryLimits.MaximumExaminedEntries]));
    public IReadOnlyList<BatchItemViewModel> ZeroAudioItems => Items.Where(item => item.Included &&
        item.NeedsZeroAudioConfirmation && item.Snapshot.State is not (BatchItemState.Completed or BatchItemState.NoChange)).ToArray();
    public bool HasZeroAudioItems => ZeroAudioItems.Count != 0;
    public string ReviewText => string.Join(Environment.NewLine + Environment.NewLine, Snapshot.Review?.Items.Select(reviewed =>
        $"{reviewed.Plan.Inspection.Source.FullPath}{Environment.NewLine}{Localization["Output"]}: {reviewed.Plan.DestinationPath}{Environment.NewLine}" +
        $"{PlanPresentation.DescribeTracks(reviewed.Plan, Localization)}{Environment.NewLine}" +
        $"{PlanPresentation.DescribeDefaultAudio(reviewed.Plan, Localization)}{Environment.NewLine}{(reviewed.Plan.IsNoChange ? Localization["NoChange"] : string.Empty)}") ?? []);
    public string ExcludedText => string.Join(Environment.NewLine, Items.Where(item => !item.Included).Select(item => $"{item.SourcePath} — {item.StateText}"));
    public bool HasExcluded => Items.Any(item => !item.Included);
    public string ReviewIssuesText => string.Join(Environment.NewLine, reviewFailures.Select(issue =>
        $"{Snapshot.Items.FirstOrDefault(item => item.Id == issue.ItemId)?.SourcePath}: {Localization[$"Error{issue.Failure.Code}"]}"));
    public string Diagnostics => string.Join(Environment.NewLine, new[] { EffectiveFailure?.Detail }
        .Concat(Snapshot.Items.Select(item => item.Result is { CleanupIssues.IsDefaultOrEmpty: false } result
            ? string.Join(Environment.NewLine, result.CleanupIssues.Select(issue => $"{issue.Path}: {issue.Detail}")) : null))
        .Where(detail => !string.IsNullOrWhiteSpace(detail)));
    public bool HasDiagnostics => !string.IsNullOrWhiteSpace(Diagnostics);
    private Failure? EffectiveFailure => failure ?? Snapshot.Failure;
    private BatchItem? WarningOwner => Snapshot.Items.FirstOrDefault(item => item.State == BatchItemState.AwaitingWarningReview && item.Result?.Phase == JobPhase.AwaitingWarningReview);
    private bool IsWorkspaceAvailable => !IsBusy && !externalOperationActive && !HasWarningCandidate && !HasSafetyBlock &&
        Snapshot.Phase is not (BatchPhase.Inspecting or BatchPhase.Reviewing or BatchPhase.Running);

    public IAsyncRelayCommand AddFilesCommand { get; }
    public IAsyncRelayCommand AddFolderCommand { get; }
    public IAsyncRelayCommand ChooseOutputCommand { get; }
    public IAsyncRelayCommand InspectPendingCommand { get; }
    public IAsyncRelayCommand InspectSelectedCommand { get; }
    public IRelayCommand RemoveSelectedCommand { get; }
    public IRelayCommand ApproveSelectedCommand { get; }
    public IAsyncRelayCommand ReviewCommand { get; }
    public IAsyncRelayCommand RunCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand PauseCommand { get; }
    public IAsyncRelayCommand PublishWarningsCommand { get; }
    public IAsyncRelayCommand DiscardWarningsCommand { get; }
    public IRelayCommand NewBatchCommand { get; }
    public IRelayCommand ConfirmNewBatchCommand { get; }
    public IRelayCommand KeepCurrentBatchCommand { get; }
    public IRelayCommand CloseReviewCommand { get; }
    public IRelayCommand OpenSelectedDetailsCommand { get; }

    /// <summary>Requests explicit consent before clearing any existing list, plans or results.</summary>
    private void RequestNewBatch()
    {
        if (!CanEdit)
        {
            return;
        }

        if (Snapshot.Items.IsEmpty)
        {
            StartNewBatch(Snapshot.Revision);
            return;
        }

        newBatchConfirmationRevision = Snapshot.Revision;
        Refresh();
    }

    /// <summary>Clears only the exact reviewed session to which the user consented.</summary>
    private void ConfirmNewBatch()
    {
        var workspaceAvailable = IsWorkspaceAvailable && !Presets.IsBusy;

        if (newBatchConfirmationRevision is not { } revision || !workspaceAvailable)
        {
            return;
        }

        StartNewBatch(revision);
    }

    /// <summary>Declines a reset without clearing errors, progress, selections or approvals.</summary>
    private void KeepCurrentBatch()
    {
        newBatchConfirmationRevision = null;
        Refresh();
    }

    /// <summary>Resets presentation only after Application accepts an ownership-safe atomic reset.</summary>
    private void StartNewBatch(long revision)
    {
        var reset = services.Coordinator.StartNewBatch(revision);
        newBatchConfirmationRevision = null;

        if (!reset.Applied)
        {
            failure = reset.Failure;
            Refresh();
            return;
        }

        operationRevision++;
        IsReviewOpen = false;
        IsDetailsOpen = false;
        IsRulesOpen = false;
        LanguageRules.ClearPreview();
        failure = null;
        reviewFailures = [];
        activeProgress = null;
        lastProgressItem = null;
        warningCloseRequested = false;
        ProgressValue = 0;
        IsProgressIndeterminate = false;
        Synchronize();
    }

    /// <summary>Shares only workspace ownership availability, never another item's media state.</summary>
    public void SetWorkspaceAccess(BatchWorkspaceAccess access)
    {
        if (externalOperationActive == access.ExternalOperationActive && writesBlocked == access.WritesBlocked)
        {
            return;
        }

        externalOperationActive = access.ExternalOperationActive;
        writesBlocked = access.WritesBlocked;
        Refresh();
    }

    /// <summary>Cancels and awaits the same active operation before window shutdown continues.</summary>
    public async Task ShutdownAsync()
    {
        Cancel();
        await activeOperation;
        await Presets.WhenIdleAsync();
        await Recovery.WhenIdleAsync();
    }

    /// <summary>Asks about the frozen warning owner even when another row or tab is selected.</summary>
    public void RequestWarningCloseReview()
    {
        warningCloseRequested = HasWarningCandidate;
        Refresh();
    }

    /// <summary>Keeps the same warning candidate without starting remaining files.</summary>
    public void RemainInWarningReview()
    {
        warningCloseRequested = false;
        Refresh();
    }

    /// <summary>Closes only after the one owned warning candidate has been safely discarded.</summary>
    public async Task<bool> DiscardWarningsForCloseAsync()
    {
        if (!HasWarningCandidate || IsBusy)
        {
            return false;
        }

        await DiscardWarningsCommand.ExecuteAsync(null);
        return !HasWarningCandidate && !HasCleanupIssue && !HasSafetyBlock && !HasError;
    }

    /// <summary>Refreshes command predicates when the editor selection changes.</summary>
    partial void OnSelectedItemChanged(BatchItemViewModel? value) => Refresh();

    /// <summary>Updates edit and cancellation availability without changing per-file approvals.</summary>
    partial void OnIsBusyChanged(bool value) => Refresh();

    /// <summary>Adds explicit files without changing existing state when the picker is cancelled.</summary>
    private Task AddFilesAsync() => ExecuteOwnedAsync(async cancellationToken =>
    {
        var paths = await services.Dialogs.SelectSourcesAsync();

        if (paths.Count == 0)
        {
            return;
        }

        await DiscoverAsync(new(BatchInputKind.ExplicitFiles, paths, cancellationToken));
    });

    /// <summary>Captures the folder mode for this addition without reinterpreting previously accepted rows.</summary>
    private Task AddFolderAsync() => ExecuteOwnedAsync(async cancellationToken =>
    {
        if (IncludeSubfolders && string.IsNullOrWhiteSpace(outputFolder))
        {
            preparationNoticeKey = "RecursiveOutputGuide";
            Refresh();
            var selectedOutput = await services.Dialogs.SelectOutputFolderAsync();
            cancellationToken.ThrowIfCancellationRequested();

            if (selectedOutput is null)
            {
                return;
            }

            ApplyOutputFolder(selectedOutput);
        }

        var kind = IncludeSubfolders ? BatchInputKind.RecursiveFolder : BatchInputKind.TopLevelFolder;
        var outputRoot = IncludeSubfolders ? outputFolder : null;
        var folder = await services.Dialogs.SelectInputFolderAsync();

        if (folder is null)
        {
            return;
        }

        await DiscoverAsync(new(kind, [folder], cancellationToken, outputRoot));
    });

    /// <summary>Accepts a bounded addition, then probes its real files serially by stable item ID.</summary>
    private async Task DiscoverAsync(BatchInputDiscoveryRequest request)
    {
        var discovered = await services.Discovery.DiscoverAsync(request);
        request.CancellationToken.ThrowIfCancellationRequested();
        var edit = services.Session.AddDiscoveryResult(discovered);
        failure = edit.Failure;

        if (!edit.Applied)
        {
            return;
        }

        if (!string.IsNullOrEmpty(outputFolder))
        {
            foreach (var editor in Items.Where(editor => editor.Snapshot.IntendedDestinationPath is null))
            {
                editor.ApplyOutputFolder(outputFolder);
            }
        }

        if (services.Session.Snapshot.Items.Any(item => item.State == BatchItemState.PendingInspection && item.Included))
        {
            await services.Coordinator.InspectPendingAsync(request.CancellationToken);
        }
    }

    /// <summary>Changes every affected destination and invalidates their existing approvals.</summary>
    private Task ChooseOutputAsync() => ExecuteOwnedAsync(async cancellationToken =>
    {
        var folder = await services.Dialogs.SelectOutputFolderAsync();
        cancellationToken.ThrowIfCancellationRequested();

        if (folder is null)
        {
            return;
        }

        ApplyOutputFolder(folder);
    });

    /// <summary>Continues explicitly requested inspection after a cancelled preparation.</summary>
    private Task InspectPendingAsync() => ExecuteOwnedAsync(async cancellationToken =>
        await services.Coordinator.InspectPendingAsync(cancellationToken));

    /// <summary>Requires a fresh per-file inspection and approval after a corrected source.</summary>
    private Task InspectSelectedAsync()
    {
        var itemId = SelectedItem?.Id;
        return ExecuteOwnedAsync(async cancellationToken =>
        {
            if (itemId is { } identifier)
            {
                await services.Coordinator.InspectItemAsync(new(identifier, cancellationToken));
            }
        });
    }

    /// <summary>Removes an unstarted row instead of silently omitting a failed inspection.</summary>
    private void RemoveSelected()
    {
        if (SelectedItem is not { } selected)
        {
            return;
        }

        var removal = services.Session.Remove(selected.Id);

        if (!removal.Applied)
        {
            failure = removal.Failure;
            Refresh();
            return;
        }

        OnEditorChanged();
    }

    /// <summary>Approves only the selected editor's immutable retention plan.</summary>
    private void ApproveSelected() => SelectedItem?.Approve();

    /// <summary>Captures displayed consent and creates every independent plan in one explicit Review.</summary>
    private async Task ReviewAsync()
    {
        if (!CanEdit)
        {
            return;
        }

        IsRulesOpen = false;
        var request = new BatchSelectionReviewRequest(Snapshot.Revision,
            Items.Where(item => item.Included && item.ZeroAudioConfirmed).Select(item => item.Id).ToImmutableArray());
        await ExecuteOwnedAsync(async cancellationToken =>
        {
            var review = await services.Coordinator.ReviewSelectionsAsync(request with { CancellationToken = cancellationToken });
            reviewFailures = review.ItemFailures;
            failure = review.Failure;
        });
        IsReviewOpen = true;
        Refresh();
    }

    /// <summary>Runs one reviewed queue and rejects callbacks from another attempt, item or plan.</summary>
    private Task RunAsync()
    {
        if (!CanRun)
        {
            return Task.CompletedTask;
        }

        var review = Snapshot.Review;
        IsReviewOpen = false;
        return ExecuteOwnedAsync(async cancellationToken =>
        {
            if (review is null)
            {
                return;
            }

            var revision = operationRevision;
            Recovery.Checkpoint();
            await Recovery.WhenIdleAsync();
            var progress = new Progress<BatchProgress>(update => UpdateProgress(update, revision));
            var result = await services.Coordinator.RunAsync(new(review.Id, progress, cancellationToken));
            failure = result.Failure;
        });
    }

    /// <summary>Requests cancellation once; Application prevents dispatch of the next file.</summary>
    private void Cancel()
    {
        services.Coordinator.Cancel();
        activeCancellation?.Cancel();
    }

    /// <summary>Publishes the frozen warning candidate without implicitly resuming the queue.</summary>
    private Task PublishWarningsAsync() => ResolveWarningAsync(true);

    /// <summary>Discards the frozen warning candidate through the existing job owner.</summary>
    private Task DiscardWarningsAsync() => ResolveWarningAsync(false);

    /// <summary>Captures the warning's actual item ID before asynchronous review work begins.</summary>
    private Task ResolveWarningAsync(bool publish)
    {
        var owner = WarningOwner;
        return ExecuteOwnedAsync(async cancellationToken =>
        {
            if (owner is null)
            {
                return;
            }

            var request = new BatchWarningRequest(owner.Id, cancellationToken);
            var result = publish ? await services.Coordinator.PublishWarningsAsync(request) : await services.Coordinator.DiscardWarningsAsync(request);
            failure = result.Failure;
            warningCloseRequested = HasWarningCandidate && warningCloseRequested;
        });
    }

    /// <summary>Retains one awaitable command owner, including dialogs and preparation cancellation.</summary>
    private async Task ExecuteOwnedAsync(Func<CancellationToken, Task> operation)
    {
        if (IsBusy || externalOperationActive)
        {
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        activeOperation = completion.Task;
        activeCancellation = new();
        operationRevision++;
        failure = null;
        reviewFailures = [];
        preparationNoticeKey = string.Empty;
        activeProgress = null;
        IsProgressIndeterminate = true;
        ProgressValue = 0;
        IsBusy = true;

        try
        {
            await operation(activeCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            failure = new(ErrorCode.Cancelled);
        }
        catch (OperationFailureException exception)
        {
            failure = exception.Failure;
        }
        catch (Exception exception)
        {
            services.Log.Error(exception, "A batch desktop operation failed");
            failure = new(ErrorCode.IoFailure, exception.Message);
        }
        finally
        {
            activeCancellation.Dispose();
            activeCancellation = null;
            IsBusy = false;
            IsProgressIndeterminate = false;
            Synchronize();
            completion.TrySetResult();
        }
    }

    /// <summary>Uses live ownership to reject delayed progress from a finished file or earlier run.</summary>
    private void UpdateProgress(BatchProgress update, long revision)
    {
        var owner = services.Session.Snapshot.Items.FirstOrDefault(item => item.Id == update.ItemId);
        var isCurrent = IsBusy && operationRevision == revision && owner?.State == BatchItemState.Running && owner.ApprovedPlan?.Id == update.PlanId;

        if (!isCurrent)
        {
            return;
        }

        lastProgressItem = update.ItemId;
        activeProgress = update.Job;
        ProgressValue = update.Job.Percent ?? 0;
        IsProgressIndeterminate = update.Job.Percent is null;
        Refresh();
    }

    /// <summary>Marshals immutable Application notifications without retaining a stale selected-row target.</summary>
    private void OnSessionChanged(BatchSnapshot snapshot)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Synchronize();
            return;
        }

        Dispatcher.UIThread.Post(Synchronize);
    }

    /// <summary>Updates stable editor instances by ID and always reads the latest session snapshot.</summary>
    private void Synchronize()
    {
        Snapshot = services.Session.Snapshot;
        var liveIds = Snapshot.Items.Select(item => item.Id).ToHashSet();

        foreach (var removed in Items.Where(item => !liveIds.Contains(item.Id)).ToArray())
        {
            Items.Remove(removed);
            editors.Remove(removed.Id);
        }

        foreach (var item in Snapshot.Items)
        {
            if (!editors.TryGetValue(item.Id, out var editor))
            {
                editor = new(new(item, services.Session, Localization, outputFolder,
                    () => CanEditItem(item.Id), () => OnItemEdited(item.Id)));
                editors.Add(item.Id, editor);
                Items.Add(editor);
            }

            editor.ApplySnapshot(item);
        }

        var hasSelectedItem = SelectedItem is not null && liveIds.Contains(SelectedItem.Id);

        if (!hasSelectedItem)
        {
            SelectedItem = Items.FirstOrDefault();
        }

        if (lastProgressItem is { } identifier && !Snapshot.Items.Any(item => item.Id == identifier && item.State == BatchItemState.Running))
        {
            activeProgress = null;
        }

        Refresh();
        Recovery.Checkpoint();
    }

    /// <summary>Clears obsolete aggregate validation messages after a deliberate local edit.</summary>
    private void OnEditorChanged()
    {
        reviewFailures = [];
        failure = null;
        Refresh();
    }

    /// <summary>Reports a rejected stale aggregate action without hiding the current per-file evidence.</summary>
    private void OnAggregateSelectionEdited(Failure? editFailure)
    {
        if (editFailure is not null)
        {
            failure = editFailure;
            Refresh();
            return;
        }

        OnEditorChanged();
    }

    /// <summary>Rejects callbacks from editors whose item identity belongs to a previous list.</summary>
    private bool CanEditItem(Guid itemId) => CanEdit && services.Session.Snapshot.Items.Any(item => item.Id == itemId);

    /// <summary>Refreshes diagnostics only for an editor that still belongs to the current list.</summary>
    private void OnItemEdited(Guid itemId)
    {
        if (!services.Session.Snapshot.Items.Any(item => item.Id == itemId))
        {
            return;
        }

        OnEditorChanged();
    }

    /// <summary>Localizes all existing editors and updates queue commands without changing selections.</summary>
    private void Refresh()
    {
        foreach (var item in Items)
        {
            item.Refresh();
        }

        RefreshReviewRows();
        OnPropertyChanged(string.Empty);
        AggregateTracks?.Refresh();
        LanguageRules?.Refresh();
        Presets?.Refresh();
        Recovery?.Refresh();
        AddFilesCommand?.NotifyCanExecuteChanged();
        AddFolderCommand?.NotifyCanExecuteChanged();
        ChooseOutputCommand?.NotifyCanExecuteChanged();
        InspectPendingCommand?.NotifyCanExecuteChanged();
        InspectSelectedCommand?.NotifyCanExecuteChanged();
        RemoveSelectedCommand?.NotifyCanExecuteChanged();
        ApproveSelectedCommand?.NotifyCanExecuteChanged();
        ReviewCommand?.NotifyCanExecuteChanged();
        RunCommand?.NotifyCanExecuteChanged();
        CancelCommand?.NotifyCanExecuteChanged();
        PauseCommand?.NotifyCanExecuteChanged();
        PublishWarningsCommand?.NotifyCanExecuteChanged();
        DiscardWarningsCommand?.NotifyCanExecuteChanged();
        NewBatchCommand?.NotifyCanExecuteChanged();
        ConfirmNewBatchCommand?.NotifyCanExecuteChanged();
        KeepCurrentBatchCommand?.NotifyCanExecuteChanged();
        OpenSelectedDetailsCommand?.NotifyCanExecuteChanged();
    }

    /// <summary>Maps the existing single-job progress vocabulary to shared resources.</summary>
    private static string PhaseKey(JobPhase phase) => phase switch
    {
        JobPhase.Validating => "Checking",
        JobPhase.Completed => "Finishing",
        JobPhase.AwaitingWarningReview => "WarningReview",
        _ => phase.ToString()
    };
}
