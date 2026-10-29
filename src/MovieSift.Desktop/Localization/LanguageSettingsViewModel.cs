using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MovieSift.Application.Localization;

namespace MovieSift.Desktop.Localization;

/// <summary>Downloads and applies explicit cosmetic language choices independently of the media owner.</summary>
public sealed partial class LanguageSettingsViewModel : ObservableObject
{
    private readonly LanguageSettingsOptions options;
    private readonly HashSet<string> installed = new(StringComparer.Ordinal) { "en" };
    private readonly IReadOnlyList<LanguageChoiceViewModel> choices;
    private CancellationTokenSource? cancellation;
    private Task operation = Task.CompletedTask;
    private bool available = true;
    private long revision;
    private string? errorKey;
    private string? requestedLocale;

    [ObservableProperty] private string searchText = string.Empty;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isDownloading;
    [ObservableProperty] private bool isSynchronizing;
    [ObservableProperty] private double progressValue;
    [ObservableProperty] private bool isIndeterminate;

    /// <summary>Creates the fixed catalog without networking or automatically changing language.</summary>
    public LanguageSettingsViewModel(LanguageSettingsOptions options)
    {
        this.options = options;
        choices = TranslationCatalog.Languages.Select(language => new LanguageChoiceViewModel(this, language.Code, language.NativeName)).ToArray();
        CancelCommand = new RelayCommand(() => cancellation?.Cancel(), () => IsBusy);
        RetryCommand = new AsyncRelayCommand(() => errorKey == "LanguageSyncFailed" ? StartSynchronizationAsync() : StartSelectionAsync(requestedLocale!, true), () => CanChoose && requestedLocale is not null);
        Localization.PropertyChanged += (_, _) => Refresh();
        Refresh();
    }

    public LocalizationService Localization => options.Localization;
    public ObservableCollection<LanguageChoiceViewModel> Languages { get; } = [];
    public bool CanChoose => available && !IsBusy;
    public bool HasError => errorKey is not null;
    public bool HasNoMatches => Languages.Count == 0;
    public string ErrorText => errorKey is null ? string.Empty : Localization[errorKey];
    public string DownloadText => Localization.Format(new("LanguageDownloading", [TranslationCatalog.Languages.FirstOrDefault(language => language.Code == requestedLocale)?.NativeName ?? string.Empty]));
    public IRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand RetryCommand { get; }

    /// <summary>Reports only verified local packs as available offline.</summary>
    public bool IsInstalled(string locale) => installed.Contains(locale);

    /// <summary>Shares the existing updater gate without becoming a media lifecycle owner.</summary>
    public void SetAvailability(bool value)
    {
        if (available == value)
        {
            return;
        }

        available = value;
        Refresh();
    }

    /// <summary>Restores a cached preference without issuing a startup network request.</summary>
    public async Task RestoreCachedAsync(string locale)
    {
        var requestRevision = ++revision;
        requestedLocale = TranslationCatalog.IsSupported(locale) ? locale : null;

        if (options.Store is null)
        {
            Localization.SetLanguage(locale);
            return;
        }

        try
        {
            foreach (var language in TranslationCatalog.Languages.Where(language => language.Code != "en"))
            {
                try
                {
                    var cached = await options.Store.LoadCachedAsync(language.Code);

                    if (requestRevision != revision)
                    {
                        return;
                    }

                    if (cached is not null)
                    {
                        Localization.InstallPack(cached);
                        installed.Add(language.Code);
                    }
                }
                catch (TranslationPackException)
                {
                    // Invalid unrelated cached packs remain untouched and are not advertised as installed.
                }
            }

            if (requestRevision != revision)
            {
                return;
            }

            if (installed.Contains(locale))
            {
                Localization.SetLanguage(locale);
                Refresh();
                _ = StartSynchronizationAsync();
                return;
            }
        }
        catch (TranslationPackException)
        {
            // Startup must retain a usable English interface and the original invalid cache.
        }

        if (requestRevision == revision)
        {
            Localization.SetLanguage("en");
            errorKey = "LanguageCacheMissing";
            requestedLocale = TranslationCatalog.IsSupported(locale) ? locale : null;
            Refresh();
            _ = StartSynchronizationAsync();
        }
    }

    /// <summary>Serializes user requests and retains the current language until a complete pack is validated.</summary>
    public Task SelectAsync(string locale)
        => StartSelectionAsync(locale, false);

    /// <summary>Tracks retry replacement as the same serial cosmetic operation, bypassing an invalid cache only explicitly.</summary>
    private Task StartSelectionAsync(string locale, bool forceDownload)
    {
        if (!CanChoose || !TranslationCatalog.IsSupported(locale))
        {
            return Task.CompletedTask;
        }

        operation = SelectCoreAsync(locale, forceDownload);
        return operation;
    }

