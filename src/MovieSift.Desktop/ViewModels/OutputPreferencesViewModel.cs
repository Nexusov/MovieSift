using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using MovieSift.Application;
using MovieSift.Application.Ports;
using MovieSift.Core.Planning;
using MovieSift.Desktop.Localization;
using Serilog;

namespace MovieSift.Desktop.ViewModels;

/// <summary>Remembers only an output choice; session edits and explicit approval stay with the existing batch owner.</summary>
public sealed class OutputPreferencesViewModel(IOutputPreferencesStore? store, LocalizationService localization,
    Func<bool> canEdit, Func<OutputMode> currentMode, ILogger log) : ObservableObject
{
    private bool rememberChoice;
    private bool hasError;
    private long revision;
    private int pendingWrites;
    private Task writes = Task.CompletedTask;
    public OutputMode DefaultMode { get; private set; } = OutputMode.Copy;
    public bool HasError => hasError;
    public string ErrorText => localization["OutputPreferenceFailed"];
    public bool IsSaving => pendingWrites != 0;

    public bool RememberChoice
    {
        get => rememberChoice;
        set
        {
            if (!canEdit() || !SetProperty(ref rememberChoice, value))
            {
                return;
            }

            SaveCurrent();
        }
    }

    /// <summary>Loads a default without changing any restored row, inspection, consent or approval.</summary>
    public async Task InitializeAsync()
    {
        if (store is null)
        {
            return;
        }

        var expectedRevision = revision;

        try
        {
            var preference = await store.LoadAsync();

            if (revision != expectedRevision)
            {
                return;
            }

            rememberChoice = preference.Remember;
            DefaultMode = preference.Remember ? preference.Mode : OutputMode.Copy;
            OnPropertyChanged(nameof(RememberChoice));
        }
        catch (Exception exception) when (IsPreferenceFailure(exception))
        {
            ReportFailure(exception);
        }
    }

    /// <summary>Queues immutable preference snapshots in user-edit order, never fire-and-forget media authority.</summary>
    public void SaveCurrent()
    {
        revision++;
        DefaultMode = RememberChoice ? currentMode() : OutputMode.Copy;

        if (store is null)
        {
            return;
        }

        var preference = new OutputPreference(RememberChoice, currentMode());
        pendingWrites++;
        OnPropertyChanged(nameof(IsSaving));
        writes = SaveAfterAsync(writes, preference);
    }

    /// <summary>Allows shutdown and update installation to wait for the last complete portable preference write.</summary>
    public Task WhenIdleAsync() => writes;

    /// <summary>Serializes storage calls while reporting failures without blocking otherwise safe manual media work.</summary>
    private async Task SaveAfterAsync(Task previous, OutputPreference preference)
    {
        await previous;

        try
        {
            await store!.SaveAsync(preference);
            hasError = false;
        }
        catch (Exception exception) when (IsPreferenceFailure(exception))
        {
            ReportFailure(exception);
        }
        finally
        {
            pendingWrites--;
            OnPropertyChanged(nameof(IsSaving));
            OnPropertyChanged(nameof(HasError));
        }
    }

    /// <summary>Keeps preference diagnostics separate from ownership and source-publication failures.</summary>
    private void ReportFailure(Exception exception)
    {
        hasError = true;
        log.Warning(exception, "The portable output preference could not be saved or restored");
        OnPropertyChanged(nameof(HasError));
    }

    private static bool IsPreferenceFailure(Exception exception) => exception is
        IOException or UnauthorizedAccessException or JsonException or OperationFailureException;
}
