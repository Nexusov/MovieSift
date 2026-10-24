using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrackTrim.Application.Appearance;
using TrackTrim.Desktop.Localization;

namespace TrackTrim.Desktop.Appearance;

/// <summary>Edits live cosmetic preferences, rejecting invalid palettes and coalescing bounded asynchronous saves.</summary>
public sealed class AppearanceViewModel : ObservableObject
{
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(250);
    private readonly AppearanceViewModelOptions options;
    private AppearanceSettings current = AppearanceSettings.Default;
    private AppearanceSettings? pending;
    private Task loading = Task.CompletedTask;
    private Task saving = Task.CompletedTask;
    private bool initialized;
    private bool catalogReadable = true;
    private bool resetPending;
    private bool savingActive;
    private long revision;
    private long committedResetRevision;
    private string? inputError;
    private string? storageError;
    private string? diagnostic;
    private string accentHex = AppearanceSettings.Default.AccentHex;
    private int themeIndex;
    private int motionIndex;
    private string lightPage = string.Empty, lightSurface = string.Empty, lightText = string.Empty, lightMuted = string.Empty, lightBorder = string.Empty;
    private string darkPage = string.Empty, darkSurface = string.Empty, darkText = string.Empty, darkMuted = string.Empty, darkBorder = string.Empty;

    /// <summary>Starts with usable built-ins and no automatic load of any media or rule configuration.</summary>
    public AppearanceViewModel(AppearanceViewModelOptions options)
    {
        this.options = options;
        ResetCommand = new AsyncRelayCommand(ResetAsync);
        SetAccentCommand = new RelayCommand<string>(SetAccent);
        ThemeOptions = [new(Localization, "AppearanceSystem"), new(Localization, "AppearanceLight"), new(Localization, "AppearanceDark")];
        MotionOptions = [new(Localization, "AppearanceSystem"), new(Localization, "AppearanceReduced"), new(Localization, "AppearanceFullMotion")];
        options.Localization.PropertyChanged += (_, _) =>
        {
            foreach (var option in ThemeOptions.Concat(MotionOptions))
            {
                option.Refresh();
            }

            Refresh();
        };
        options.Applier.Apply(current, AppearancePalettePolicy.Resolve(current));
    }

    public LocalizationService Localization => options.Localization;
    public AppearanceSettings Current => current;
    public bool HasPendingSave => !loading.IsCompleted || savingActive;
    public bool HasError => inputError is not null || storageError is not null;
    public string ErrorText => inputError is { } invalid ? Localization[invalid] : storageError is { } unavailable ? Localization[unavailable] : string.Empty;
    public string Diagnostics => diagnostic ?? string.Empty;
    public bool HasDiagnostics => !string.IsNullOrEmpty(diagnostic);
    public string MotionNotice => Localization[AppearanceMotionPolicy.SystemPreferenceAvailable ? "AppearanceMotionSystemAvailable" : "AppearanceMotionSystemFallback"];
    public IReadOnlyList<AppearanceOptionViewModel> ThemeOptions { get; }
    public IReadOnlyList<AppearanceOptionViewModel> MotionOptions { get; }
    public IAsyncRelayCommand ResetCommand { get; }
    public IRelayCommand<string> SetAccentCommand { get; }
    public int ThemeIndex
    {
        get => themeIndex;
        set
        {
            if (!Enum.IsDefined((AppearanceTheme)value))
            {
                OnPropertyChanged();
                return;
            }

            Edit(ref themeIndex, value);
        }
    }
    public int MotionIndex
    {
        get => motionIndex;
        set
        {
            if (!Enum.IsDefined((AppearanceMotion)value))
            {
                OnPropertyChanged();
                return;
            }

            Edit(ref motionIndex, value);
        }
    }
    public string AccentHex { get => accentHex; set => Edit(ref accentHex, value); }
    public Color AccentColor
    {
        get => AppearancePalettePolicy.TryColor(accentHex, out var color) ? color : Color.Parse(current.AccentHex);
        set => AccentHex = AppearancePalettePolicy.ToHex(value);
    }
    public string LightPage { get => lightPage; set => Edit(ref lightPage, value); }
    public string LightSurface { get => lightSurface; set => Edit(ref lightSurface, value); }
    public string LightText { get => lightText; set => Edit(ref lightText, value); }
    public string LightMuted { get => lightMuted; set => Edit(ref lightMuted, value); }
    public string LightBorder { get => lightBorder; set => Edit(ref lightBorder, value); }
    public string DarkPage { get => darkPage; set => Edit(ref darkPage, value); }
    public string DarkSurface { get => darkSurface; set => Edit(ref darkSurface, value); }
    public string DarkText { get => darkText; set => Edit(ref darkText, value); }
    public string DarkMuted { get => darkMuted; set => Edit(ref darkMuted, value); }
    public string DarkBorder { get => darkBorder; set => Edit(ref darkBorder, value); }