    /// <summary>Drains the request and the shell preference save before the HTTP client is disposed.</summary>
    public async Task ShutdownAsync()
    {
        ++revision;
        cancellation?.Cancel();
        await operation;
    }

    /// <summary>Fetches only data through the application port and applies one still-current result.</summary>
    private async Task SelectCoreAsync(string locale, bool forceDownload)
    {
        var requestRevision = ++revision;
        requestedLocale = locale;
        errorKey = null;
        cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        IsBusy = true;
        IsDownloading = false;
        ProgressValue = 0;
        IsIndeterminate = true;
        Refresh();

        try
        {
            if (locale != "en" && options.Store is null)
            {
                throw new TranslationPackException(TranslationErrorCode.Unavailable, "No translation store is configured.");
            }

            if (locale != "en" && options.Store is { } store)
            {
                var progress = new Progress<TranslationDownloadProgress>(value =>
                {
                    if (requestRevision != revision || !IsBusy)
                    {
                        return;
                    }

                    IsIndeterminate = value.TotalBytes is not > 0;
                    ProgressValue = value.TotalBytes is > 0 ? Math.Clamp(100d * value.BytesReceived / value.TotalBytes.Value, 0, 100) : 0;
                });
                var cached = forceDownload ? null : await store.LoadCachedAsync(locale, token);
                if (cached is null)
                {
                    IsDownloading = true;
                }

                var pack = cached ?? await store.DownloadAsync(locale, progress, token);
                token.ThrowIfCancellationRequested();

                if (requestRevision != revision)
                {
                    return;
                }

                Localization.InstallPack(pack);
                installed.Add(locale);
            }

            if (requestRevision == revision && available)
            {
                await options.ApplyLanguage(locale);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (TranslationPackException exception)
        {
            errorKey = exception.Code is TranslationErrorCode.InvalidPack or TranslationErrorCode.Incompatible or TranslationErrorCode.ResourceContractMismatch ? "LanguageInvalidPack" : "LanguageFailure";
        }
        finally
        {
            cancellation.Dispose();
            cancellation = null;
            IsBusy = false;
            IsDownloading = false;
            Refresh();
        }
    }

    /// <summary>Tracks a retry through the same shutdown-drained operation as startup synchronization.</summary>
    private Task StartSynchronizationAsync()
    {
        operation = SynchronizeAsync();
        return operation;
    }

    /// <summary>Refreshes cached dictionaries after upgrades without showing a fake user-download progress bar.</summary>
    private async Task SynchronizeAsync()
    {
        if (options.Store is null)
        {
            return;
        }

        var requestRevision = revision;
        var initialLanguage = Localization.Language;
        cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        IsBusy = true;
        IsSynchronizing = true;
        Refresh();

        try
        {
            var packs = await options.Store.SynchronizeCachedAsync(token);

            if (requestRevision != revision || token.IsCancellationRequested)
            {
                return;
            }

            if (errorKey == "LanguageSyncFailed")
            {
                errorKey = null;
            }

            foreach (var pack in packs)
            {
                Localization.InstallPack(pack);
                installed.Add(pack.Locale);
            }

            var canRestoreMissingPreference = initialLanguage == "en" && Localization.Language == initialLanguage &&
                requestedLocale is not null && requestedLocale != initialLanguage && packs.Any(pack => pack.Locale == requestedLocale);

            if (canRestoreMissingPreference)
            {
                await options.ApplyLanguage(requestedLocale!);
                errorKey = null;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (TranslationPackException) when (requestRevision == revision)
        {
            errorKey = "LanguageSyncFailed";
        }
        catch (TranslationPackException)
        {
            // A late failure after shutdown no longer owns presentation state.
        }
        finally
        {
            cancellation.Dispose();
            cancellation = null;
            IsBusy = false;
            IsSynchronizing = false;
            Refresh();
        }
    }

    /// <summary>Filters by native name and locale without changing the current language.</summary>
    partial void OnSearchTextChanged(string value) => Refresh();

    /// <summary>Refreshes finite localized presentation while keeping row identities stable.</summary>
    private void Refresh()
    {
        var query = SearchText.Trim();
        var matching = choices.Where(choice => choice.NativeName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            choice.Code.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();

        if (!Languages.SequenceEqual(matching))
        {
            Languages.Clear();

            foreach (var choice in matching)
            {
                Languages.Add(choice);
            }
        }

        foreach (var choice in choices)
        {
            choice.Refresh();
        }

        CancelCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(string.Empty);
    }
}
