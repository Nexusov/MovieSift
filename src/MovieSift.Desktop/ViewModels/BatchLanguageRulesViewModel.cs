using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MovieSift.Application.Batches;
using MovieSift.Core.Failures;
using MovieSift.Core.Rules;

namespace MovieSift.Desktop.ViewModels;

/// <summary>Edits language, title and subtitle forced-flag constraints with revision-bound per-file proposals.</summary>
public sealed partial class BatchLanguageRulesViewModel : ObservableObject
{
    private readonly BatchLanguageRulesOptions options;
    private BatchLanguagePreview? preview;
    private Failure? failure;
    private bool applying;
    private bool applied;
    private bool stale;
    private int changedCount;
    private long configurationRevision;
    private bool restoringConfiguration;
    private ImmutableArray<string> loadedAudioLanguages;
    private ImmutableArray<string> loadedSubtitleLanguages;

    [ObservableProperty]
    private bool audioRuleEnabled;

    [ObservableProperty]
    private bool subtitleRuleEnabled;

    [ObservableProperty]
    private string audioLanguages = string.Empty;

    [ObservableProperty]
    private string subtitleLanguages = string.Empty;

    [ObservableProperty]
    private bool audioTitleFilterEnabled;

    [ObservableProperty]
    private bool subtitleTitleFilterEnabled;

    [ObservableProperty]
    private string audioTitleContains = string.Empty;

    [ObservableProperty]
    private string subtitleTitleContains = string.Empty;

    [ObservableProperty]
    private bool keepOnlyForcedSubtitles;

    /// <summary>Uses the existing batch owner and workspace availability without starting a media operation.</summary>
    public BatchLanguageRulesViewModel(BatchLanguageRulesOptions options)
    {
        this.options = options;
        PreviewCommand = new RelayCommand(PreviewChoices, () => CanConfigure && !options.Session.Snapshot.Items.IsEmpty);
        ApplyCommand = new RelayCommand(ApplyChoices, () => CanApply);
        DismissCommand = new RelayCommand(ClearPreview, () => HasPreview || HasError || stale);
        options.Session.Changed += OnSessionChanged;
        options.Localization.PropertyChanged += (_, _) => Refresh();
    }

    public Localization.LocalizationService Localization => options.Localization;
    public ObservableCollection<LanguageRuleFilePreviewViewModel> Files { get; } = [];
    public BatchLanguagePreview? Preview => preview;
    public long ConfigurationRevision => configurationRevision;
    public bool CanConfigure => options.CanEdit();
    public bool CanEditAudioLanguages => CanConfigure && AudioRuleEnabled;
    public bool CanEditSubtitleLanguages => CanConfigure && SubtitleRuleEnabled;
    public bool CanEditAudioTitle => CanEditAudioLanguages && AudioTitleFilterEnabled;
    public bool CanEditSubtitleTitle => CanEditSubtitleLanguages && SubtitleTitleFilterEnabled;
    public bool HasPreview => preview is not null;
    public bool HasError => failure is not null;
    public bool IsApplied => applied;
    public bool IsStale => stale;
    public string ErrorText => failure is { } current ? Localization[$"Error{current.Code}"] : string.Empty;
    public string StatusText => stale ? Localization["RulePreviewStale"] : applied
        ? Localization.Format(new("RuleAppliedSummary", [changedCount, ManualReviewCount]))
        : preview is not null ? Localization.Format(new("RulePreviewSummary", [Files.Count, Files.Count(file => file.Item.State == BatchLanguagePreviewState.Changed), ManualReviewCount]))
        : string.Empty;
    private int ManualReviewCount => Files.Count(file => file.Item.State is BatchLanguagePreviewState.ManualReview or
        BatchLanguagePreviewState.PendingInspection or BatchLanguagePreviewState.Failed or BatchLanguagePreviewState.Cancelled);
    private bool CanApply => CanConfigure && preview is not null && !applied && preview.Revision == options.Session.Snapshot.Revision &&
        preview.Items.Any(item => item.State is BatchLanguagePreviewState.Changed or BatchLanguagePreviewState.Unchanged);

    public IRelayCommand PreviewCommand { get; }
    public IRelayCommand ApplyCommand { get; }
    public IRelayCommand DismissCommand { get; }

    /// <summary>Clears transient proposals while retaining the user's in-memory rule configuration.</summary>
    public void ClearPreview()
    {
        preview = null;
        failure = null;
        applied = false;
        stale = false;
        changedCount = 0;
        Files.Clear();
        Refresh();
    }

