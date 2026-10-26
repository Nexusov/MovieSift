using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MovieSift.Application;
using MovieSift.Application.Batches;
using MovieSift.Application.Inspection;
using MovieSift.Application.Jobs;
using MovieSift.Application.Ports;
using MovieSift.Application.Updates;
using MovieSift.Core.Media;
using MovieSift.Desktop.Localization;
using MovieSift.Desktop.Updates;
using MovieSift.Desktop.ViewModels;
using MovieSift.Desktop.Views;
using Serilog;
using Xunit;

namespace MovieSift.Desktop.Tests;

/// <summary>Exercises explicit update consent and shell ownership without network calls or real installation.</summary>
public sealed class UpdatePresentationTests
{
    /// <summary>A slow startup request runs once while ordinary media commands remain available.</summary>
    [AvaloniaFact]
    public async Task StartupChecksOnceWithoutReservingTheMediaWorkspace()
    {
        var response = Signal<UpdateRelease?>();
        var fixture = new UpdateFixture();
        fixture.Service.Check = token => response.Task.WaitAsync(token);
        var notifications = 0;
        fixture.Updates.ShowRequested += () => notifications++;
        var initialization = fixture.Main.InitializeAsync();
        var secondInitialization = fixture.Updates.InitializeAsync();

        Assert.Equal(1, fixture.Service.CheckCount);
        Assert.True(fixture.Updates.IsChecking);
        Assert.True(fixture.Main.CanEdit);
        Assert.True(fixture.Batch.AddFilesCommand.CanExecute(null));
        Assert.False(fixture.Updates.CheckCommand.CanExecute(null));
        Assert.Equal(0, fixture.Service.StageCount);
        response.SetResult(UpdateFixture.Release);
        await Await(Task.WhenAll(initialization, secondInitialization));
        await fixture.Updates.InitializeAsync();

        Assert.Equal(1, fixture.Service.CheckCount);
        Assert.Equal(1, notifications);
        Assert.True(fixture.Updates.HasUpdate);
        Assert.Equal(0, fixture.Service.LaunchCount);
    }

    /// <summary>An explicit opt-out takes precedence over a delayed preference read.</summary>
    [AvaloniaFact]
    public async Task StartupOptOutDuringPreferenceReadPreventsAutomaticNetworkWork()
    {
        var read = Signal<bool>();
        var fixture = new UpdateFixture();
        fixture.Preferences.Load = () => read.Task;
        var initialization = fixture.Main.InitializeAsync();
        fixture.Updates.CheckOnStartup = false;
        read.SetResult(true);
        await Await(initialization);
        await Await(fixture.Updates.InitializeAsync());
        await fixture.Updates.WhenPreferencesSavedAsync();

        Assert.False(fixture.Updates.CheckOnStartup);
        Assert.Equal([false], fixture.Preferences.Writes);
        Assert.Equal(0, fixture.Service.CheckCount);
        Assert.True(fixture.Main.CanEdit);

        await fixture.Updates.CheckCommand.ExecuteAsync(null);
        Assert.Equal(1, fixture.Service.CheckCount);
        Assert.True(fixture.Updates.HasUpdate);
        Assert.Equal(0, fixture.Service.StageCount);
    }

    /// <summary>Offline checks report a local status without an unsolicited dialog or media failure.</summary>
    [AvaloniaFact]
    public async Task OfflineStartupDoesNotBlockMediaOrOpenAnErrorDialog()
    {
        var fixture = new UpdateFixture();
        fixture.Service.Check = _ => Task.FromException<UpdateRelease?>(new HttpRequestException("Controlled offline response."));
        var notifications = 0;
        fixture.Updates.ShowRequested += () => notifications++;
        await fixture.Main.InitializeAsync();

        Assert.Equal(0, notifications);
        Assert.Equal(fixture.Localization["UpdatesCheckFailed"], fixture.Updates.StatusText);
        Assert.False(fixture.Updates.HasUpdate);
        Assert.False(fixture.Main.PersistenceFailed);
        Assert.False(fixture.Main.HasError);
        Assert.True(fixture.Batch.AddFilesCommand.CanExecute(null));
        Assert.False(fixture.Main.HasBlockingOperation);
        Assert.Equal(0, fixture.Service.StageCount);
    }

