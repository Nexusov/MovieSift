using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Serilog;
using TrackTrim.Application.Appearance;
using TrackTrim.Application.Ports;
using TrackTrim.Desktop.Appearance;
using TrackTrim.Desktop.Localization;
using TrackTrim.Desktop.ViewModels;
using TrackTrim.Desktop.Views;
using Xunit;

namespace TrackTrim.Desktop.Tests;

/// <summary>Checks cosmetic policy, live state isolation and bounded asynchronous preference presentation.</summary>
public sealed class AppearanceTests
{
    /// <summary>Checks every real foreground/background pair in both variants using linearized-sRGB contrast.</summary>
    [Theory]
    [InlineData("#5B6CDA")]
    [InlineData("#168272")]
    [InlineData("#C64165")]
    [InlineData("#A56A13")]
    [InlineData("#FFFFFF")]
    [InlineData("#000000")]
    public void ResolvedPalettesMeetActualTextAndIndicatorContrast(string accent)
    {
        var result = AppearancePalettePolicy.Resolve(new(AccentHex: accent));
        Assert.True(result.IsValid, result.ErrorKey);

        foreach (var palette in new[] { result.Light!, result.Dark! })
        {
            foreach (var surface in new[] { palette.Window, palette.Panel, palette.Secondary })
            {
                foreach (var foreground in new[] { palette.Text, palette.Muted, palette.Error, palette.Warning })
                {
                    Assert.True(AppearancePalettePolicy.Contrast(foreground, surface) >= AppearancePalettePolicy.TextContrast);
                }

                foreach (var indicator in new[] { palette.Accent, palette.AccentHover, palette.AccentPressed, palette.Focus, palette.Border })
                {
                    Assert.True(AppearancePalettePolicy.Contrast(indicator, surface) >= AppearancePalettePolicy.IndicatorContrast);
                }
            }

            foreach (var background in new[] { palette.Accent, palette.AccentHover, palette.AccentPressed })
            {
                Assert.True(AppearancePalettePolicy.Contrast(palette.OnAccent, background) >= AppearancePalettePolicy.TextContrast);
            }

            Assert.True(AppearancePalettePolicy.Contrast(palette.Text, palette.Selection) >= AppearancePalettePolicy.TextContrast);
        }

        Assert.Equal(21, AppearancePalettePolicy.Contrast(Colors.Black, Colors.White), precision: 8);
    }

