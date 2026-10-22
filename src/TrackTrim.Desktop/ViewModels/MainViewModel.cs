using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrackTrim.Application;
using TrackTrim.Application.Jobs;
using TrackTrim.Application.Ports;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Media;
using TrackTrim.Core.Planning;
using TrackTrim.Desktop.Localization;

namespace TrackTrim.Desktop.ViewModels;

/// <summary>Presents one inspection, one immutable review, and one owned MKV job.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly MainViewModelServices services;
    private MediaInspection? inspection;
    private RemuxPlan? approvedPlan;
    private JobResult? result;
    private Failure? failure;
    private ImmutableArray<InterruptedOutput> interrupted = [];
    private CancellationTokenSource? activeCancellation;
    private Task activeOperation = Task.CompletedTask;
    private string statusKey = "Idle";
    private long languageRevision;
    private string toolDiagnostics = string.Empty;

    [ObservableProperty]
    private string sourcePath = string.Empty;

    [ObservableProperty]
    private string outputPath = string.Empty;

    [ObservableProperty]
    private bool zeroAudioConfirmed;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private double progressValue;

    [ObservableProperty]
    private bool isProgressIndeterminate;

    [ObservableProperty]
    private string cleanupDetails = string.Empty;

    [ObservableProperty]
    private bool persistenceFailed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarningCloseDecision))]
    private bool warningCloseRequested;

    /// <summary>Connects application use cases to commands without owning media I/O.</summary>
    public MainViewModel(MainViewModelServices services)
    {
        this.services = services;
        PersistenceFailed = !services.DataWritable;
        Localization.PropertyChanged += (_, _) => RefreshLocalization();
        OpenSourceCommand = new AsyncRelayCommand(OpenSourceAsync, () => CanEdit);
        ChooseOutputCommand = new AsyncRelayCommand(ChooseOutputAsync, () => CanEdit);
        ReviewPlanCommand = new RelayCommand(ReviewPlan, () => CanEdit);
        RunCommand = new AsyncRelayCommand(RunAsync, () => CanRun);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        ChangeLanguageCommand = new AsyncRelayCommand<string>(ChangeLanguageAsync);
        PublishWarningsCommand = new AsyncRelayCommand(PublishWarningsAsync, () => HasWarningCandidate && !IsBusy);
        DiscardWarningsCommand = new AsyncRelayCommand(DiscardWarningsAsync, () => HasWarningCandidate && !IsBusy);
        RemainInWarningReviewCommand = new RelayCommand(RemainInWarningReview, () => HasWarningCloseDecision && !IsBusy);
        CleanInterruptedCommand = new AsyncRelayCommand(CleanInterruptedAsync, () => HasCleanableInterrupted && CanEdit);
    }

    public LocalizationService Localization => services.Localization;
    public ObservableCollection<TrackViewModel> Tracks { get; } = [];
    public IAsyncRelayCommand OpenSourceCommand { get; }
    public IAsyncRelayCommand ChooseOutputCommand { get; }
    public IRelayCommand ReviewPlanCommand { get; }
    public IAsyncRelayCommand RunCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand<string> ChangeLanguageCommand { get; }
    public IAsyncRelayCommand PublishWarningsCommand { get; }
    public IAsyncRelayCommand DiscardWarningsCommand { get; }
    public IRelayCommand RemainInWarningReviewCommand { get; }
    public IAsyncRelayCommand CleanInterruptedCommand { get; }
    public bool HasBlockingOperation => IsBusy || HasWarningCandidate || RequiresCloseDecision;
    public bool CanEdit => !HasBlockingOperation;
    public bool CanRun => approvedPlan is not null && CanEdit && !PersistenceFailed;
    public bool RequiresCloseDecision => failure?.Code == ErrorCode.ProcessTerminationFailed;
    public bool HasPlan => approvedPlan is not null;
    public bool HasTracks => Tracks.Count != 0;
    public bool HasWarningCandidate => result?.Phase == JobPhase.AwaitingWarningReview;
    public bool HasWarningCloseDecision => WarningCloseRequested && HasWarningCandidate;
    public bool CanRespondToWarningClose => HasWarningCloseDecision && !IsBusy;
    public bool HasInterrupted => !interrupted.IsDefaultOrEmpty;
    public bool HasCleanableInterrupted => interrupted.Any(entry => entry.CanClean && entry.Output is not null);
    public string InterruptedDetails => DescribeInterrupted();
    public bool HasError => failure is not null && failure.Code != ErrorCode.Cancelled;
    public bool HasDiagnostics => !string.IsNullOrWhiteSpace(Diagnostics);
    public string Diagnostics => DescribeDiagnostics();
    public bool HasErrorDetail => !string.IsNullOrWhiteSpace(ErrorDetail);
    public bool HasCleanupIssue => !string.IsNullOrWhiteSpace(CleanupDetails);
    public bool HasSizeDifference => result?.OutputLength is not null && inspection is not null && result.PublishedPath is not null;
    public bool RemovesDefaultAudio => approvedPlan?.RemovesDefaultAudio == true;
    public bool NeedsZeroAudioConfirmation => inspection?.Tracks.Any(track => track.Kind == TrackKind.Audio) == true &&
        !Tracks.Any(track => track.Track.Kind == TrackKind.Audio && track.Keep);
    public string BackendDescription => inspection is null ? string.Empty : $"{inspection.Backend.Name} {inspection.Backend.Version}";
    public string MediaFeaturesText => DescribeMediaFeatures();
    public string StatusText => result?.PublishedPath is { } path && statusKey is "Published" or "PublishedWithWarnings" ? Localization.Format(new(statusKey, [path])) : Localization[statusKey];
    public string ErrorText => failure is null ? string.Empty : Localization[$"Error{failure.Code}"];
    public string ErrorDetail => failure is { Reason: not ErrorReason.None } reasonedFailure
        ? Localization[$"ErrorReason{reasonedFailure.Reason}"]
        : string.Empty;
    public string PlanDescription => approvedPlan is null ? string.Empty : Localization.Format(new("TracksSummary", [approvedPlan.RetainedTracks.Length,
        approvedPlan.Inspection.Tracks.Length - approvedPlan.RetainedTracks.Length]));
    public string PlanDestination => approvedPlan?.DestinationPath ?? string.Empty;
    public string PlanTracksText => DescribePlanTracks();
    public string PlanDefaultAudioText => DescribePlanDefaultAudio();
    public string SizeDifferenceText => DescribeSizeDifference();

    /// <summary>Restores only explicit language preferences and lists interrupted ownership records.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            var initialLanguageRevision = languageRevision;
            var savedLanguage = await services.Settings.LoadLanguageAsync();

            if (initialLanguageRevision == languageRevision)
            {
                Localization.SetLanguage(savedLanguage);
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

        NotifyPresentation();
    }

    /// <summary>Waits until cancellation has stopped all currently owned application work.</summary>
    public async Task ShutdownAsync()
    {
        statusKey = "CloseDuringWork";
        NotifyPresentation();

        if (activeCancellation is not null)
        {
            await activeCancellation.CancelAsync();
        }

        await activeOperation;
    }

    /// <summary>Requests an explicit decision without discarding or publishing a warning candidate.</summary>
    public void RequestWarningCloseReview()
    {
        if (!HasWarningCandidate)
        {
            return;
        }

        WarningCloseRequested = true;
        NotifyPresentation();
    }

    /// <summary>Allows closing only after the existing warning candidate was discarded with complete cleanup.</summary>
    public async Task<bool> DiscardWarningsForCloseAsync()
    {
        var canDiscard = HasWarningCandidate && !IsBusy;

        if (!canDiscard)
        {
            return false;
        }

        await DiscardWarningsCommand.ExecuteAsync(null);
        var discardCompleted = !HasWarningCandidate && !HasError && !HasCleanupIssue;
        return discardCompleted;
    }

    /// <summary>Invalidates a reviewed destination whenever its editable value changes.</summary>
    partial void OnOutputPathChanged(string value)
    {
        InvalidatePlan();
    }

    /// <summary>Requires a new plan after the silent-output confirmation changes.</summary>
    partial void OnZeroAudioConfirmedChanged(bool value)
    {
        InvalidatePlan();
    }

    /// <summary>Updates command availability throughout owned work.</summary>
    partial void OnIsBusyChanged(bool value)
    {
        NotifyPresentation();
    }

    /// <summary>Opens and inspects one real source through the application boundary.</summary>
    private async Task OpenSourceAsync()
    {
        var selectedPath = await SelectPathAsync(services.Dialogs.SelectSourceAsync);

        if (selectedPath is null)
        {
            return;
        }

        await ExecuteOwnedAsync(async cancellationToken =>
        {
            statusKey = "Inspecting";
            approvedPlan = null;
            inspection = null;
            result = null;
            Tracks.Clear();
            SourcePath = selectedPath;
            OutputPath = string.Empty;
            ZeroAudioConfirmed = false;
            NotifyPresentation();
            var inspectionResult = await services.Inspection.InspectAsync(new(selectedPath, cancellationToken));

            if (inspectionResult.IsStale)
            {
                return;
            }

            failure = inspectionResult.Failure;
            inspection = inspectionResult.Inspection;

            if (inspection is null)
            {
                statusKey = failure?.Code == ErrorCode.Cancelled ? "Cancelled" : "Failed";
                return;
            }

            foreach (var track in inspection.Tracks)
            {
                Tracks.Add(new TrackViewModel(new(track, Localization, InvalidatePlan)));
            }

            OutputPath = string.Empty;
            statusKey = "InspectionReady";
        });
    }

    /// <summary>Chooses an explicit folder and derives an editable non-overwriting filename.</summary>
    private async Task ChooseOutputAsync()
    {
        var directoryPath = await SelectPathAsync(services.Dialogs.SelectOutputFolderAsync);

        if (directoryPath is null)
        {
            return;
        }

        var sourceName = string.IsNullOrEmpty(SourcePath) ? "output" : Path.GetFileNameWithoutExtension(SourcePath);
        OutputPath = Path.Combine(directoryPath, $"{sourceName}.trimmed.mkv");
    }

    /// <summary>Reports native dialog failures without allowing an unobserved command exception.</summary>
    private async Task<string?> SelectPathAsync(Func<Task<string?>> selectPath)
    {
        try
        {
            return await selectPath();
        }
        catch (Exception exception)
        {
            services.Log.Error(exception, "A native storage dialog failed");
            failure = new(ErrorCode.IoFailure, exception.Message);
            statusKey = "Failed";
            NotifyPresentation();
            return null;
        }
    }

    /// <summary>Snapshots the approved tracks and explicit silent-output decision.</summary>
    private void ReviewPlan()
    {
        failure = null;

        if (inspection is null)
        {
            failure = new(ErrorCode.InvalidSource);
            NotifyPresentation();
            return;
        }

        var retainedIdentifiers = Tracks.Where(track => track.Keep).Select(track => track.Track.Id).ToArray();
        var planResult = PlanBuilder.Build(new(inspection, retainedIdentifiers, OutputPath, ZeroAudioConfirmed));
        approvedPlan = planResult.Plan;
        failure = planResult.Failure;

        if (!planResult.IsSuccess)
        {
            statusKey = "Failed";
            NotifyPresentation();
            return;
        }

        statusKey = approvedPlan?.IsNoChange == true ? "NoChange" : "PlanReady";
        NotifyPresentation();
    }

    /// <summary>Executes exactly the last reviewed immutable plan.</summary>
    private async Task RunAsync()
    {
        var plan = approvedPlan;

        if (plan is null)
        {
            return;
        }

        await ExecuteOwnedAsync(async cancellationToken =>
        {
            var progress = new Progress<JobProgress>(UpdateProgress);
            ApplyResult(await services.Jobs.RunAsync(new(plan, progress, cancellationToken)));
        });
    }

    /// <summary>Publishes the already verified warning candidate after application rechecks.</summary>
    private async Task PublishWarningsAsync()
    {
        var jobIdentifier = result?.JobId;

        if (jobIdentifier is null)
        {
            return;
        }

        await ExecuteOwnedAsync(async cancellationToken =>
            ApplyResult(await services.Jobs.PublishWarningsAsync(new(jobIdentifier.Value, cancellationToken))));
    }

    /// <summary>Discards only the application-owned warning candidate.</summary>
    private async Task DiscardWarningsAsync()
    {
        var jobIdentifier = result?.JobId;

        if (jobIdentifier is null)
        {
            return;
        }

        await ExecuteOwnedAsync(async cancellationToken =>
        {
            ApplyResult(await services.Jobs.DiscardWarningsAsync(new(jobIdentifier.Value, cancellationToken)));

            if (failure is not null)
            {
                return;
            }

            statusKey = HasCleanupIssue ? "DiscardCleanupIncomplete" : "Discarded";
        });
    }

    /// <summary>Requests cancellation while leaving process termination to the job owner.</summary>
    private void Cancel()
    {
        activeCancellation?.Cancel();
    }

    /// <summary>Returns to the same warning review without restarting or disposing the candidate.</summary>
    private void RemainInWarningReview()
    {
        WarningCloseRequested = false;
        NotifyPresentation();
    }

    /// <summary>Updates the UI immediately and persists only the stable language code.</summary>
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

    /// <summary>Offers validated cleanup rather than trusting a prior verification or process ID.</summary>
    private async Task CleanInterruptedAsync()
    {
        await ExecuteOwnedAsync(async cancellationToken =>
        {
            var issues = ImmutableArray.CreateBuilder<CleanupIssue>();

            foreach (var entry in interrupted.Where(entry => entry.CanClean))
            {
                if (entry.Output is not { } output)
                {
                    continue;
                }

                issues.AddRange(await services.Outputs.CleanupAsync(new(output, cancellationToken)));
            }

            CleanupDetails = string.Join(Environment.NewLine, issues.Select(issue => $"{issue.Path}: {issue.Detail}"));
            interrupted = await services.Outputs.FindInterruptedAsync(cancellationToken);
        });
    }

    /// <summary>Owns command cancellation and reports unexpected boundary failures in local logs.</summary>
    private async Task ExecuteOwnedAsync(Func<CancellationToken, Task> operation)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        activeOperation = completion.Task;
        activeCancellation = new CancellationTokenSource();
        IsBusy = true;
        IsProgressIndeterminate = true;
        ProgressValue = 0;
        failure = null;
        toolDiagnostics = string.Empty;
        CleanupDetails = string.Empty;

        try
        {
            await operation(activeCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            statusKey = "Cancelled";
        }
        catch (OperationFailureException exception)
        {
            failure = exception.Failure;
            statusKey = "Failed";
        }
        catch (Exception exception)
        {
            services.Log.Error(exception, "A desktop workflow failed");
            failure = new(ErrorCode.IoFailure, exception.Message);
            statusKey = "Failed";
        }
        finally
        {
            activeCancellation.Dispose();
            activeCancellation = null;
            IsBusy = false;
            IsProgressIndeterminate = false;
            NotifyPresentation();
            completion.TrySetResult();
        }
    }

    /// <summary>Stores outcome evidence separately from progress presentation.</summary>
    private void ApplyResult(JobResult jobResult)
    {
        result = jobResult;
        failure = jobResult.Failure;
        toolDiagnostics = jobResult.Tool?.Diagnostics ?? string.Empty;
        var cleanupIssues = jobResult.CleanupIssues.IsDefault ? [] : jobResult.CleanupIssues;
        CleanupDetails = string.Join(Environment.NewLine, cleanupIssues.Select(issue => $"{issue.Path}: {issue.Detail}"));
        statusKey = GetResultStatusKey(jobResult);

        if (jobResult.Phase != JobPhase.AwaitingWarningReview)
        {
            approvedPlan = null;
            WarningCloseRequested = false;
        }
    }

    /// <summary>Receives backend progress through the UI synchronization context.</summary>
    private void UpdateProgress(JobProgress progress)
    {
        if (!IsBusy)
        {
            return;
        }

        statusKey = GetPhaseKey(progress.Phase);
        IsProgressIndeterminate = progress.Percent is null;
        ProgressValue = progress.Percent ?? 0;
        OnPropertyChanged(nameof(StatusText));
    }

    /// <summary>Invalidates reviewed selections without changing an active immutable job.</summary>
    private void InvalidatePlan()
    {
        if (approvedPlan is not null)
        {
            approvedPlan = null;
            statusKey = "PlanInvalidated";
        }

        NotifyPresentation();
    }

    /// <summary>Lists exact retained and removed file-local identities from the immutable review.</summary>
    private string DescribePlanTracks()
    {
        if (approvedPlan is null)
        {
            return string.Empty;
        }

        var retainedIdentifiers = approvedPlan.RetainedTracks.Select(track => track.Id).ToHashSet();
        return string.Join(Environment.NewLine, approvedPlan.Inspection.Tracks.Select(track =>
        {
            var action = retainedIdentifiers.Contains(track.Id) ? Localization["Keep"] : Localization["Remove"];
            var title = string.IsNullOrWhiteSpace(track.Title) ? Localization["Untitled"] : track.Title;
            return $"{action}: {Localization[track.Kind.ToString()]} {track.Id} — {title} ({track.Codec}, {track.LanguageIetf ?? track.Language})";
        }));
    }

    /// <summary>Reports the resulting default-audio flags from the approved immutable selection.</summary>
    private string DescribePlanDefaultAudio()
    {
        var plan = approvedPlan;

        if (plan is null)
        {
            return string.Empty;
        }

        if (!plan.RetainedTracks.Any(track => track.Kind == TrackKind.Audio))
        {
            return Localization["PlanNoAudio"];
        }

        if (!plan.HasDefaultAudio)
        {
            return Localization["PlanNoDefaultAudio"];
        }

        var defaultIdentifiers = plan.RetainedTracks
            .Where(track => track.Kind == TrackKind.Audio && track.IsDefault)
            .Select(track => track.Id.ToString(CultureInfo.InvariantCulture));
        return Localization.Format(new("PlanDefaultAudioRetained", [string.Join(", ", defaultIdentifiers)]));
    }

    /// <summary>Shows reviewable paths without treating old journal stages as publication evidence.</summary>
    private string DescribeInterrupted()
    {
        return string.Join(Environment.NewLine, interrupted.Select(entry =>
        {
            var state = DescribeInterruptedState(entry);
            var paths = entry.Output is { } output
                ? Localization.Format(new("InterruptedPaths", [output.CandidatePath, output.DestinationPath]))
                : entry.Issue ?? string.Empty;
            return $"{state}{Environment.NewLine}{paths}";
        }));
    }

    /// <summary>Distinguishes invalid records from valid ownership that cannot currently be cleaned.</summary>
    private string DescribeInterruptedState(InterruptedOutput entry)
    {
        if (entry.Stage == "InvalidRecord")
        {
            return Localization["InterruptedInvalidRecord"];
        }

        return Localization[entry.CanClean ? "InterruptedCleanable" : "InterruptedUnresolved"];
    }

    /// <summary>Keeps original tool, adapter and cleanup details in an explicitly technical presentation.</summary>
    private string DescribeDiagnostics()
    {
        var details = new[] { toolDiagnostics, failure?.Detail ?? string.Empty, CleanupDetails };
        var distinctDetails = details.Where(detail => !string.IsNullOrWhiteSpace(detail)).Distinct(StringComparer.Ordinal);
        return string.Join(Environment.NewLine + Environment.NewLine, distinctDetails);
    }

    /// <summary>Reports measured growth or reduction without claiming that the original was removed.</summary>
    private string DescribeSizeDifference()
    {
        if (!HasSizeDifference)
        {
            return string.Empty;
        }

        var difference = inspection!.Source.Length - result!.OutputLength!.Value;
        var key = difference < 0 ? "SizeIncrease" : "SizeDifference";
        return Localization.Format(new(key, [Math.Abs(difference)]));
    }

    /// <summary>Reports inspected container features that belong to the preservation review.</summary>
    private string DescribeMediaFeatures()
    {
        if (inspection is null)
        {
            return string.Empty;
        }

        var chapters = Localization[inspection.HasChapters ? "Yes" : "No"];
        var tags = Localization[inspection.HasTags ? "Yes" : "No"];
        return Localization.Format(new("MediaFeatures", [inspection.Attachments.Length, chapters, tags]));
    }

    /// <summary>Keeps an approved warning publication distinguishable from a clean success.</summary>
    private static string GetResultStatusKey(JobResult jobResult)
    {
        if (jobResult.PublishedPath is null)
        {
            return GetPhaseKey(jobResult.Phase);
        }

        return jobResult.Tool?.ExitCode == 1 ? "PublishedWithWarnings" : "Published";
    }

    /// <summary>Refreshes language-sensitive values and all command predicates.</summary>
    private void NotifyPresentation()
    {
        OnPropertyChanged(string.Empty);
        OpenSourceCommand?.NotifyCanExecuteChanged();
        ChooseOutputCommand?.NotifyCanExecuteChanged();
        ReviewPlanCommand?.NotifyCanExecuteChanged();
        RunCommand?.NotifyCanExecuteChanged();
        CancelCommand?.NotifyCanExecuteChanged();
        PublishWarningsCommand?.NotifyCanExecuteChanged();
        DiscardWarningsCommand?.NotifyCanExecuteChanged();
        RemainInWarningReviewCommand?.NotifyCanExecuteChanged();
        CleanInterruptedCommand?.NotifyCanExecuteChanged();
    }

    /// <summary>Refreshes only current rows so repeated source inspection does not retain old tracks.</summary>
    private void RefreshLocalization()
    {
        foreach (var track in Tracks)
        {
            track.RefreshLocalization();
        }

        NotifyPresentation();
    }

    /// <summary>Maps protocol-independent phases to application-owned resource keys.</summary>
    private static string GetPhaseKey(JobPhase phase)
    {
        return phase switch
        {
            JobPhase.Validating => "Checking",
            JobPhase.AwaitingWarningReview => "WarningReview",
            JobPhase.Completed => "Finishing",
            _ => phase.ToString()
        };
    }
}