    /// <summary>An update discovered during inspection waits for the real owner and appears only once automatically.</summary>
    [AvaloniaFact]
    public async Task NotificationWaitsForInspectionAndSurvivesDismissalAsAnExplicitOffer()
    {
        var check = Signal<UpdateRelease?>();
        var inspect = Signal();
        var entered = Signal();
        var fixture = new UpdateFixture();
        fixture.Service.Check = token => check.Task.WaitAsync(token);
        fixture.Backend.OnInspect = async request =>
        {
            entered.TrySetResult();
            await inspect.Task.WaitAsync(request.CancellationToken);
        };
        var notifications = 0;
        fixture.Updates.ShowRequested += () => notifications++;
        var initialization = fixture.Main.InitializeAsync();
        var loading = fixture.Batch.AddFilesCommand.ExecuteAsync(null);
        await Await(entered.Task);
        check.SetResult(UpdateFixture.Release);
        await Await(initialization);
        await Await(fixture.Updates.InitializeAsync());

        Assert.Equal(0, notifications);
        Assert.True(fixture.Updates.InstallationBlocked);
        inspect.SetResult();
        await Await(loading);
        await fixture.Batch.Recovery.WhenIdleAsync();
        Assert.Equal(1, notifications);

        fixture.Main.IsAppearanceOpen = true;
        fixture.Main.IsAppearanceOpen = false;
        Assert.Equal(1, notifications);
        fixture.Updates.ShowCommand.Execute(null);
        Assert.Equal(2, notifications);
        Assert.Equal(0, fixture.Service.StageCount);
        Assert.Equal(0, fixture.Service.LaunchCount);
    }

    /// <summary>Escape dismisses the actual localized prompt while preserving the available release.</summary>
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public async Task DismissingThePromptRetainsTheOfferWithoutStartingADownload(string language)
    {
        var fixture = new UpdateFixture();
        await fixture.Main.InitializeAsync();
        fixture.Localization.SetLanguage(language);
        var window = new UpdateWindow { DataContext = fixture.Updates };
        window.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();
            var install = window.FindControl<Button>("InstallButton")!;
            Assert.Equal(fixture.Localization["UpdatesInstall"], install.Content);
            Assert.Contains("1.1.0", fixture.Updates.AvailableVersionText);
            Assert.NotEqual("UpdatesAvailableVersion", fixture.Updates.AvailableVersionText);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();

            Assert.False(window.IsVisible);
            Assert.True(fixture.Updates.HasUpdate);
            Assert.True(fixture.Updates.ShowCommand.CanExecute(null));
            Assert.Equal(0, fixture.Service.StageCount);
            Assert.Equal(0, fixture.Service.LaunchCount);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The real shell keeps a visible entry point after dismissal and does not create duplicate dialogs.</summary>
    [AvaloniaFact]
    public async Task ShellBadgeReopensOneDismissedNotificationWithoutDownloading()
    {
        var fixture = new UpdateFixture();
        var window = new MainWindow { DataContext = fixture.Main };
        window.Show();

        try
        {
            await fixture.Updates.InitializeAsync();
            Dispatcher.UIThread.RunJobs();
            var notification = Assert.Single(window.OwnedWindows.OfType<UpdateWindow>());
            fixture.Updates.ShowCommand.Execute(null);
            Assert.Single(window.OwnedWindows.OfType<UpdateWindow>());
            notification.Close();
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(window.OwnedWindows);
            var badge = window.FindControl<Button>("AvailableUpdateButton")!;
            Assert.True(badge.IsVisible);
            Assert.True(badge.IsEnabled);
            Assert.True(badge.Focus());
            Press(window, PhysicalKey.Space);
            Dispatcher.UIThread.RunJobs();
            Assert.Single(window.OwnedWindows.OfType<UpdateWindow>()).Close();
            Assert.Equal(1, fixture.Service.CheckCount);
            Assert.Equal(0, fixture.Service.StageCount);
        }
        finally
        {
            foreach (var child in window.OwnedWindows.ToArray())
            {
                child.Close();
            }

            await fixture.Main.ShutdownAsync();
            window.Close();
        }
    }

    /// <summary>The actual Install button stages once, hands off once, and closes the old shell only after success.</summary>
    [AvaloniaFact]
    public async Task ExplicitInstallThroughTheWindowTransfersOwnershipBeforeClosing()
    {
        var fixture = new UpdateFixture();
        var window = new MainWindow { DataContext = fixture.Main };
        window.Show();

        try
        {
            await fixture.Updates.InitializeAsync();
            Dispatcher.UIThread.RunJobs();
            var notification = Assert.Single(window.OwnedWindows.OfType<UpdateWindow>());
            Assert.True(notification.FindControl<Button>("InstallButton")!.Focus());
            Press(notification, PhysicalKey.Space);
            if (fixture.Updates.InstallCommand.ExecutionTask is { } installation)
            {
                await Await(installation);
            }

            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, fixture.Service.StageCount);
            Assert.Equal(1, fixture.Service.LaunchCount);
            Assert.Empty(fixture.Service.Discarded);
            Assert.False(window.IsVisible);
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            foreach (var child in window.OwnedWindows.ToArray())
            {
                child.Close();
            }

            window.Close();
        }
    }