    /// <summary>Resolves the pinned Fluent Expander and ToggleButton templates against each live semantic palette.</summary>
    [AvaloniaFact]
    public void ExpanderAndToggleTemplatesUseSemanticColorsInBothVariants()
    {
        var application = Assert.IsAssignableFrom<Avalonia.Application>(Avalonia.Application.Current);
        var originalTheme = application.RequestedThemeVariant;
        var hadLight = application.Resources.ThemeDictionaries.TryGetValue(ThemeVariant.Light, out var originalLight);
        var hadDark = application.Resources.ThemeDictionaries.TryGetValue(ThemeVariant.Dark, out var originalDark);
        var expander = new Expander { Header = "File choices", Content = new TextBlock { Text = "Actual file detail" }, IsExpanded = true };
        var toggle = new ToggleButton { Content = "Selected option" };
        var window = new Window { Content = new StackPanel { Children = { expander, toggle } } };
        var applier = new AppearanceApplier(application);
        window.Show();

        try
        {
            foreach (var theme in new[] { AppearanceTheme.Light, AppearanceTheme.Dark })
            {
                var settings = new AppearanceSettings(Theme: theme);
                var palettes = AppearancePalettePolicy.Resolve(settings);
                applier.Apply(settings, palettes);
                toggle.IsChecked = false;
                toggle.IsEnabled = true;
                expander.IsEnabled = true;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var expected = theme == AppearanceTheme.Dark ? palettes.Dark! : palettes.Light!;
                var header = Assert.Single(expander.GetVisualDescendants().OfType<ToggleButton>(), control => control.Name == "ExpanderHeader");
                var headerBackground = Assert.Single(header.GetVisualDescendants().OfType<Border>(), control => control.Name == "ToggleButtonBackground");
                var headerText = Assert.Single(header.GetVisualDescendants().OfType<ContentPresenter>(), control => control.Name == "PART_ContentPresenter");
                var content = Assert.Single(expander.GetVisualDescendants().OfType<Border>(), control => control.Name == "ExpanderContent");
                var toggleContent = Assert.Single(toggle.GetVisualDescendants().OfType<ContentPresenter>(), control => control.Name == "PART_ContentPresenter");
                Assert.Equal(expected.Panel, BrushColor(headerBackground.Background));
                Assert.Equal(expected.Text, BrushColor(headerText.Foreground));
                Assert.Equal(expected.Panel, BrushColor(content.Background));
                Assert.Equal(expected.Border, BrushColor(content.BorderBrush));
                Assert.Equal(expected.Secondary, BrushColor(toggleContent.Background));
                toggle.IsChecked = true;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(expected.Selection, BrushColor(toggleContent.Background));
                Assert.Equal(expected.Text, BrushColor(toggleContent.Foreground));
                toggle.IsEnabled = false;
                expander.IsEnabled = false;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(expected.Muted, BrushColor(toggleContent.Foreground));
                Assert.Equal(expected.Muted, BrushColor(headerText.Foreground));
            }
        }
        finally
        {
            window.Close();
            application.RequestedThemeVariant = originalTheme;

            if (hadLight)
            {
                application.Resources.ThemeDictionaries[ThemeVariant.Light] = originalLight!;
            }

            if (!hadLight)
            {
                application.Resources.ThemeDictionaries.Remove(ThemeVariant.Light);
            }

            if (hadDark)
            {
                application.Resources.ThemeDictionaries[ThemeVariant.Dark] = originalDark!;
            }

            if (!hadDark)
            {
                application.Resources.ThemeDictionaries.Remove(ThemeVariant.Dark);
            }
        }
    }

