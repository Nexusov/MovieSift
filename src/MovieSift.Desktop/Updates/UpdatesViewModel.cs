using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MovieSift.Application.Ports;
using MovieSift.Application.Updates;
using MovieSift.Desktop.Localization;
using Serilog;

namespace MovieSift.Desktop.Updates;

/// <summary>Presents optional release checks and explicitly requested installation independently of media jobs.</summary>
public sealed class UpdatesViewModel : ObservableObject
{
    private readonly IUpdateService service;
    private readonly IUpdatePreferencesStore preferences;
    private readonly ILogger log;
    private CancellationTokenSource? cancellation;
    private Task operation = Task.CompletedTask;
    private Task preferenceSave = Task.CompletedTask;
    private Task? initialization;
    private UpdateRelease? available;
    private bool checkOnStartup = true;
    private bool canInstall;
    private bool canNotify;
    private bool pendingNotification;
    private bool checking;
    private bool installing;
    private bool launching;
    private bool installerLaunched;
    private bool preferenceFailed;
    private bool stopping;
    private long preferenceRevision;
    private double progress;
    private string statusKey = "UpdatesNotChecked";
    private PreparedUpdate? pendingDiscard;

    /// <summary>Connects update I/O and localized presentation without starting network work in the constructor.</summary>
    public UpdatesViewModel(IUpdateService service, IUpdatePreferencesStore preferences,
        LocalizationService localization, AppVersion currentVersion, ILogger log, bool previousInstallFailed = false)
    {
        this.service = service;
        this.preferences = preferences;
        this.log = log;
        PreviousInstallFailed = previousInstallFailed;
        Localization = localization;
        CurrentVersion = currentVersion.ToString();
        CheckCommand = new AsyncRelayCommand(() => CheckAsync(false), () => !HasPendingOperation);
        InstallCommand = new AsyncRelayCommand(InstallAsync, () => HasUpdate && canInstall && !HasPendingOperation);
        CancelCommand = new RelayCommand(() => cancellation?.Cancel(), () => installing && !launching);
        ShowCommand = new RelayCommand(() => ShowRequested?.Invoke(), () => HasUpdate);
        Localization.PropertyChanged += (_, _) => Refresh();
    }

    public event Action? ShowRequested;
    public event Func<PreparedUpdate, Task>? InstallPrepared;
    public LocalizationService Localization { get; }
    public string CurrentVersion { get; }
    public string CurrentVersionText => Localization.Format(new("UpdatesCurrentVersion", [CurrentVersion]));
    public string AvailableVersionText => available is { } release
        ? Localization.Format(new("UpdatesAvailableVersion", [release.Version.ToString()])) : string.Empty;
    public string StatusText => Localization[statusKey];
    public bool HasUpdate => available is not null;
    public bool IsChecking => checking;
    public bool IsInstalling => installing;
    public bool IsRestarting => launching;
    public bool HasPendingOperation => checking || installing;
    public bool HasPendingWork => HasPendingOperation || initialization is { IsCompleted: false } || !preferenceSave.IsCompleted;
    public bool PreferenceFailed => preferenceFailed;
    public bool PreviousInstallFailed { get; }
    public bool HasCleanupNotice => pendingDiscard is not null;
    public string CleanupNotice => pendingDiscard is { } update
        ? Localization.Format(new("UpdatesCleanupFailed", [update.SessionDirectory])) : string.Empty;
    public bool InstallationBlocked => HasUpdate && !canInstall && !installing;
    public bool CanCancel => installing && !launching;
    public double Progress => progress;
    public IAsyncRelayCommand CheckCommand { get; }
    public IAsyncRelayCommand InstallCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand ShowCommand { get; }

    public bool CheckOnStartup
    {
        get => checkOnStartup;
        set
        {
            if (installing)
            {
                OnPropertyChanged();
                return;
            }

            if (!SetProperty(ref checkOnStartup, value))
            {
                return;
            }

            var revision = ++preferenceRevision;
            preferenceSave = SavePreferenceAsync(preferenceSave, value, revision);
            Refresh();
        }
    }

    /// <summary>Loads once and performs at most one nonblocking automatic check for this application lifetime.</summary>
    public Task InitializeAsync() => initialization ??= InitializeCoreAsync();

    /// <summary>Defers unsolicited notification until the shell has no active work or mandatory media decision.</summary>
    public void SetAvailability(bool installationAllowed, bool notificationAllowed)
    {
        var changed = canInstall != installationAllowed || canNotify != notificationAllowed;
        canInstall = installationAllowed;
        canNotify = notificationAllowed;

        if (changed)
        {
            Refresh();
        }

        if (pendingNotification && canNotify && !HasPendingOperation)
        {
            pendingNotification = false;
            ShowRequested?.Invoke();
        }
    }

    /// <summary>Stops network/staging work before normal shutdown while preserving already saved preferences.</summary>
    public async Task ShutdownAsync()
    {
        stopping = true;
        if (!launching && cancellation is not null)
        {
            await cancellation.CancelAsync();
        }

        await operation;

        if (initialization is { } loading)
        {
            await loading;
        }

        await preferenceSave;
    }