    /// <summary>Only one explicit install stages a package; cancellation restores the same reviewed workspace.</summary>
    [AvaloniaFact]
    public async Task InstallSerializesRequestsAndCancellationRestoresMediaAvailability()
    {
        var staged = Signal<PreparedUpdate>();
        var fixture = new UpdateFixture();
        await fixture.Main.InitializeAsync();
        await fixture.PrepareMediaAsync();
        var review = fixture.Batch.Snapshot.Review;
        fixture.Service.Stage = token => staged.Task.WaitAsync(token);
        fixture.Updates.InstallPrepared += fixture.Updates.LaunchInstallerAsync;

        Assert.True(fixture.Batch.CanRun);
        var installation = fixture.Updates.InstallCommand.ExecuteAsync(null);
        await fixture.Updates.InstallCommand.ExecuteAsync(null);
        Assert.Equal(1, fixture.Service.StageCount);
        Assert.True(fixture.Updates.IsInstalling);
        Assert.False(fixture.Updates.InstallCommand.CanExecute(null));
        Assert.False(fixture.Batch.RunCommand.CanExecute(null));
        Assert.False(fixture.Batch.AddFilesCommand.CanExecute(null));
        Assert.Empty(fixture.Backend.RemuxRequests);

        fixture.Updates.CancelCommand.Execute(null);
        await Await(installation);
        Assert.False(fixture.Updates.IsInstalling);
        Assert.True(fixture.Batch.CanRun);
        Assert.Same(review, fixture.Batch.Snapshot.Review);
        Assert.True(fixture.Updates.HasUpdate);
        Assert.Equal(0, fixture.Service.LaunchCount);
        Assert.Equal(fixture.Localization["UpdatesCancelled"], fixture.Updates.StatusText);
    }

    /// <summary>Cancellation after a package is staged discards that owned package before returning to idle.</summary>
    [AvaloniaFact]
    public async Task CancellationAfterStagingDiscardsThePreparedPackageWithoutLaunching()
    {
        var fixture = new UpdateFixture();
        await fixture.Main.InitializeAsync();
        fixture.Updates.InstallPrepared += fixture.Updates.LaunchInstallerAsync;
        fixture.Service.Stage = _ =>
        {
            fixture.Updates.CancelCommand.Execute(null);
            return Task.FromResult(UpdateFixture.Prepared);
        };
        await fixture.Updates.InstallCommand.ExecuteAsync(null);

        Assert.Equal([UpdateFixture.Prepared], fixture.Service.Discarded);
        Assert.Equal(0, fixture.Service.LaunchCount);
        Assert.True(fixture.Updates.HasUpdate);
        Assert.True(fixture.Main.CanEdit);
    }