    /// <summary>Updates command availability when another workflow owns the workspace.</summary>
    public void Refresh()
    {
        foreach (var file in Files)
        {
            file.Refresh();
        }

        OnPropertyChanged(string.Empty);
        PreviewCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
        DismissCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Discards a proposal when its audio mode changes, without editing the batch.</summary>
    partial void OnAudioRuleEnabledChanged(bool value) => ConfigurationEdited();

    /// <summary>Discards a proposal when its subtitle mode changes, without editing the batch.</summary>
    partial void OnSubtitleRuleEnabledChanged(bool value) => ConfigurationEdited();

    /// <summary>Requires a new preview after an explicit audio language edit.</summary>
    partial void OnAudioLanguagesChanged(string value)
    {
        if (!restoringConfiguration)
        {
            loadedAudioLanguages = default;
        }

        ConfigurationEdited();
    }

    /// <summary>Requires a new preview after an explicit subtitle language edit.</summary>
    partial void OnSubtitleLanguagesChanged(string value)
    {
        if (!restoringConfiguration)
        {
            loadedSubtitleLanguages = default;
        }

        ConfigurationEdited();
    }

    /// <summary>Requires a new preview after changing the optional audio title constraint.</summary>
    partial void OnAudioTitleFilterEnabledChanged(bool value) => ConfigurationEdited();

    /// <summary>Requires a new preview after changing the optional subtitle title constraint.</summary>
    partial void OnSubtitleTitleFilterEnabledChanged(bool value) => ConfigurationEdited();

    /// <summary>Discards a proposal after an audio title edit without changing any Keep choice.</summary>
    partial void OnAudioTitleContainsChanged(string value) => ConfigurationEdited();

    /// <summary>Discards a proposal after a subtitle title edit without changing any Keep choice.</summary>
    partial void OnSubtitleTitleContainsChanged(string value) => ConfigurationEdited();

    /// <summary>Requires explicit preview after a forced-flag edit without changing selections or approvals.</summary>
    partial void OnKeepOnlyForcedSubtitlesChanged(bool value) => ConfigurationEdited();

    /// <summary>Captures all active and inactive editor values without normalizing stored language aliases or title text.</summary>
    public LanguageRules CaptureConfiguration() => new(
        new(AudioRuleEnabled ? LanguageRuleMode.KeepMatching : LanguageRuleMode.Unchanged,
            loadedAudioLanguages.IsDefault ? ParseLanguages(AudioLanguages) : loadedAudioLanguages,
            new(AudioTitleFilterEnabled, AudioTitleContains)),
        new(SubtitleRuleEnabled ? LanguageRuleMode.KeepMatching : LanguageRuleMode.Unchanged,
            loadedSubtitleLanguages.IsDefault ? ParseLanguages(SubtitleLanguages) : loadedSubtitleLanguages,
            new(SubtitleTitleFilterEnabled, SubtitleTitleContains)), KeepOnlyForcedSubtitles);

    /// <summary>Restores only configuration; an identical load preserves its existing issued proposal.</summary>
    public bool LoadConfiguration(LanguageRules configuration)
    {
        if (LanguageRuleConfiguration.ContentEquals(CaptureConfiguration(), configuration))
        {
            return false;
        }

        restoringConfiguration = true;

        try
        {
            AudioRuleEnabled = configuration.Audio.Mode == LanguageRuleMode.KeepMatching;
            SubtitleRuleEnabled = configuration.Subtitles.Mode == LanguageRuleMode.KeepMatching;
            AudioLanguages = string.Join(", ", configuration.Audio.Languages);
            SubtitleLanguages = string.Join(", ", configuration.Subtitles.Languages);
            AudioTitleFilterEnabled = configuration.Audio.TitleFilter?.Enabled ?? false;
            SubtitleTitleFilterEnabled = configuration.Subtitles.TitleFilter?.Enabled ?? false;
            AudioTitleContains = configuration.Audio.TitleFilter?.Text ?? string.Empty;
            SubtitleTitleContains = configuration.Subtitles.TitleFilter?.Text ?? string.Empty;
            KeepOnlyForcedSubtitles = configuration.KeepOnlyForcedSubtitles;
            loadedAudioLanguages = configuration.Audio.Languages;
            loadedSubtitleLanguages = configuration.Subtitles.Languages;
        }
        finally
        {
            restoringConfiguration = false;
        }

        ConfigurationEdited();
        return true;
    }

    /// <summary>Invalidates only transient rule presentation after one coherent configuration edit.</summary>
    private void ConfigurationEdited()
    {
        if (restoringConfiguration)
        {
            return;
        }

        configurationRevision++;
        ClearPreview();
    }

    /// <summary>Computes proposals from immutable live selections without invalidating any approval or review.</summary>
    private void PreviewChoices()
    {
        if (!CanConfigure)
        {
            return;
        }

        var result = options.Coordinator.PreviewLanguageRules(CaptureConfiguration());
        failure = result.Failure;
        preview = result.Preview;
        applied = false;
        stale = false;
        changedCount = 0;
        RebuildFiles();
    }

    /// <summary>Applies only the explicitly displayed, still-current proposal through Application.</summary>
    private void ApplyChoices()
    {
        if (!CanConfigure || preview is not { } current || applied)
        {
            return;
        }

        applying = true;

        try
        {
            var result = options.Coordinator.ApplyLanguageRules(current);
            failure = result.Failure;
            applied = result.Applied;
            changedCount = result.ChangedItemIds.Length;

            if (!result.ChangedItemIds.IsEmpty)
            {
                options.ChoicesChanged();
            }

            if (result.Failure?.Code == ErrorCode.StalePlan)
            {
                preview = null;
                stale = true;
            }
        }
        finally
        {
            applying = false;
            RebuildFiles();
        }
    }

    /// <summary>Re-reads the current revision so delayed notifications cannot restore a previous batch's preview.</summary>
    private void OnSessionChanged(BatchSnapshot snapshot)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnSessionChanged(options.Session.Snapshot));
            return;
        }

        if (applying || preview is null || preview.Revision == options.Session.Snapshot.Revision)
        {
            return;
        }

        var hasItems = !options.Session.Snapshot.Items.IsEmpty;
        ClearPreview();
        stale = hasItems;
        Refresh();
    }

    /// <summary>Rebuilds presentation copies while preserving the actual immutable proposal and metadata.</summary>
    private void RebuildFiles()
    {
        Files.Clear();

        foreach (var item in preview?.Items ?? [])
        {
            Files.Add(new(item, Localization, applied));
        }

        Refresh();
    }

    /// <summary>Splits an explicit comma-separated list; Core validates and canonicalizes matching copies.</summary>
    private static ImmutableArray<string> ParseLanguages(string languages) =>
        languages.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToImmutableArray();
}