    /// <summary>Waits for optional preference writes without waiting recursively for the installation callback.</summary>
    public Task WhenPreferencesSavedAsync() => preferenceSave;

    /// <summary>Starts the verified helper only after the window has established its safe-close decision.</summary>
    public async Task LaunchInstallerAsync(PreparedUpdate prepared)
    {
        cancellation?.Token.ThrowIfCancellationRequested();
        launching = true;
        statusKey = "UpdatesRestarting";
        Refresh();
        await service.LaunchInstallerAsync(prepared, CancellationToken.None);
        installerLaunched = true;
    }

    /// <summary>A delayed preferences read cannot overwrite an explicit setting edited in the meantime.</summary>
    private async Task InitializeCoreAsync()
    {
        var revision = preferenceRevision;

        try
        {
            var loaded = await preferences.LoadAsync();

            if (revision == preferenceRevision)
            {
                checkOnStartup = loaded;
            }
        }
        catch (Exception exception)
        {
            preferenceFailed = true;
            log.Warning(exception, "Update preferences could not be read");
        }

        Refresh();

        if (checkOnStartup && !stopping)
        {
            await CheckAsync(true);
        }
    }

    /// <summary>Serializes explicit preference edits and reports optional storage failure without blocking media.</summary>
    private async Task SavePreferenceAsync(Task previous, bool value, long revision)
    {
        await previous;

        try
        {
            await preferences.SaveAsync(value);

            if (revision == preferenceRevision)
            {
                preferenceFailed = false;
            }
        }
        catch (Exception exception)
        {
            if (revision == preferenceRevision)
            {
                preferenceFailed = true;
            }

            log.Warning(exception, "Update preferences could not be saved");
        }

        Refresh();
    }

    /// <summary>Network failure is a status message; only a newer release opens an automatic notification.</summary>
    private async Task CheckAsync(bool automatic)
    {
        if (HasPendingOperation || stopping)
        {
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        operation = completion.Task;
        cancellation = new();
        checking = true;
        statusKey = "UpdatesChecking";
        Refresh();

        try
        {
            available = await service.CheckAsync(cancellation.Token);
            statusKey = HasUpdate ? "UpdatesReady" : "UpdatesCurrent";
            pendingNotification = automatic && HasUpdate && !PreviousInstallFailed;
        }
        catch (OperationCanceledException)
        {
            statusKey = "UpdatesCancelled";
        }
        catch (Exception exception)
        {
            statusKey = "UpdatesCheckFailed";
            log.Warning(exception, "MovieSift release check failed");
        }
        finally
        {
            checking = false;
            cancellation.Dispose();
            cancellation = null;
            Refresh();
            completion.TrySetResult();
        }

        SetAvailability(canInstall, canNotify);
    }

    /// <summary>Stages a complete package while reserving the shell, then delegates the safe-close boundary.</summary>
    private async Task InstallAsync()
    {
        if (available is not { } release || !canInstall || HasPendingOperation || InstallPrepared is null)
        {
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        operation = completion.Task;
        cancellation = new();
        installing = true;
        installerLaunched = false;
        progress = 0;
        statusKey = "UpdatesDownloading";
        Refresh();

        try
        {
            if (pendingDiscard is { } leftover)
            {
                await service.DiscardAsync(leftover, cancellation.Token);
                pendingDiscard = null;
            }

            var reporting = new Progress<UpdateProgress>(value =>
            {
                if (installing && !launching)
                {
                    progress = value.TotalBytes > 0 ? Math.Clamp(100d * value.BytesReceived / value.TotalBytes, 0, 100) : 0;
                    OnPropertyChanged(nameof(Progress));
                }
            });
            var prepared = await service.StageAsync(release, reporting, cancellation.Token);
            pendingDiscard = prepared;
            cancellation.Token.ThrowIfCancellationRequested();

            if (!canInstall)
            {
                throw new UpdateException(UpdateErrorCode.Busy, "Media or recovery work prevents installation.");
            }

            await InstallPrepared(prepared);
        }
        catch (OperationCanceledException)
        {
            statusKey = "UpdatesCancelled";
        }
        catch (Exception exception)
        {
            statusKey = exception is UpdateException update ? $"UpdateError{update.Code}" : "UpdateErrorInstaller";
            log.Warning(exception, "MovieSift update installation did not complete");
        }
        finally
        {
            if (pendingDiscard is { } discarded && !installerLaunched)
            {
                try
                {
                    await service.DiscardAsync(discarded, CancellationToken.None);
                    pendingDiscard = null;
                }
                catch (Exception exception)
                {
                    log.Warning(exception, "Update staging cleanup did not complete at {UpdateDirectory}", discarded.SessionDirectory);
                }
            }
            else if (installerLaunched)
            {
                pendingDiscard = null;
            }

            installing = false;
            launching = false;
            cancellation.Dispose();
            cancellation = null;
            Refresh();
            completion.TrySetResult();
        }
    }

    /// <summary>Keeps translated text and command availability in sync without altering media authority.</summary>
    private void Refresh()
    {
        OnPropertyChanged(string.Empty);
        CheckCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        ShowCommand.NotifyCanExecuteChanged();
    }
}