    /// <summary>Checks the actual effective template brush instead of the presence of a resource key.</summary>
    private static Color BrushColor(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    /// <summary>Invalid HEX or unreadable custom pairs retain the last complete usable appearance.</summary>
    [AvaloniaFact]
    public async Task InvalidEditsNeverReplaceTheUsablePaletteOrSaveInvalidValues()
    {
        var store = new AppearanceUiStore();
        var applier = new AppearanceApplier(null);
        var editor = new AppearanceViewModel(new(store, new LocalizationService(), applier));
        await editor.InitializeAsync();
        var usable = editor.Current;
        var palettes = applier.Palettes;
        editor.AccentHex = "#nothex";
        Assert.True(editor.HasError);
        Assert.Same(usable, editor.Current);
        Assert.Same(palettes, applier.Palettes);
        editor.AccentHex = usable.AccentHex;
        usable = editor.Current;
        palettes = applier.Palettes;
        editor.LightText = "#FFFFFF";
        Assert.True(editor.HasError);
        Assert.Same(usable, editor.Current);
        Assert.Same(palettes, applier.Palettes);
        await editor.WhenIdleAsync();
        Assert.DoesNotContain(store.Writes, write => write.Settings.LightOverrides?.PrimaryText == "#FFFFFF");
        Assert.DoesNotContain(store.Writes, write => write.Settings.AccentHex == "#nothex");
    }

    /// <summary>A late startup result cannot erase a newer editor choice, and rapid valid edits coalesce into one save.</summary>
    [AvaloniaFact]
    public async Task DelayedStartupPreservesNewerChoiceAndCoalescesSaving()
    {
        var read = new TaskCompletionSource<AppearanceLoadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new AppearanceUiStore { Read = () => read.Task };
        var editor = new AppearanceViewModel(new(store, new LocalizationService(), new(null)));
        var loading = editor.InitializeAsync();
        editor.ThemeIndex = (int)AppearanceTheme.Light;
        editor.AccentHex = "#168272";
        editor.AccentHex = "#A56A13";
        read.SetResult(new(new(Theme: AppearanceTheme.Dark)));
        await loading;
        await editor.WhenIdleAsync();
        Assert.Equal(AppearanceTheme.Light, editor.Current.Theme);
        Assert.Equal("#A56A13", editor.Current.AccentHex);
        Assert.Equal(editor.Current, Assert.Single(store.Writes).Settings);
    }

    /// <summary>A user choice made before deferred shell initialization wins over saved defaults too.</summary>
    [AvaloniaFact]
    public async Task EditBeforeStartupReadCannotBeReplacedBySavedAppearance()
    {
        var store = new AppearanceUiStore { Read = () => Task.FromResult(new AppearanceLoadResult(new(Theme: AppearanceTheme.Dark))) };
        var editor = new AppearanceViewModel(new(store, new LocalizationService(), new(null)));
        editor.ThemeIndex = (int)AppearanceTheme.Light;
        await editor.InitializeAsync();
        await editor.WhenIdleAsync();
        Assert.Equal(AppearanceTheme.Light, editor.Current.Theme);
        Assert.Equal(AppearanceTheme.Light, Assert.Single(store.Writes).Settings.Theme);
    }

    /// <summary>Malformed startup data remains visible and untouched until explicit cosmetic reset.</summary>
    [AvaloniaFact]
    public async Task MalformedCatalogSupportsSessionEditsAndOnlyExplicitResetReplacesIt()
    {
        var store = new AppearanceUiStore { Read = () => Task.FromResult(new AppearanceLoadResult(null, new(AppearanceErrorCode.InvalidData, "Controlled malformed record"))) };
        var editor = new AppearanceViewModel(new(store, new LocalizationService(), new(null)));
        await editor.InitializeAsync();
        editor.ThemeIndex = (int)AppearanceTheme.Dark;
        Assert.True(editor.HasError);
        Assert.Equal(AppearanceTheme.Dark, editor.Current.Theme);
        await editor.WhenIdleAsync();
        Assert.Empty(store.Writes);
        await editor.ResetCommand.ExecuteAsync(null);
        Assert.True(Assert.Single(store.Writes).ReplaceInvalidData);
        Assert.Equal(AppearanceSettings.Default, editor.Current);
    }

    /// <summary>A completed explicit reset supersedes the older startup read's corrupt or failed outcome.</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulResetSupersedesDelayedStartupFailureAndAllowsLaterSaves(bool readThrows)
    {
        var read = new TaskCompletionSource<AppearanceLoadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resetSaved = BatchViewModelFixture.Signal();
        var store = new AppearanceUiStore
        {
            Read = () => read.Task,
            Save = request =>
            {
                if (request.ReplaceInvalidData)
                {
                    resetSaved.TrySetResult();
                }

                return Task.FromResult(new AppearanceSaveResult(true));
            }
        };
        var editor = new AppearanceViewModel(new(store, new LocalizationService(), new(null)));
        var loading = editor.InitializeAsync();
        var resetting = editor.ResetCommand.ExecuteAsync(null);
        await BatchViewModelFixture.Await(resetSaved.Task);
        Assert.True(Assert.Single(store.Writes).ReplaceInvalidData);

        if (readThrows)
        {
            read.SetException(new IOException("Controlled obsolete startup read failure"));
        }
        else
        {
            read.SetResult(new(null, new(AppearanceErrorCode.InvalidData, "Controlled obsolete malformed record")));
        }

        await BatchViewModelFixture.Await(loading);
        await BatchViewModelFixture.Await(resetting);
        Assert.False(editor.HasError);
        Assert.Empty(editor.Diagnostics);
        editor.ThemeIndex = (int)AppearanceTheme.Dark;
        await editor.WhenIdleAsync();
        Assert.Equal(2, store.Writes.Count);
        Assert.False(store.Writes.Last().ReplaceInvalidData);
        Assert.Equal(AppearanceTheme.Dark, store.Writes.Last().Settings.Theme);
        Assert.Equal(AppearanceTheme.Dark, editor.Current.Theme);
        Assert.False(editor.HasError);
    }

    /// <summary>An uncommitted reset cannot hide malformed startup data or authorize ordinary replacement.</summary>
    [AvaloniaFact]
    public async Task FailedResetDoesNotSuppressDelayedCatalogFailure()
    {
        var read = new TaskCompletionSource<AppearanceLoadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resetAttempted = BatchViewModelFixture.Signal();
        var store = new AppearanceUiStore
        {
            Read = () => read.Task,
            Save = _ =>
            {
                resetAttempted.TrySetResult();
                return Task.FromResult(new AppearanceSaveResult(false, new(AppearanceErrorCode.PersistenceUnavailable, "Controlled reset write failure")));
            }
        };
        var editor = new AppearanceViewModel(new(store, new LocalizationService(), new(null)));
        var loading = editor.InitializeAsync();
        var resetting = editor.ResetCommand.ExecuteAsync(null);
        await BatchViewModelFixture.Await(resetAttempted.Task);
        read.SetResult(new(null, new(AppearanceErrorCode.InvalidData, "Malformed record still present")));
        await BatchViewModelFixture.Await(loading);
        await BatchViewModelFixture.Await(resetting);
        editor.ThemeIndex = (int)AppearanceTheme.Dark;
        await editor.WhenIdleAsync();
        Assert.Single(store.Writes);
        Assert.True(editor.HasError);
        Assert.Equal(editor.Localization["AppearanceCatalogError"], editor.ErrorText);
        Assert.Equal(AppearanceTheme.Dark, editor.Current.Theme);
    }

    /// <summary>A committed reset clears its obsolete catalog error even while newer invalid text remains in the editor.</summary>
    [AvaloniaFact]
    public async Task CommittedResetClearsCatalogErrorAfterNewerInvalidEditorInput()
    {
        var entered = BatchViewModelFixture.Signal();
        var save = new TaskCompletionSource<AppearanceSaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new AppearanceUiStore
        {
            Read = () => Task.FromResult(new AppearanceLoadResult(null, new(AppearanceErrorCode.InvalidData, "Controlled malformed catalog"))),
            Save = _ =>
            {
                entered.TrySetResult();
                return save.Task;
            }
        };
        var editor = new AppearanceViewModel(new(store, new LocalizationService(), new(null)));
        await editor.InitializeAsync();
        Assert.Equal(editor.Localization["AppearanceCatalogError"], editor.ErrorText);
        var resetting = editor.ResetCommand.ExecuteAsync(null);
        await BatchViewModelFixture.Await(entered.Task);
        editor.AccentHex = "#nothex";
        save.SetResult(new(true));
        await BatchViewModelFixture.Await(resetting);
        Assert.Equal(editor.Localization["AppearanceInvalidHex"], editor.ErrorText);
        editor.AccentHex = AppearanceSettings.Default.AccentHex;
        Assert.False(editor.HasError);
        Assert.Empty(editor.Diagnostics);
        Assert.Single(store.Writes);
        Assert.Equal(AppearanceSettings.Default, editor.Current);
    }

    /// <summary>Theme, motion and language changes leave the same live owned job and immutable plans untouched.</summary>
    [AvaloniaFact]
    public async Task AppearanceAndLanguageChangesPreserveTheOwnedActiveJob()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.PrepareAsync();
        await fixture.Appearance.InitializeAsync();
        var plans = fixture.Batch.Items.Select(item => item.ApprovedPlan).ToArray();
        var selections = fixture.Batch.Items.Select(item => item.Tracks.Select(track => track.Keep).ToArray()).ToArray();
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
            var active = Assert.Single(fixture.Backend.RemuxRequests);
            var snapshot = fixture.Batch.Snapshot;
            var review = snapshot.Review;
            var progress = fixture.Batch.ProgressValue;
            fixture.Appearance.ThemeIndex = (int)AppearanceTheme.Dark;
            fixture.Appearance.MotionIndex = (int)AppearanceMotion.Reduced;
            fixture.Appearance.AccentHex = "#168272";

            foreach (var language in new[] { "ru", "en" })
            {
                await fixture.Main.ChangeLanguageCommand.ExecuteAsync(language);
                Dispatcher.UIThread.RunJobs();
                Assert.Same(active, Assert.Single(fixture.Backend.RemuxRequests));
                Assert.Same(snapshot, fixture.Batch.Snapshot);
                Assert.Same(review, fixture.Batch.Snapshot.Review);
                Assert.Same(plans[0], active.Plan);
                Assert.Equal(plans, fixture.Batch.Items.Select(item => item.ApprovedPlan));
                Assert.Equal(progress, fixture.Batch.ProgressValue);
                Assert.True(fixture.Batch.IsBusy);
                Assert.True(fixture.Batch.CancelCommand.CanExecute(null));
                Assert.False(active.CancellationToken.IsCancellationRequested);

                for (var index = 0; index < fixture.Batch.Items.Count; index++)
                {
                    Assert.Equal(selections[index], fixture.Batch.Items[index].Tracks.Select(track => track.Keep));
                }
            }

            await fixture.Appearance.WhenIdleAsync();
            Assert.Single(fixture.Backend.RemuxRequests);
            Assert.True(fixture.Batch.CancelCommand.CanExecute(null));
        }
        finally
        {
            release.TrySetResult();
        }