    /// <summary>A failed safe-close callback never leaves a staged installer or consumes the release offer.</summary>
    [AvaloniaFact]
    public async Task FailedHandoffDiscardsThePreparedPackageAndKeepsTheOffer()
    {
        var fixture = new UpdateFixture();
        await fixture.Main.InitializeAsync();
        fixture.Updates.InstallPrepared += _ => Task.FromException(new UpdateException(UpdateErrorCode.Busy, "Controlled state change."));
        await fixture.Updates.InstallCommand.ExecuteAsync(null);

        Assert.Equal([UpdateFixture.Prepared], fixture.Service.Discarded);
        Assert.Equal(0, fixture.Service.LaunchCount);
        Assert.Equal(fixture.Localization["UpdateErrorBusy"], fixture.Updates.StatusText);
        Assert.True(fixture.Updates.InstallCommand.CanExecute(null));
        Assert.True(fixture.Main.CanEdit);
    }

    /// <summary>A cleanup fault stays separate from the handoff failure and cannot silently lose ownership on retry.</summary>
    [AvaloniaFact]
    public async Task FailedStagingCleanupPreservesItsNoticeAndRetriesBeforeAnotherDownload()
    {
        var fixture = new UpdateFixture();
        await fixture.Main.InitializeAsync();
        fixture.Service.Discard = _ => Task.FromException(new IOException("Controlled cleanup failure."));
        fixture.Updates.InstallPrepared += _ => Task.FromException(new UpdateException(UpdateErrorCode.Busy, "Controlled close failure."));
        await fixture.Updates.InstallCommand.ExecuteAsync(null);

        Assert.Equal(fixture.Localization["UpdateErrorBusy"], fixture.Updates.StatusText);
        Assert.True(fixture.Updates.HasCleanupNotice);
        Assert.Contains(UpdateFixture.Prepared.SessionDirectory, fixture.Updates.CleanupNotice);
        Assert.True(fixture.Main.CanEdit);

        var cleanupRetried = false;
        fixture.Service.Discard = _ =>
        {
            cleanupRetried = true;
            return Task.CompletedTask;
        };
        fixture.Service.Stage = _ =>
        {
            Assert.True(cleanupRetried);
            return Task.FromResult(UpdateFixture.Prepared);
        };
        await fixture.Updates.InstallCommand.ExecuteAsync(null);
        Assert.False(fixture.Updates.HasCleanupNotice);
        Assert.Equal(2, fixture.Service.StageCount);
        Assert.Equal(0, fixture.Service.LaunchCount);
    }

    /// <summary>Availability is rechecked after staging so a newly protected state cannot launch an installer.</summary>
    [AvaloniaFact]
    public async Task ProtectedStateThatAppearsDuringStagingPreventsHandoff()
    {
        var staged = Signal<PreparedUpdate>();
        var fixture = new UpdateFixture();
        await fixture.Main.InitializeAsync();
        fixture.Service.Stage = token => staged.Task.WaitAsync(token);
        fixture.Updates.InstallPrepared += fixture.Updates.LaunchInstallerAsync;
        var installation = fixture.Updates.InstallCommand.ExecuteAsync(null);
        fixture.Main.PersistenceFailed = true;
        staged.SetResult(UpdateFixture.Prepared);
        await Await(installation);

        Assert.Equal(0, fixture.Service.LaunchCount);
        Assert.Equal([UpdateFixture.Prepared], fixture.Service.Discarded);
        Assert.True(fixture.Updates.InstallationBlocked);
        Assert.True(fixture.Updates.HasUpdate);
    }

