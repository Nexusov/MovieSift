using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MovieSift.Application.Presets;

namespace MovieSift.Desktop.ViewModels;

/// <summary>Manages a portable catalog without owning media state or implicitly loading a selected preset.</summary>
public sealed partial class RulePresetsViewModel : ObservableObject
{
    private readonly RulePresetsOptions options;
    private RulePresetFailure? failure;
    private ImmutableArray<RulePresetFailure> cleanupIssues = [];
    private string? statusKey;
    private bool catalogAvailable;
    private bool isBusy;
    private long selectionRevision;
    private Guid? deletionId;
    private string deletionName = string.Empty;
    private TaskCompletionSource? idle;

    [ObservableProperty]
    private RulePreset? selectedPreset;

    [ObservableProperty]
    private string presetName = string.Empty;

    /// <summary>Connects explicit asynchronous catalog commands to the existing editor and workspace gate.</summary>
    public RulePresetsViewModel(RulePresetsOptions options)
    {
        this.options = options;
        RefreshCatalogCommand = new AsyncRelayCommand(RefreshCatalogAsync, () => CanRead);
        SaveAsNewCommand = new AsyncRelayCommand(SaveAsNewAsync, () => CanWrite);
        LoadCommand = new AsyncRelayCommand(LoadAsync, () => CanLoad);
        RenameCommand = new AsyncRelayCommand(RenameAsync, () => CanWrite && SelectedPreset is not null);
        DeleteCommand = new RelayCommand(RequestDelete, () => CanWrite && SelectedPreset is not null);
        ConfirmDeleteCommand = new AsyncRelayCommand(ConfirmDeleteAsync, () => CanConfirmDelete);
        KeepPresetCommand = new RelayCommand(KeepPreset, () => HasDeleteConfirmation && !IsBusy);
        options.Localization.PropertyChanged += (_, _) => Refresh();
    }

    public Localization.LocalizationService Localization => options.Localization;
    public ObservableCollection<RulePreset> Presets { get; } = [];
    public bool IsBusy => isBusy;
    public bool HasError => failure is not null || !cleanupIssues.IsEmpty;
    public bool HasDeleteConfirmation => deletionId is not null;
    public bool CanSelect => CanRead;
    public bool CanRead => !IsBusy && !HasDeleteConfirmation && options.CanEdit();
    public bool CanWrite => CanRead && catalogAvailable && options.CanWrite();
    public bool CanLoad => CanRead && catalogAvailable && SelectedPreset is not null;
    private bool CanConfirmDelete => HasDeleteConfirmation && !IsBusy && options.CanEdit() && options.CanWrite() &&
        SelectedPreset?.Id == deletionId;
    public string StatusText => IsBusy ? Localization["PresetWorking"] : statusKey is null ? string.Empty : Localization[statusKey];
    public string DeleteConfirmationText => Localization.Format(new("PresetDeleteConfirmation", [deletionName]));
    public string ErrorText => failure is { } current ? Localization[$"PresetError{current.Code}"] :
        cleanupIssues.IsEmpty ? string.Empty : Localization["PresetCleanupIssue"];
    public string ErrorDetails => string.Join(Environment.NewLine,
        new[] { failure?.Detail }.Where(detail => !string.IsNullOrWhiteSpace(detail))
            .Concat((failure is { RuleIssues.IsDefault: false } current ? current.RuleIssues : [])
                .Select(issue => $"{Localization[issue.Category.ToString()]}: {Localization[$"PresetIssue{issue.Reason}"]}"))
            .Concat(cleanupIssues.Select(issue => issue.Detail)));
    public bool HasErrorDetails => !string.IsNullOrWhiteSpace(ErrorDetails);

    public IAsyncRelayCommand RefreshCatalogCommand { get; }
    public IAsyncRelayCommand SaveAsNewCommand { get; }
    public IAsyncRelayCommand LoadCommand { get; }
    public IAsyncRelayCommand RenameCommand { get; }
    public IRelayCommand DeleteCommand { get; }
    public IAsyncRelayCommand ConfirmDeleteCommand { get; }
    public IRelayCommand KeepPresetCommand { get; }

    /// <summary>Restores catalog entries only; startup deliberately selects and loads no preset.</summary>
    public Task InitializeAsync() => RefreshCatalogAsync();

    /// <summary>Lets normal window shutdown await the current finite persistence operation.</summary>
    public Task WhenIdleAsync() => idle?.Task ?? Task.CompletedTask;

    /// <summary>Refreshes translated presentation and shared-workspace command availability.</summary>
    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
        RefreshCatalogCommand.NotifyCanExecuteChanged();
        SaveAsNewCommand.NotifyCanExecuteChanged();
        LoadCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        ConfirmDeleteCommand.NotifyCanExecuteChanged();
        KeepPresetCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Selection is deliberately independent of loading, including while an old request completes.</summary>
    partial void OnSelectedPresetChanged(RulePreset? value)
    {
        selectionRevision++;
        Refresh();
    }

    /// <summary>Reads a complete catalog without replacing an unreadable file or changing editor values.</summary>
    private Task RefreshCatalogAsync() => PerformAsync(async () =>
    {
        var result = await options.Service.ReadAsync();
        AcceptCatalog(result, reading: true);
    });