        await BatchViewModelFixture.Await(run);
        Assert.Equal(TrackTrim.Application.Batches.BatchPhase.Completed, fixture.Batch.Snapshot.Phase);
        Assert.Equal(plans.Length, fixture.Backend.RemuxRequests.Count);
    }

    /// <summary>Session-only write failure and language changes preserve review, selections and media availability.</summary>
    [AvaloniaFact]
    public async Task CosmeticFailureDoesNotBlockReviewedWorkOrTranslateUserColors()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.PrepareAsync();
        var review = fixture.Batch.Snapshot.Review;
        var plans = fixture.Batch.Items.Select(item => item.ApprovedPlan).ToArray();
        var selections = fixture.Batch.Items.Select(item => item.Tracks.Select(track => track.Keep).ToArray()).ToArray();
        var store = new AppearanceUiStore { Save = _ => Task.FromResult(new AppearanceSaveResult(false, new(AppearanceErrorCode.PersistenceUnavailable, "Controlled read-only storage"))) };
        var editor = new AppearanceViewModel(new(store, fixture.Localization, new(null)));
        await editor.InitializeAsync();
        var view = new AppearanceView { DataContext = editor };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            editor.ThemeIndex = (int)AppearanceTheme.Dark;
            editor.AccentHex = "#168272";
            await editor.WhenIdleAsync();
            Assert.True(editor.HasError);
            Assert.Equal(AppearanceTheme.Dark, editor.Current.Theme);
            var configuration = editor.Current;

            foreach (var language in new[] { "ru", "en" })
            {
                await fixture.Main.ChangeLanguageCommand.ExecuteAsync(language);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("#168272", view.FindControl<TextBox>("AppearanceAccentHex")!.Text);
                Assert.Same(configuration, editor.Current);
                Assert.Equal((int)AppearanceTheme.Dark, view.FindControl<ComboBox>("AppearanceTheme")!.SelectedIndex);
                Assert.Equal(editor.Localization["AppearanceReset"], view.FindControl<Button>("AppearanceReset")!.Content);
                Assert.Equal(editor.Localization["AppearanceSessionOnly"], editor.ErrorText);
                Assert.Same(review, fixture.Batch.Snapshot.Review);
                Assert.Equal(plans, fixture.Batch.Items.Select(item => item.ApprovedPlan));
                Assert.True(fixture.Batch.RunCommand.CanExecute(null));

                for (var index = 0; index < fixture.Batch.Items.Count; index++)
                {
                    Assert.Equal(selections[index], fixture.Batch.Items[index].Tracks.Select(track => track.Keep));
                }
            }

            Assert.Empty(fixture.Backend.RemuxRequests);
            Assert.Equal(0, fixture.Outputs.CleanupCount);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Constructing the actual settings controls and translating them never creates cosmetic writes or busy ownership.</summary>
    [AvaloniaFact]
    public async Task UnchangedSettingsViewAndLanguageChangesDoNotSaveOrBecomeActiveWork()
    {
        var fixture = new BatchViewModelFixture();
        var store = new AppearanceUiStore();
        var editor = new AppearanceViewModel(new(store, fixture.Localization, new(Avalonia.Application.Current)));
        using var logger = new LoggerConfiguration().CreateLogger();
        var shell = new MainViewModel(new(fixture.Settings, fixture.Outputs, fixture.Localization, true, logger, fixture.Batch, editor));
        var window = new MainWindow { DataContext = shell };
        window.Show();

        try
        {
            await shell.InitializeAsync();
            shell.ToggleAppearanceCommand.Execute(null);

            foreach (var language in new[] { "en", "ru", "en" })
            {
                await shell.ChangeLanguageCommand.ExecuteAsync(language);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Assert.False(editor.HasPendingSave);
                Assert.False(shell.HasActiveOperation);
                Assert.Equal(AppearanceSettings.Default, editor.Current);
                Assert.Empty(store.Writes);
            }
        }
        finally
        {
            await shell.ShutdownAsync();
            window.Close();
        }
    }

    /// <summary>A failed older cosmetic save cannot drop a newer valid edit or leave its save unawaited.</summary>
    [AvaloniaFact]
    public async Task NewerEditIsPersistedAfterAnOlderInFlightWriteFails()
    {
        var entered = BatchViewModelFixture.Signal();
        var release = new TaskCompletionSource<AppearanceSaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeCount = 0;
        var store = new AppearanceUiStore
        {
            Save = _ =>
            {
                writeCount++;

                if (writeCount == 1)
                {
                    entered.TrySetResult();
                    return release.Task;
                }

                return Task.FromResult(new AppearanceSaveResult(true));
            }
        };
        var editor = new AppearanceViewModel(new(store, new LocalizationService(), new(null)));
        await editor.InitializeAsync();
        editor.ThemeIndex = (int)AppearanceTheme.Dark;
        await BatchViewModelFixture.Await(entered.Task);
        editor.AccentHex = "#168272";
        release.SetException(new IOException("Controlled first save failure"));
        await editor.WhenIdleAsync();
        Assert.Equal(2, store.Writes.Count);
        Assert.Equal(editor.Current, store.Writes.Last().Settings);
        Assert.False(editor.HasPendingSave);
        Assert.False(editor.HasError);
    }

    /// <summary>Reduced motion removes actual button/drop transitions without changing control availability.</summary>
    [AvaloniaFact]
    public void ReducedMotionRemovesCosmeticTransitionsImmediately()
    {
        var button = new Button { Content = "Test" };
        var window = new Window { Content = button };
        AppearanceMotionPolicy.Track(window);
        window.Show();

        try
        {
            AppearanceMotionPolicy.Configure(AppearanceMotion.Reduced);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("reduced-motion", window.Classes);
            Assert.Null(button.Transitions);
            Assert.True(button.IsEnabled);
            Assert.Equal(TimeSpan.Zero, Avalonia.Application.Current!.Resources["TTPressDuration"]);
            AppearanceMotionPolicy.Configure(AppearanceMotion.Full);
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("reduced-motion", window.Classes);
            Assert.NotNull(button.Transitions);
        }
        finally
        {
            window.Close();
            AppearanceMotionPolicy.Configure(AppearanceMotion.System);
        }
    }
}

/// <summary>Provides deterministic cosmetic persistence outcomes without filesystem or media side effects.</summary>
internal sealed class AppearanceUiStore : IAppearanceStore
{
    public Func<Task<AppearanceLoadResult>>? Read { get; init; }
    public Func<AppearanceSaveRequest, Task<AppearanceSaveResult>>? Save { get; init; }
    public List<AppearanceSaveRequest> Writes { get; } = [];

    /// <summary>Returns a controlled startup catalog or the missing-file default.</summary>
    public Task<AppearanceLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Read?.Invoke() ?? Task.FromResult(new AppearanceLoadResult(AppearanceSettings.Default, IsMissing: true));

    /// <summary>Captures immutable configurations for save and failure assertions.</summary>
    public Task<AppearanceSaveResult> SaveAsync(AppearanceSaveRequest request)
    {
        Writes.Add(request);
        return Save?.Invoke(request) ?? Task.FromResult(new AppearanceSaveResult(true));
    }
}