    /// <summary>Unresolved ownership and warning decisions retain the update offer but prohibit installation.</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedOwnershipOrLiveWarningBlocksInstall(bool warning)
    {
        var fixture = new UpdateFixture();

        if (!warning)
        {
            fixture.Outputs.Interrupted = [new(null, "InvalidRecord", false, "controlled-untrusted-record.json")];
        }

        await fixture.Main.InitializeAsync();

        if (warning)
        {
            await fixture.PrepareMediaAsync();
            fixture.Backend.ExitCodes[fixture.Batch.Items[0].SourcePath] = 1;
            await fixture.Batch.RunCommand.ExecuteAsync(null);
            Assert.True(fixture.Main.HasWarningCandidate);
        }

        fixture.Updates.InstallPrepared += fixture.Updates.LaunchInstallerAsync;
        Assert.False(fixture.Main.CanInstallUpdate);
        Assert.True(fixture.Updates.InstallationBlocked);
        Assert.False(fixture.Updates.InstallCommand.CanExecute(null));
        await fixture.Updates.InstallCommand.ExecuteAsync(null);
        Assert.Equal(0, fixture.Service.StageCount);
        Assert.True(fixture.Updates.HasUpdate);
    }

    /// <summary>A pending destructive queue-reset choice must be resolved before installation can start.</summary>
    [AvaloniaFact]
    public async Task QueueResetConfirmationDefersInstallation()
    {
        var fixture = new UpdateFixture();
        await fixture.Main.InitializeAsync();
        await fixture.PrepareMediaAsync();
        fixture.Batch.NewBatchCommand.Execute(null);
        Assert.True(fixture.Batch.HasNewBatchConfirmation);
        Assert.False(fixture.Main.CanInstallUpdate);
        Assert.False(fixture.Updates.InstallCommand.CanExecute(null));
        fixture.Batch.KeepCurrentBatchCommand.Execute(null);
        Assert.True(fixture.Main.CanInstallUpdate);
        Assert.True(fixture.Updates.InstallCommand.CanExecute(null));
    }

    /// <summary>An in-flight language write cannot overlap the installer handoff of portable settings.</summary>
    [AvaloniaFact]
    public async Task LanguagePreferenceWriteDefersInstallationUntilItCompletes()
    {
        var saved = Signal();
        var fixture = new UpdateFixture();
        await fixture.Main.InitializeAsync();
        fixture.Settings.Save = _ => saved.Task;
        var language = fixture.Main.ChangeLanguageCommand.ExecuteAsync("ru");

        Assert.False(fixture.Main.CanInstallUpdate);
        Assert.False(fixture.Updates.InstallCommand.CanExecute(null));
        saved.SetResult();
        await Await(language);
        Assert.True(fixture.Main.CanInstallUpdate);
        Assert.True(fixture.Updates.InstallCommand.CanExecute(null));
        Assert.Equal("ru", fixture.Localization.Language);
        Assert.Equal(0, fixture.Service.StageCount);
    }

