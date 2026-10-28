using System.Collections.Immutable;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MovieSift.Application.Localization;
using MovieSift.Desktop.Localization;
using MovieSift.Desktop.Views;
using Xunit;

namespace MovieSift.Desktop.Tests;

/// <summary>Exercises explicit translation intent without granting any media execution authority.</summary>
public sealed class LanguageSettingsTests
{
    /// <summary>Preserves every reviewed choice through a successful download, cache reuse and separate cosmetic panels.</summary>
    [AvaloniaFact]
    public async Task DownloadAndLanguagePanelsPreserveReviewedWorkspace()
    {
        var store = new ControlledTranslations();
        var fixture = new BatchViewModelFixture(translationPacks: store);
        await fixture.PrepareAsync();
        var reviewed = fixture.Batch.Snapshot;
        var languages = fixture.Main.Languages;
        var window = new MainWindow { DataContext = fixture.Main, Width = 640, Height = 480 };
        window.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(12, languages.Languages.Count);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(window.FindControl<Button>("LanguageButton")!)));
            fixture.Main.ToggleLanguageCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(fixture.Main.IsLanguageOpen);
            Assert.False(fixture.Main.IsAppearanceOpen);
            var selecting = languages.SelectAsync("ru");
            await BatchViewModelFixture.Await(store.Started.Task);
            Assert.Equal("en", fixture.Localization.Language);
            Assert.True(languages.IsBusy);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var cancel = Assert.Single(window.GetVisualDescendants().OfType<Button>(), button => button.Name == "CancelLanguageDownload");
            var cancelOrigin = cancel.TranslatePoint(default, window)!.Value;
            Assert.True(cancel.IsEffectivelyVisible && cancel.IsEffectivelyEnabled);
            Assert.InRange(cancelOrigin.Y, 0, window.ClientSize.Height - cancel.Bounds.Height);
            Assert.Same(reviewed, fixture.Batch.Snapshot);
            store.Complete.TrySetResult(ControlledTranslations.Pack("ru"));
            await BatchViewModelFixture.Await(selecting);
            Assert.Equal("ru", fixture.Localization.Language);
            Assert.True(languages.IsInstalled("ru"));
            Assert.Equal("ru", fixture.Settings.Language);
            Assert.Same(reviewed, fixture.Batch.Snapshot);
            await languages.SelectAsync("en");
            await languages.SelectAsync("ru");
            Assert.Equal(1, store.Downloads);
            var restartedLocalization = new LocalizationService();
            var restarted = new LanguageSettingsViewModel(new(store, restartedLocalization, locale => Task.CompletedTask));
            await restarted.RestoreCachedAsync("en");
            Assert.True(restarted.IsInstalled("ru"));
            Assert.Equal("en", restartedLocalization.Language);
            Assert.Equal(1, store.Downloads);
            fixture.Main.ToggleAppearanceCommand.Execute(null);
            Assert.False(fixture.Main.IsLanguageOpen);
            Assert.True(fixture.Main.IsAppearanceOpen);
            fixture.Main.ToggleLanguageCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(fixture.Main.IsLanguageOpen);
            Assert.Same(window.FindControl<Button>("LanguageButton"), window.FocusManager!.GetFocusedElement());
            Assert.Same(reviewed, fixture.Batch.Snapshot);
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            store.Complete.TrySetResult(ControlledTranslations.Pack("ru"));
            await languages.ShutdownAsync();
            window.Close();
        }
    }

    /// <summary>Cancellation and availability gates retain the actual language without installing partial data.</summary>
    [AvaloniaFact]
    public async Task CancelAndShutdownDoNotApplyLateTranslations()
    {
        var store = new ControlledTranslations();
        var localization = new LocalizationService();
        var writes = 0;
        var languages = new LanguageSettingsViewModel(new(store, localization, locale =>
        {
            localization.SetLanguage(locale);
            writes++;
            return Task.CompletedTask;
        }));
        languages.SetAvailability(false);
        await languages.SelectAsync("ru");
        Assert.Equal(0, store.Downloads);
        languages.SetAvailability(true);
        var selecting = languages.SelectAsync("ru");
        await BatchViewModelFixture.Await(store.Started.Task);
        languages.CancelCommand.Execute(null);
        await BatchViewModelFixture.Await(selecting);
        Assert.False(languages.IsBusy);
        Assert.False(languages.HasError);
        Assert.Equal("en", localization.Language);
        Assert.Equal(0, writes);
        Assert.False(languages.IsInstalled("ru"));
        var second = languages.SelectAsync("ru");
        await languages.ShutdownAsync();
        await second;
        store.Complete.TrySetResult(ControlledTranslations.Pack("ru"));
        Assert.Equal("en", localization.Language);
        Assert.Equal(0, writes);
    }

    /// <summary>Startup is local-only and unreadable packs retain a usable English interface and visible recovery.</summary>
    [AvaloniaFact]
    public async Task MissingOrInvalidPackPreservesEnglishAndExistingState()
    {
        var store = new ControlledTranslations();
        var localization = new LocalizationService();
        var languages = new LanguageSettingsViewModel(new(store, localization, locale =>
        {
            localization.SetLanguage(locale);
            return Task.CompletedTask;
        }));
        await languages.RestoreCachedAsync("ru");
        Assert.Equal("en", localization.Language);
        Assert.Equal(0, store.Downloads);
        Assert.True(languages.HasError);
        Assert.True(languages.RetryCommand.CanExecute(null));
        var selecting = languages.SelectAsync("ru");
        store.Complete.TrySetException(new TranslationPackException(TranslationErrorCode.InvalidPack, "controlled invalid pack"));
        await selecting;
        Assert.Equal("en", localization.Language);
        Assert.True(languages.HasError);
        Assert.Equal(localization["LanguageInvalidPack"], languages.ErrorText);
        languages.SearchText = "pt-BR";
        Assert.Equal("pt-BR", Assert.Single(languages.Languages).Code);
    }

    /// <summary>Explicit Retry bypasses a rejected local pack without deleting it before a verified download.</summary>
    [AvaloniaFact]
    public async Task RetryCanReplaceRejectedCache()
    {
        var store = new ControlledTranslations { CacheReadFailure = true };
        var localization = new LocalizationService();
        var languages = new LanguageSettingsViewModel(new(store, localization, locale =>
        {
            localization.SetLanguage(locale);
            return Task.CompletedTask;
        }));
        await languages.SelectAsync("ru");
        Assert.True(languages.HasError);
        Assert.Equal(0, store.Downloads);
        Assert.Equal("en", localization.Language);
        store.Complete.TrySetResult(ControlledTranslations.Pack("ru"));
        await languages.RetryCommand.ExecuteAsync(null);
        Assert.Equal(1, store.Downloads);
        Assert.Equal("ru", localization.Language);
        Assert.False(languages.HasError);
    }

    /// <summary>Preserves complete regional/script locale values rather than collapsing them to two letters.</summary>
    [Theory]
    [InlineData("pt-BR")]
    [InlineData("zh-Hans")]
    public void FullLocaleIdentityAndEnglishFallbackAreStable(string locale)
    {
        var localization = new LocalizationService();
        localization.InstallPack(ControlledTranslations.Pack(locale));
        localization.SetLanguage(locale);
        Assert.Equal(locale, localization.Language);
        Assert.Equal("translated title", localization["LanguageTitle"]);
        localization.SetLanguage("en");
        Assert.Equal("Interface language", localization["LanguageTitle"]);
    }
}