    /// <summary>Reads once and rejects delayed startup completion after a newer user edit.</summary>
    public Task InitializeAsync()
    {
        if (initialized || !loading.IsCompleted)
        {
            return loading;
        }

        loading = LoadAsync();
        return loading;
    }

    /// <summary>Lets normal shutdown await cosmetic writes without creating a media safety gate.</summary>
    public async Task WhenIdleAsync()
    {
        await loading;

        while (savingActive)
        {
            await saving;
        }
    }

    /// <summary>Updates field text immediately while retaining the last fully usable configuration.</summary>
    private void Edit<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!SetProperty(ref field, value, name))
        {
            return;
        }

        var proposed = Capture();
        var palette = AppearancePalettePolicy.Resolve(proposed);
        inputError = palette.ErrorKey;

        if (palette.IsValid && SameConfiguration(current, proposed))
        {
            Refresh();
            return;
        }

        revision++;

        if (palette.IsValid)
        {
            current = proposed;
            options.Applier.Apply(current, palette);
            pending = current;
            StartSaving();
        }

        Refresh();
    }

    /// <summary>Captures both active and inactive variant fields without touching rules or media selections.</summary>
    private AppearanceSettings Capture() => new((AppearanceTheme)themeIndex, accentHex,
        new(Nullable(lightPage), Nullable(lightSurface), Nullable(lightText), Nullable(lightMuted), Nullable(lightBorder)),
        new(Nullable(darkPage), Nullable(darkSurface), Nullable(darkText), Nullable(darkMuted), Nullable(darkBorder)), (AppearanceMotion)motionIndex);

    /// <summary>Uses blank fields as an explicit request for the built-in variant value.</summary>
    private static string? Nullable(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Handles corrupt storage visibly and never automatically overwrites user-authored problematic data.</summary>
    private async Task LoadAsync()
    {
        var capturedRevision = revision;
        var capturedResetRevision = committedResetRevision;

        try
        {
            var result = await options.Store.LoadAsync();

            if (capturedResetRevision != committedResetRevision)
            {
                return;
            }

            catalogReadable = result.Failure is null;

            if (!catalogReadable)
            {
                storageError = "AppearanceCatalogError";
                diagnostic = result.Failure?.Detail;
                return;
            }

            if (capturedRevision != revision || capturedRevision != 0)
            {
                return;
            }

            var loaded = result.Settings ?? AppearanceSettings.Default;
            var palette = AppearancePalettePolicy.Resolve(loaded);

            if (!palette.IsValid)
            {
                catalogReadable = false;
                storageError = "AppearanceCatalogError";
                return;
            }

            Accept(loaded, palette);
        }
        catch (Exception exception)
        {
            if (capturedResetRevision != committedResetRevision)
            {
                return;
            }

            catalogReadable = false;
            storageError = "AppearanceSessionOnly";
            diagnostic = exception.Message;
        }
        finally
        {
            initialized = true;
            StartSaving();
            Refresh();
        }
    }

    /// <summary>Compares effective cosmetic values so picker writeback and null/empty overrides remain true no-ops.</summary>
    private static bool SameConfiguration(AppearanceSettings first, AppearanceSettings second) =>
        first.Theme == second.Theme && first.Motion == second.Motion && SameColor(first.AccentHex, second.AccentHex) &&
        SameOverrides(first.LightOverrides, second.LightOverrides) && SameOverrides(first.DarkOverrides, second.DarkOverrides);

    /// <summary>Treats absent variant fields alike without erasing stored non-default custom values.</summary>
    private static bool SameOverrides(AppearancePaletteOverrides? first, AppearancePaletteOverrides? second) =>
        SameColor(first?.Page, second?.Page) && SameColor(first?.Surface, second?.Surface) &&
        SameColor(first?.PrimaryText, second?.PrimaryText) && SameColor(first?.SecondaryText, second?.SecondaryText) &&
        SameColor(first?.Border, second?.Border);

    /// <summary>Ignores only hexadecimal letter case, which does not change an opaque color.</summary>
    private static bool SameColor(string? first, string? second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    /// <summary>Schedules one finite save loop rather than a write for each picker movement.</summary>
    private void StartSaving()
    {
        if (!initialized || savingActive || pending is null || !catalogReadable && !resetPending)
        {
            return;
        }

        savingActive = true;
        saving = SavePendingAsync();
    }

    /// <summary>Persists the latest stable edit and retains session appearance after write failure.</summary>
    private async Task SavePendingAsync()
    {
        try
        {
            while (pending is not null)
            {
                var capturedRevision = revision;
                await Task.Delay(SaveDebounce);

                if (capturedRevision != revision)
                {
                    continue;
                }

                if (pending is not { } captured)
                {
                    continue;
                }

                pending = null;
                var replaceInvalid = resetPending;
                resetPending = false;
                var result = await options.Store.SaveAsync(new(captured, ReplaceInvalidData: replaceInvalid));

                if (replaceInvalid && result.Saved)
                {
                    committedResetRevision++;
                    catalogReadable = true;
                    storageError = result.CleanupFailure is null ? null : "AppearanceSessionOnly";
                    diagnostic = result.CleanupFailure?.Detail;
                }

                if (capturedRevision != revision)
                {
                    continue;
                }

                storageError = result.Saved && result.CleanupFailure is null ? null : "AppearanceSessionOnly";
                diagnostic = result.Failure?.Detail ?? result.CleanupFailure?.Detail;
                catalogReadable = result.Saved || catalogReadable;
            }
        }
        catch (Exception exception)
        {
            storageError = "AppearanceSessionOnly";
            diagnostic = exception.Message;
        }
        finally
        {
            savingActive = false;
            StartSaving();
            Refresh();
        }
    }

    /// <summary>Resets only appearance; explicit reset may replace an unusable appearance record.</summary>
    private async Task ResetAsync()
    {
        revision++;
        Accept(AppearanceSettings.Default, AppearancePalettePolicy.Resolve(AppearanceSettings.Default));
        resetPending = true;
        pending = current;
        initialized = true;
        StartSaving();
        Refresh();
        await WhenIdleAsync();
    }

    /// <summary>Accepts one complete validated configuration without firing another edit or save.</summary>
    private void Accept(AppearanceSettings settings, AppearancePaletteResult palettes)
    {
        current = settings;
        accentHex = settings.AccentHex;
        themeIndex = (int)settings.Theme;
        motionIndex = (int)settings.Motion;
        lightPage = settings.LightOverrides?.Page ?? string.Empty;
        lightSurface = settings.LightOverrides?.Surface ?? string.Empty;
        lightText = settings.LightOverrides?.PrimaryText ?? string.Empty;
        lightMuted = settings.LightOverrides?.SecondaryText ?? string.Empty;
        lightBorder = settings.LightOverrides?.Border ?? string.Empty;
        darkPage = settings.DarkOverrides?.Page ?? string.Empty;
        darkSurface = settings.DarkOverrides?.Surface ?? string.Empty;
        darkText = settings.DarkOverrides?.PrimaryText ?? string.Empty;
        darkMuted = settings.DarkOverrides?.SecondaryText ?? string.Empty;
        darkBorder = settings.DarkOverrides?.Border ?? string.Empty;
        inputError = null;
        options.Applier.Apply(settings, palettes);
        Refresh();
    }

    /// <summary>Applies a curated accent through the same validation path as HEX and the color picker.</summary>
    private void SetAccent(string? value)
    {
        if (value is not null)
        {
            AccentHex = value;
        }
    }

    /// <summary>Refreshes only cosmetic bindings, including the alpha-free picker projection.</summary>
    private void Refresh() => OnPropertyChanged(string.Empty);
}
