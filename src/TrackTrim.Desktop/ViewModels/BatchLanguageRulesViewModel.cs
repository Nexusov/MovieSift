using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrackTrim.Application.Batches;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Rules;

namespace TrackTrim.Desktop.ViewModels;

/// <summary>Edits language and optional literal-title rules with explicit, revision-bound per-file proposals.</summary>
public sealed partial class BatchLanguageRulesViewModel : ObservableObject
{
    private readonly BatchLanguageRulesOptions options;
    private BatchLanguagePreview? preview;
    private Failure? failure;
    private bool applying;
    private bool applied;
    private bool stale;
    private int changedCount;

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
    partial void OnAudioRuleEnabledChanged(bool value) => ClearPreview();

    /// <summary>Discards a proposal when its subtitle mode changes, without editing the batch.</summary>
    partial void OnSubtitleRuleEnabledChanged(bool value) => ClearPreview();

    /// <summary>Requires a new preview after an explicit audio language edit.</summary>
    partial void OnAudioLanguagesChanged(string value) => ClearPreview();

    /// <summary>Requires a new preview after an explicit subtitle language edit.</summary>
    partial void OnSubtitleLanguagesChanged(string value) => ClearPreview();

    /// <summary>Requires a new preview after changing the optional audio title constraint.</summary>
    partial void OnAudioTitleFilterEnabledChanged(bool value) => ClearPreview();

    /// <summary>Requires a new preview after changing the optional subtitle title constraint.</summary>
    partial void OnSubtitleTitleFilterEnabledChanged(bool value) => ClearPreview();

    /// <summary>Discards a proposal after an audio title edit without changing any Keep choice.</summary>
    partial void OnAudioTitleContainsChanged(string value) => ClearPreview();

    /// <summary>Discards a proposal after a subtitle title edit without changing any Keep choice.</summary>
    partial void OnSubtitleTitleContainsChanged(string value) => ClearPreview();

    /// <summary>Computes proposals from immutable live selections without invalidating any approval or review.</summary>
    private void PreviewChoices()
    {
        if (!CanConfigure)
        {
            return;
        }

        var audio = new TrackLanguageRule(
            AudioRuleEnabled ? LanguageRuleMode.KeepMatching : LanguageRuleMode.Unchanged,
            ParseLanguages(AudioLanguages), new(AudioTitleFilterEnabled, AudioTitleContains));
        var subtitles = new TrackLanguageRule(
            SubtitleRuleEnabled ? LanguageRuleMode.KeepMatching : LanguageRuleMode.Unchanged,
            ParseLanguages(SubtitleLanguages), new(SubtitleTitleFilterEnabled, SubtitleTitleContains));
        var rules = new LanguageRules(audio, subtitles);
        var result = options.Coordinator.PreviewLanguageRules(rules);
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
