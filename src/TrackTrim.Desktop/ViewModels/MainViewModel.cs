using System.Collections.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrackTrim.Application;
using TrackTrim.Application.Ports;
using TrackTrim.Core.Failures;
using TrackTrim.Desktop.Appearance;
using TrackTrim.Desktop.Localization;

namespace TrackTrim.Desktop.ViewModels;

/// <summary>Owns portable preferences, interrupted ownership notices and the unified workspace lifetime.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly MainViewModelServices services;
    private ImmutableArray<InterruptedOutput> interrupted = [];
    private CancellationTokenSource? cleanupCancellation;
    private Task cleanupOperation = Task.CompletedTask;
    private Failure? cleanupFailure;
    private long languageRevision;
    private bool hadPendingMediaDecision;

    [ObservableProperty]
    private bool isAppearanceOpen;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string cleanupDetails = string.Empty;

    [ObservableProperty]
    private bool persistenceFailed;

    /// <summary>Connects shell-only actions while the batch coordinator remains the sole media owner.</summary>
    public MainViewModel(MainViewModelServices services)
    {
        this.services = services;
        PersistenceFailed = !services.DataWritable;
        ChangeLanguageCommand = new AsyncRelayCommand<string>(ChangeLanguageAsync);
        ToggleAppearanceCommand = new RelayCommand(() => IsAppearanceOpen = !IsAppearanceOpen);
        RemainInWarningReviewCommand = new RelayCommand(Batch.RemainInWarningReview, () => CanRespondToWarningClose);
        CleanInterruptedCommand = new AsyncRelayCommand(CleanInterruptedAsync, () => HasCleanableInterrupted && CanEdit);
        Localization.PropertyChanged += (_, _) => NotifyPresentation();
        Batch.PropertyChanged += (_, _) => NotifyPresentation();

        if (Appearance is not null)
        {
            Appearance.PropertyChanged += (_, _) => OnPropertyChanged(nameof(HasActiveOperation));
        }
    }

    public LocalizationService Localization => services.Localization;
    public BatchViewModel Batch => services.Batch;
    public AppearanceViewModel? Appearance => services.Appearance;
    public IRelayCommand ToggleAppearanceCommand { get; }
    public IAsyncRelayCommand<string> ChangeLanguageCommand { get; }
    public IRelayCommand RemainInWarningReviewCommand { get; }
    public IAsyncRelayCommand CleanInterruptedCommand { get; }
    public bool HasOwnedOperation => IsBusy || Batch.IsBusy || Batch.Presets.IsBusy || Batch.Recovery.HasPendingWork;
    public bool HasActiveOperation => HasOwnedOperation || Appearance?.HasPendingSave == true;
    public bool HasBlockingOperation => IsBusy || Batch.HasBlockingOperation || HasShellCleanupIssue;
    public bool CanEdit => !HasBlockingOperation;
    public bool RequiresCloseDecision => Batch.HasSafetyBlock || HasShellCleanupIssue;
    public bool HasPendingMediaDecision => HasWarningCandidate || RequiresCloseDecision;
    public string CloseDecisionText => Batch.Snapshot.Items.Any(item =>
        item.Failure?.Code == ErrorCode.PublicationUncertain || item.Result?.Failure?.Code == ErrorCode.PublicationUncertain)
        ? Localization["ErrorPublicationUncertain"] : Batch.RequiresTerminationDecision
            ? Localization["CloseUnconfirmedNotice"] : Localization["BatchCleanupCloseNotice"];
    public bool HasWarningCandidate => Batch.HasWarningCandidate;
    public string WarningCloseSource => Batch.CurrentItemText;
    public bool HasWarningCloseDecision => Batch.HasWarningCloseDecision;
    public bool CanRespondToWarningClose => HasWarningCloseDecision && !HasOwnedOperation;
    public bool HasInterrupted => !interrupted.IsDefaultOrEmpty;
    public bool HasCleanableInterrupted => interrupted.Any(entry => entry.CanClean && entry.Output is not null);
    public string InterruptedDetails => DescribeInterrupted();
    public bool HasCleanupIssue => HasShellCleanupIssue || Batch.HasCleanupIssue;
    public bool HasError => cleanupFailure is not null;
    public string ErrorText => cleanupFailure is { } failure ? Localization[$"Error{failure.Code}"] : string.Empty;
    public string Diagnostics => string.Join(Environment.NewLine, new[] { cleanupFailure?.Detail, CleanupDetails }
        .Where(detail => !string.IsNullOrWhiteSpace(detail)));
    public bool HasDiagnostics => !string.IsNullOrWhiteSpace(Diagnostics);
    private bool HasShellCleanupIssue => cleanupFailure is not null || !string.IsNullOrWhiteSpace(CleanupDetails);

    /// <summary>Restores only preferences and recovery choices without inspecting or processing sources.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            var initialRevision = languageRevision;
            var language = await services.Settings.LoadLanguageAsync();

            if (initialRevision == languageRevision)
            {
                Localization.SetLanguage(language);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationFailureException)
        {
            PersistenceFailed = true;
            services.Log.Warning(exception, "Portable language settings could not be read");
        }

        try
        {
            interrupted = await services.Outputs.FindInterruptedAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationFailureException)
        {
            PersistenceFailed = true;
            services.Log.Warning(exception, "Portable ownership records could not be read");
        }

        await Batch.Presets.InitializeAsync();
        await Batch.Recovery.InitializeAsync();

        if (Appearance is not null)
        {
            await Appearance.InitializeAsync();
        }
        NotifyPresentation();
    }

    /// <summary>Awaits the single media owner and any explicit interrupted-storage cleanup.</summary>
    public async Task ShutdownAsync()
    {
        var batchShutdown = Batch.ShutdownAsync();

        if (cleanupCancellation is not null)
        {
            await cleanupCancellation.CancelAsync();
        }

        await cleanupOperation;
        await batchShutdown;

        if (Appearance is not null)
        {
            await Appearance.WhenIdleAsync();
        }
    }

    /// <summary>Leaves the warning candidate with its existing batch owner until the user decides.</summary>
    public void RequestWarningCloseReview() => Batch.RequestWarningCloseReview();

    /// <summary>Awaits the existing explicit warning-discard lifecycle before allowing close.</summary>
    public async Task<bool> DiscardWarningsForCloseAsync()
    {
        var canClose = await Batch.DiscardWarningsForCloseAsync();

        if (canClose && Appearance is not null)
        {
            await Appearance.WhenIdleAsync();
        }

        return canClose;
    }

    /// <summary>Updates availability while shell-owned interrupted-storage cleanup is active.</summary>
    partial void OnIsBusyChanged(bool value) => NotifyPresentation();

    /// <summary>Propagates a storage failure to the unified workspace's write gate.</summary>
    partial void OnPersistenceFailedChanged(bool value) => NotifyPresentation();

    /// <summary>Updates translated presentation immediately and persists only the language code.</summary>
    private async Task ChangeLanguageAsync(string? language)
    {
        languageRevision++;
        Localization.SetLanguage(language);

        try
        {
            await services.Settings.SaveLanguageAsync(new(Localization.Language));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationFailureException)
        {
            PersistenceFailed = true;
            services.Log.Warning(exception, "The language preference could not be saved");
            NotifyPresentation();
        }
    }

    /// <summary>Cleans only validated interrupted ownership through the output-store boundary.</summary>
    private async Task CleanInterruptedAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cleanupOperation = completion.Task;
        cleanupCancellation = new CancellationTokenSource();
        IsBusy = true;
        cleanupFailure = null;
        CleanupDetails = string.Empty;

        try
        {
            var issues = ImmutableArray.CreateBuilder<CleanupIssue>();

            foreach (var entry in interrupted.Where(entry => entry.CanClean && entry.Output is not null))
            {
                issues.AddRange(await services.Outputs.CleanupAsync(new(entry.Output!, cleanupCancellation.Token)));
            }

            CleanupDetails = string.Join(Environment.NewLine, issues.Select(issue => $"{issue.Path}: {issue.Detail}"));
            interrupted = await services.Outputs.FindInterruptedAsync(cleanupCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Interrupted records remain discoverable; cancellation never authorizes deletion by the shell.
        }
        catch (OperationFailureException exception)
        {
            cleanupFailure = exception.Failure;
        }
        catch (Exception exception)
        {
            cleanupFailure = new(ErrorCode.IoFailure, exception.Message);
            services.Log.Error(exception, "Interrupted ownership cleanup failed");
        }
        finally
        {
            cleanupCancellation.Dispose();
            cleanupCancellation = null;
            IsBusy = false;
            NotifyPresentation();
            completion.TrySetResult();
        }
    }

    /// <summary>Distinguishes invalid records from cleanable or unresolved interrupted ownership.</summary>
    private string DescribeInterrupted() => string.Join(Environment.NewLine, interrupted.Select(entry =>
    {
        var state = entry.Stage == "InvalidRecord" ? "InterruptedInvalidRecord" : entry.CanClean ? "InterruptedCleanable" : "InterruptedUnresolved";
        var paths = entry.Output is { } output
            ? Localization.Format(new("InterruptedPaths", [output.CandidatePath, output.DestinationPath]))
            : entry.Issue ?? string.Empty;
        return $"{Localization[state]}{Environment.NewLine}{paths}";
    }));

    /// <summary>Shares only shell availability and persistence status with the sole media workspace.</summary>
    private void NotifyPresentation()
    {
        Batch.SetWorkspaceAccess(new(IsBusy || HasShellCleanupIssue, PersistenceFailed));

        var newlyPendingDecision = HasPendingMediaDecision && !hadPendingMediaDecision;
        hadPendingMediaDecision = HasPendingMediaDecision;

        if (newlyPendingDecision)
        {
            IsAppearanceOpen = false;
        }

        OnPropertyChanged(string.Empty);
        ToggleAppearanceCommand?.NotifyCanExecuteChanged();
        RemainInWarningReviewCommand?.NotifyCanExecuteChanged();
        CleanInterruptedCommand?.NotifyCanExecuteChanged();
    }
}