    /// <summary>Persists one immutable editor snapshot and leaves its preview, approvals and current selection intact.</summary>
    private Task SaveAsNewAsync()
    {
        if (!CanWrite)
        {
            return Task.CompletedTask;
        }

        var snapshot = options.Editor.CaptureConfiguration();
        var name = PresetName;
        return PerformAsync(async () => AcceptCatalog(await options.Service.SaveAsNewAsync(new(name, snapshot)), "PresetSaved"));
    }

    /// <summary>Loads fully validated values only when the captured selection, editor and batch are still current.</summary>
    private Task LoadAsync()
    {
        if (!CanLoad || SelectedPreset is not { } selected)
        {
            return Task.CompletedTask;
        }

        var selectedRevision = selectionRevision;
        var editorRevision = options.Editor.ConfigurationRevision;
        var batchRevision = options.Session.Snapshot.Revision;
        return PerformAsync(async () =>
        {
            var result = await options.Service.LoadAsync(new(selected.Id));
            failure = result.Failure;
            RejectUnsafeCatalog(result.Failure, reading: true);

            if (result.Preset is not { } preset)
            {
                return;
            }

            var requestIsCurrent = selectedRevision == selectionRevision && editorRevision == options.Editor.ConfigurationRevision &&
                batchRevision == options.Session.Snapshot.Revision && SelectedPreset?.Id == selected.Id && options.CanEdit();

            if (!requestIsCurrent)
            {
                statusKey = "PresetLoadStale";
                return;
            }

            var changed = options.Editor.LoadConfiguration(preset.Rules);
            statusKey = changed ? "PresetLoaded" : "PresetAlreadyLoaded";
        });
    }

    /// <summary>Renames the selected stable identifier without loading or linking its configuration.</summary>
    private Task RenameAsync()
    {
        if (!CanWrite || SelectedPreset is not { } selected)
        {
            return Task.CompletedTask;
        }

        var name = PresetName;
        return PerformAsync(async () => AcceptCatalog(await options.Service.RenameAsync(new(selected.Id, name)), "PresetRenamed"));
    }

    /// <summary>Requests localized confirmation for exactly one selected catalog entry.</summary>
    private void RequestDelete()
    {
        if (!CanWrite || SelectedPreset is not { } selected)
        {
            return;
        }

        deletionId = selected.Id;
        deletionName = selected.Name;
        Refresh();
    }

    /// <summary>Declines deletion without editing the catalog, selected preset or workflow.</summary>
    private void KeepPreset()
    {
        deletionId = null;
        deletionName = string.Empty;
        Refresh();
    }

    /// <summary>Deletes only the confirmed identity and leaves even previously loaded editor values in place.</summary>
    private Task ConfirmDeleteAsync()
    {
        if (!CanConfirmDelete)
        {
            return Task.CompletedTask;
        }

        var identifier = deletionId!.Value;
        deletionId = null;
        deletionName = string.Empty;
        return PerformAsync(async () => AcceptCatalog(await options.Service.DeleteAsync(new(identifier)), "PresetDeleted"));
    }

    /// <summary>Retains a safe catalog on a failed mutation and never silently substitutes an empty list for invalid data.</summary>
    private void AcceptCatalog(RulePresetCatalogResult result, string? successKey = null, bool reading = false)
    {
        failure = result.Failure;

        if (result.Catalog is { } catalog)
        {
            var selectedIdentifier = SelectedPreset?.Id;
            Presets.Clear();

            foreach (var preset in catalog.Items)
            {
                Presets.Add(preset);
            }

            SelectedPreset = Presets.FirstOrDefault(preset => preset.Id == selectedIdentifier);
            catalogAvailable = true;
            statusKey = result.Failure is null ? successKey : null;
        }

        RejectUnsafeCatalog(result.Failure, reading);
        cleanupIssues = result.CleanupIssues.IsDefault ? [] : result.CleanupIssues;

        if (!result.CleanupIssues.IsDefaultOrEmpty)
        {
            statusKey = result.Committed ? "PresetCommittedCleanupIssue" : statusKey;
        }
    }

    /// <summary>Requires a successful reread after unsafe source data without blocking manual rule editing.</summary>
    private void RejectUnsafeCatalog(RulePresetFailure? current, bool reading)
    {
        var unsafeCatalog = current?.Code is RulePresetErrorCode.InvalidCatalog or RulePresetErrorCode.UnsupportedVersion or
            RulePresetErrorCode.PersistenceUnavailable || reading && current?.Code == RulePresetErrorCode.LimitExceeded;

        if (unsafeCatalog)
        {
            catalogAvailable = false;
        }
    }

    /// <summary>Serializes local commands and exposes a finite awaitable operation to window shutdown.</summary>
    private async Task PerformAsync(Func<Task> operation)
    {
        if (!CanRead)
        {
            return;
        }

        idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        isBusy = true;
        failure = null;
        cleanupIssues = [];
        statusKey = null;
        Refresh();
        options.ActivityChanged();

        try
        {
            await operation();
        }
        finally
        {
            isBusy = false;
            idle.TrySetResult();
            Refresh();
            options.ActivityChanged();
        }
    }
}
