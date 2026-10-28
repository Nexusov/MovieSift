using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MovieSift.Application.Inspection;
using MovieSift.Desktop.Localization;

namespace MovieSift.Desktop.ViewModels;

/// <summary>Shows monotonic elapsed time and actual inspection stages without inventing a remaining-time estimate.</summary>
public sealed class InspectionActivityViewModel : ObservableObject
{
    public static readonly TimeSpan SlowNoticeThreshold = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);
    private readonly LocalizationService localization;
    private readonly TimeProvider clock;
    private readonly DispatcherTimer timer;
    private long startedAt;
    private InspectionStage stage;
    private Guid? currentItem;

    /// <summary>Uses a UI-only timer; tests can advance a controlled monotonic clock explicitly.</summary>
    public InspectionActivityViewModel(LocalizationService localization, TimeProvider? clock = null)
    {
        this.localization = localization;
        this.clock = clock ?? TimeProvider.System;
        timer = new() { Interval = RefreshInterval };
        timer.Tick += (_, _) => Refresh();
        localization.PropertyChanged += (_, _) => Refresh();
    }

    public bool IsActive => currentItem is not null;
    public TimeSpan Elapsed => IsActive ? clock.GetElapsedTime(startedAt) : TimeSpan.Zero;
    public string StageText => localization[$"InspectionStage{stage}"];
    public string ElapsedText => localization.Format(new("InspectionElapsed", [Elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)]));
    public string RemainingText => localization["InspectionRemainingUnknown"];
    public bool HasSlowNotice => IsActive && Elapsed >= SlowNoticeThreshold;
    public string SlowNoticeText => localization["InspectionSlowNotice"];

    /// <summary>Starts a fresh clock only when inspection moves to a different file.</summary>
    public void Begin(Guid itemId)
    {
        if (currentItem == itemId)
        {
            return;
        }

        currentItem = itemId;
        startedAt = clock.GetTimestamp();
        stage = InspectionStage.CheckingSource;
        timer.Start();
        Refresh();
    }

    /// <summary>Rejects a delayed file-stage callback after completion or selection of a subsequent file.</summary>
    public void Report(BatchInspectionProgress progress)
    {
        if (currentItem != progress.ItemId)
        {
            return;
        }

        stage = progress.Stage;
        Refresh();
    }

    /// <summary>Stops UI work and clears presentation without touching the media owner or its cancellation token.</summary>
    public void End()
    {
        timer.Stop();
        currentItem = null;
        Refresh();
    }

    /// <summary>Recalculates elapsed presentation without changing the start time or media workflow.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);
}