    /// <summary>Closing while downloading requests cancellation and waits for the existing staging task.</summary>
    [AvaloniaFact]
    public async Task ClosingDownloadPromptWaitsForAcknowledgedCancellation()
    {
        var cancellationSeen = Signal();
        var stopped = Signal();
        var fixture = new UpdateFixture();
        await fixture.Main.InitializeAsync();
        fixture.Updates.InstallPrepared += fixture.Updates.LaunchInstallerAsync;
        fixture.Service.Stage = async token =>
        {
            using var registration = token.Register(() => cancellationSeen.TrySetResult());
            await stopped.Task;
            token.ThrowIfCancellationRequested();
            return UpdateFixture.Prepared;
        };
        var window = new UpdateWindow { DataContext = fixture.Updates };
        window.Show();

        try
        {
            var installation = fixture.Updates.InstallCommand.ExecuteAsync(null);
            window.Close();
            await Await(cancellationSeen.Task);
            Assert.True(window.IsVisible);
            Assert.True(fixture.Updates.IsInstalling);
            Assert.False(fixture.Main.CanEdit);
            stopped.SetResult();
            await Await(installation);
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.IsVisible);
            Assert.Equal(0, fixture.Service.LaunchCount);
            Assert.True(fixture.Updates.HasUpdate);
        }
        finally
        {
            stopped.TrySetResult();
            window.Close();
        }
    }

    /// <summary>Changing UI language updates actual controls while keeping the selected release and media review.</summary>
    [AvaloniaFact]
    public async Task LanguageChangesRefreshUpdateControlsWithoutLosingReviewedMedia()
    {
        var fixture = new UpdateFixture();
        await fixture.Main.InitializeAsync();
        await fixture.PrepareMediaAsync();
        var review = fixture.Batch.Snapshot.Review;
        var view = new UpdatesView { DataContext = fixture.Updates };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            var english = fixture.Updates.AvailableVersionText;
            await fixture.Main.ChangeLanguageCommand.ExecuteAsync("ru");
            Dispatcher.UIThread.RunJobs();
            Assert.NotEqual(english, fixture.Updates.AvailableVersionText);
            Assert.Contains("1.1.0", fixture.Updates.AvailableVersionText);
            var install = Assert.Single(view.GetVisualDescendants().OfType<Button>(), button => ReferenceEquals(button.Command, fixture.Updates.InstallCommand));
            Assert.Equal(fixture.Localization["UpdatesInstall"], install.Content);
            Assert.NotEqual("UpdatesInstall", install.Content);
            Assert.Same(review, fixture.Batch.Snapshot.Review);
            Assert.True(fixture.Updates.HasUpdate);
            Assert.Equal(0, fixture.Service.StageCount);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A reachable desktop acknowledges startup while an optional release request is still pending.</summary>
    [AvaloniaFact]
    public async Task StartupAcknowledgementDoesNotWaitForOptionalNetwork()
    {
        var response = Signal<UpdateRelease?>();
        var acknowledged = Signal();
        var fixture = new UpdateFixture();
        fixture.Service.Check = token => response.Task.WaitAsync(token);
        var window = new MainWindow { DataContext = fixture.Main };
        window.StartupCompleted += () => acknowledged.TrySetResult();
        window.Show();

        try
        {
            await Await(acknowledged.Task);
            Assert.False(response.Task.IsCompleted);
            Assert.True(window.IsVisible);
            Assert.True(fixture.Updates.IsChecking);
            Assert.True(fixture.Main.CanEdit);
            Assert.Equal(1, fixture.Service.CheckCount);
            response.SetResult(null);
            await Await(fixture.Updates.InitializeAsync());
        }
        finally
        {
            response.TrySetResult(null);
            window.Close();
        }
    }

    /// <summary>A manual release check cannot authorize installation before interrupted ownership has been loaded.</summary>
    [AvaloniaFact]
    public async Task ManualInstallWaitsForCoreStartupInitialization()
    {
        var settings = Signal<string>();
        var fixture = new UpdateFixture();
        fixture.Settings.Load = () => settings.Task;
        fixture.Preferences.Load = () => Task.FromResult(false);
        fixture.Updates.InstallPrepared += fixture.Updates.LaunchInstallerAsync;
        var initialization = fixture.Main.InitializeAsync();

        try
        {
            await fixture.Updates.CheckCommand.ExecuteAsync(null);
            Assert.True(fixture.Updates.HasUpdate);
            Assert.False(initialization.IsCompleted);
            Assert.False(fixture.Main.CanInstallUpdate);
            Assert.False(fixture.Updates.InstallCommand.CanExecute(null));
            await fixture.Updates.InstallCommand.ExecuteAsync(null);
            Assert.Equal(0, fixture.Service.StageCount);
            settings.SetResult("en");
            await Await(initialization);
            Assert.True(fixture.Main.CanInstallUpdate);
            Assert.True(fixture.Updates.InstallCommand.CanExecute(null));
            Assert.Equal(0, fixture.Service.LaunchCount);
        }
        finally
        {
            settings.TrySetResult("en");
            await Await(initialization);
        }
    }

    /// <summary>Normal close cancels and awaits the owned startup request after startup has already been acknowledged.</summary>
    [AvaloniaFact]
    public async Task NormalCloseWaitsForCancellationOfBackgroundStartupCheck()
    {
        var acknowledged = Signal();
        var cancelled = Signal();
        var finished = Signal();
        var fixture = new UpdateFixture();
        fixture.Service.Check = async token =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            await finished.Task;
            token.ThrowIfCancellationRequested();
            return null;
        };
        var window = new MainWindow { DataContext = fixture.Main };
        window.StartupCompleted += () => acknowledged.TrySetResult();
        window.Show();

        try
        {
            await Await(acknowledged.Task);
            window.Close();
            await Await(cancelled.Task);
            Assert.True(window.IsVisible);
            Assert.True(fixture.Updates.IsChecking);
            finished.SetResult();
            await Await(fixture.Updates.ShutdownAsync());
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.IsVisible);
            Assert.False(fixture.Updates.HasPendingWork);
            Assert.Equal(0, fixture.Service.StageCount);
        }
        finally
        {
            finished.TrySetResult();
            await Await(fixture.Updates.ShutdownAsync());
            window.Close();
        }
    }

    /// <summary>Bounds synchronization failures without using timing as proof of lifecycle ordering.</summary>
    private static Task Await(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    /// <summary>Uses continuations that cannot recursively execute inside a controlled completion.</summary>
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Provides a typed controlled result without real background work.</summary>
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Routes deliberate keyboard activation through the real headless control.</summary>
    private static void Press(Window window, PhysicalKey key)
    {
        window.KeyPressQwerty(key, RawInputModifiers.None);
        window.KeyReleaseQwerty(key, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Composes one actual shell and media owner around deterministic update I/O.</summary>
    private sealed class UpdateFixture
    {
        public static readonly UpdateRelease Release = new(new(1, 1, 0), new("https://github.com/Nexusov/MovieSift/releases/tag/v1.1.0"),
            new("MovieSift-linux-x64.tar.gz", new("https://github.com/Nexusov/MovieSift/releases/download/v1.1.0/MovieSift-linux-x64.tar.gz"), 1234, new string('a', 64)));
        public static readonly PreparedUpdate Prepared = new("controlled-update-session", new(1, 1, 0));
        public UpdateService Service { get; } = new();
        public UpdatePreferences Preferences { get; } = new();
        public UpdateLanguageSettings Settings { get; } = new();
        public FakeOutputStore Outputs { get; } = new();
        public BatchUiBackend Backend { get; } = new();
        public LocalizationService Localization { get; } = new();
        public UpdatesViewModel Updates { get; }
        public BatchViewModel Batch { get; }
        public MainViewModel Main { get; }

        /// <summary>Reuses media test ports without constructing a second shell competing for availability.</summary>
        public UpdateFixture()
        {
            var source = new BatchUiSourceAccess(Backend);
            var session = new BatchSession();
            var coordinator = new BatchCoordinator(session, new InspectionService(Backend, source), new JobCoordinator(Backend, source, Outputs), source);
            var logger = new LoggerConfiguration().CreateLogger();
            var discovery = new BatchUiDiscovery(Backend.Inspections.Values.Select(inspection => inspection.Source).ToImmutableArray());
            Batch = new(new(session, coordinator, discovery, new BatchUiDialogs(Backend.Inspections.Keys.ToArray()), Localization, true,
                logger, new(new BatchUiPresetStore()), new BatchUiQueueListStore()));
            Updates = new(Service, Preferences, Localization, new(1, 0, 0), logger);
            Main = new(new(Settings, Outputs, Localization, true, logger, Batch, Updates: Updates));
        }

        /// <summary>Produces a real immutable review through ordinary selection commands before updater gating.</summary>
        public async Task PrepareMediaAsync()
        {
            await Batch.AddFilesCommand.ExecuteAsync(null);
            await Batch.ChooseOutputCommand.ExecuteAsync(null);

            foreach (var item in Batch.Items)
            {
                foreach (var track in item.Tracks.Where(track => track.Track.Kind == TrackKind.Subtitles))
                {
                    track.Keep = false;
                }
            }

            await Batch.ReviewCommand.ExecuteAsync(null);
            await Batch.Recovery.WhenIdleAsync();
            Assert.True(Batch.CanRun);
        }
    }

    /// <summary>Records explicit update requests and exposes deterministic network/staging boundaries.</summary>
    private sealed class UpdateService : IUpdateService
    {
        public Func<CancellationToken, Task<UpdateRelease?>>? Check { get; set; }
        public Func<CancellationToken, Task<PreparedUpdate>>? Stage { get; set; }
        public Func<PreparedUpdate, Task>? Discard { get; set; }
        public int CheckCount { get; private set; }
        public int StageCount { get; private set; }
        public int LaunchCount { get; private set; }
        public List<PreparedUpdate> Discarded { get; } = [];

        /// <summary>Returns a controlled release without an HTTP client.</summary>
        public Task<UpdateRelease?> CheckAsync(CancellationToken cancellationToken = default)
        {
            CheckCount++;
            return Check?.Invoke(cancellationToken) ?? Task.FromResult<UpdateRelease?>(UpdateFixture.Release);
        }

        /// <summary>Creates no files and records each explicit download request.</summary>
        public Task<PreparedUpdate> StageAsync(UpdateRelease release, IProgress<UpdateProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            StageCount++;
            return Stage?.Invoke(cancellationToken) ?? Task.FromResult(UpdateFixture.Prepared);
        }

        /// <summary>Records a handoff without starting a process or replacing an application.</summary>
        public Task LaunchInstallerAsync(PreparedUpdate update, CancellationToken cancellationToken = default)
        {
            LaunchCount++;
            return Task.CompletedTask;
        }

        /// <summary>Records only the exact prepared package offered by the staging boundary.</summary>
        public Task DiscardAsync(PreparedUpdate update, CancellationToken cancellationToken = default)
        {
            Discarded.Add(update);
            return Discard?.Invoke(update) ?? Task.CompletedTask;
        }
    }

    /// <summary>Separates optional update-check settings from language and media persistence.</summary>
    private sealed class UpdatePreferences : IUpdatePreferencesStore
    {
        public Func<Task<bool>>? Load { get; set; }
        public List<bool> Writes { get; } = [];

        /// <summary>Allows a delayed read to race an explicit user edit deterministically.</summary>
        public Task<bool> LoadAsync(CancellationToken cancellationToken = default) => Load?.Invoke() ?? Task.FromResult(true);

        /// <summary>Records a setting without writing portable files.</summary>
        public Task SaveAsync(bool checkOnStartup, CancellationToken cancellationToken = default)
        {
            Writes.Add(checkOnStartup);
            return Task.CompletedTask;
        }
    }

    /// <summary>Lets a language write remain active across an attempted installer handoff.</summary>
    private sealed class UpdateLanguageSettings : ISettingsStore
    {
        public Func<Task<string>>? Load { get; set; }
        public Func<SaveLanguageRequest, Task>? Save { get; set; }

        /// <summary>Starts each presentation fixture in the product's explicit first-launch language.</summary>
        public Task<string> LoadLanguageAsync(CancellationToken cancellationToken = default) => Load?.Invoke() ?? Task.FromResult("en");

        /// <summary>Uses a controlled completion without performing filesystem writes.</summary>
        public Task SaveLanguageAsync(SaveLanguageRequest request) => Save?.Invoke(request) ?? Task.CompletedTask;
    }
}