/// <summary>Records explicit requests and controls download completion without arbitrary timing sleeps.</summary>
internal sealed class ControlledTranslations : ITranslationPackStore
{
    private readonly Dictionary<string, TranslationPack> cache = new(StringComparer.Ordinal);
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<TranslationPack> Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Downloads { get; private set; }
    public bool CacheReadFailure { get; set; }

    /// <summary>Returns only already completed controlled downloads.</summary>
    public Task<TranslationPack?> LoadCachedAsync(string locale, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (CacheReadFailure)
        {
            throw new TranslationPackException(TranslationErrorCode.InvalidPack, "controlled cache corruption");
        }

        return Task.FromResult(cache.GetValueOrDefault(locale));
    }

    /// <summary>Allows the test to correlate loading, cancellation and the final immutable value.</summary>
    public async Task<TranslationPack> DownloadAsync(string locale, IProgress<TranslationDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Downloads++;
        Started.TrySetResult();
        progress?.Report(new(10, 100));
        var pack = await Complete.Task.WaitAsync(cancellationToken);
        cache[locale] = pack;
        return pack;
    }

    /// <summary>Creates controlled translated values, not evidence of a real downloaded or linguistically reviewed pack.</summary>
    public static TranslationPack Pack(string locale) => new(locale, LocalizationService.EnglishMessages.ToImmutableDictionary()
        .SetItem("LanguageTitle", locale == "ru" ? "Язык интерфейса" : "translated title"));
}
