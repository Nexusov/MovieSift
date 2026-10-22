using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TrackTrim.Application.Batches;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Media;
using TrackTrim.Core.Planning;
using TrackTrim.Desktop.Localization;

namespace TrackTrim.Desktop.ViewModels;

/// <summary>Keeps one file's editable selection separate from every other inspector.</summary>
public sealed class BatchItemViewModel : ObservableObject
{
    private readonly BatchItemViewModelOptions options;
    private BatchItem item;
    private MediaInspection? displayedInspection;
    private string outputPath;
    private bool zeroAudioConfirmed;
    private Failure? planningFailure;

    /// <summary>Creates an editor for a stable item ID without loading global preferences.</summary>
    public BatchItemViewModel(BatchItemViewModelOptions options)
    {
        this.options = options;
        item = options.Item;
        outputPath = string.IsNullOrEmpty(options.OutputFolder) ? string.Empty : DeriveDestination(options.OutputFolder);
        ApplySnapshot(item);
    }

    public Guid Id => item.Id;
    public string SourcePath => item.SourcePath;
    public LocalizationService Localization => options.Localization;
    public ObservableCollection<TrackViewModel> Tracks { get; } = [];
    public BatchItem Snapshot => item;
    public RemuxPlan? ApprovedPlan => item.ApprovedPlan;
    public bool HasTracks => displayedInspection is not null;
    public bool CanChangeInclusion => options.CanEdit() && item.State is not (BatchItemState.Completed or BatchItemState.NoChange);
    public bool CanEditPlan => options.CanEdit() && item.Inspection is not null && item.State is BatchItemState.NeedsReview or BatchItemState.Ready;
    public bool HasPlan => item.ApprovedPlan is not null;
    public bool HasError => EffectiveFailure is { Code: not ErrorCode.Cancelled };
    public string ErrorText => EffectiveFailure is { } failure ? Localization[$"Error{failure.Code}"] : string.Empty;
    public string ErrorReasonText => EffectiveFailure is { Reason: not ErrorReason.None } failure ? Localization[$"ErrorReason{failure.Reason}"] : string.Empty;
    public string Diagnostics => string.Join(Environment.NewLine, new[]
    {
        EffectiveFailure?.Detail, item.Result?.Tool?.Diagnostics,
        item.Result is { CleanupIssues.IsDefaultOrEmpty: false } result
            ? string.Join(Environment.NewLine, result.CleanupIssues.Select(issue => $"{issue.Path}: {issue.Detail}")) : null
    }.Where(detail => !string.IsNullOrWhiteSpace(detail)).Distinct(StringComparer.Ordinal));
    public bool HasDiagnostics => !string.IsNullOrWhiteSpace(Diagnostics);
    public string StateText => Localization[GetStateKey()];
    public string PublishedPath => item.Result?.PublishedPath ?? string.Empty;
    public bool HasPublishedPath => item.Result?.PublishedPath is not null;
    public string PlanTracksText => PlanPresentation.DescribeTracks(item.ApprovedPlan, Localization);
    public string PlanDefaultAudioText => PlanPresentation.DescribeDefaultAudio(item.ApprovedPlan, Localization);
    public bool RemovesDefaultAudio => item.ApprovedPlan?.RemovesDefaultAudio == true;
    public bool NeedsZeroAudioConfirmation => item.Inspection?.Tracks.Any(track => track.Kind == TrackKind.Audio) == true &&
        !Tracks.Any(track => track.Track.Kind == TrackKind.Audio && track.Keep);
    private Failure? EffectiveFailure => planningFailure ?? item.Failure ?? item.Result?.Failure;

    /// <summary>Keeps approved-warning publication visibly distinct from a clean result.</summary>
    private string GetStateKey()
    {
        if (item.Result is { PublishedPath: not null, Tool.ExitCode: 1 })
        {
            return "BatchItemCompletedWithWarnings";
        }

        if (item.ApprovedPlan?.IsNoChange == true && item.State == BatchItemState.Ready)
        {
            return "BatchNoChangeReady";
        }

        return $"BatchItem{item.State}";
    }

    public bool Included
    {
        get => item.Included;
        set
        {
            if (item.Included == value)
            {
                return;
            }

            if (!CanChangeInclusion)
            {
                OnPropertyChanged();
                return;
            }

            var edit = options.Session.SetIncluded(Id, value);
            planningFailure = edit.Failure;
            Refresh();
            options.Changed();
        }
    }

    public string OutputPath
    {
        get => outputPath;
        set
        {
            if (!CanEditPlan || !SetProperty(ref outputPath, value))
            {
                return;
            }

            Invalidate();
        }
    }

    public bool ZeroAudioConfirmed
    {
        get => zeroAudioConfirmed;
        set
        {
            if (!CanEditPlan || !SetProperty(ref zeroAudioConfirmed, value))
            {
                return;
            }

            Invalidate();
        }
    }

    /// <summary>Accepts only this item's snapshot; selecting another row does not rebuild these tracks.</summary>
    public void ApplySnapshot(BatchItem snapshot)
    {
        if (snapshot.Id != Id)
        {
            throw new ArgumentException("The snapshot belongs to another batch item.", nameof(snapshot));
        }

        item = snapshot;

        if (snapshot.Inspection is { } inspection && !ReferenceEquals(displayedInspection, inspection))
        {
            displayedInspection = inspection;
            zeroAudioConfirmed = false;
            planningFailure = null;
            Tracks.Clear();

            foreach (var track in inspection.Tracks)
            {
                Tracks.Add(new(new(track, Localization, Invalidate)));
            }
        }

        if (snapshot.Inspection is null && displayedInspection is not null)
        {
            displayedInspection = null;
            Tracks.Clear();
            zeroAudioConfirmed = false;
            planningFailure = null;
        }

        Refresh();
    }

    /// <summary>Snapshots only the current file's IDs and explicit zero-audio consent.</summary>
    public void Approve()
    {
        if (!CanEditPlan || item.Inspection is null)
        {
            return;
        }

        var identifiers = Tracks.Where(track => track.Keep).Select(track => track.Track.Id).ToArray();
        var result = PlanBuilder.Build(new(item.Inspection, identifiers, OutputPath, ZeroAudioConfirmed));
        planningFailure = result.Failure;

        if (result.Plan is { } plan)
        {
            planningFailure = options.Session.ApprovePlan(new(Id, plan)).Failure;
        }

        Refresh();
        options.Changed();
    }

    /// <summary>Applies an explicit shared-folder change only to items that can still be planned.</summary>
    public void ApplyOutputFolder(string folder)
    {
        var editableState = item.State is BatchItemState.PendingInspection or BatchItemState.NeedsReview or
            BatchItemState.Ready or BatchItemState.Failed or BatchItemState.Cancelled;

        if (!editableState)
        {
            return;
        }

        outputPath = DeriveDestination(folder);

        if (item.Inspection is null)
        {
            Refresh();
            options.Changed();
            return;
        }

        Invalidate();
    }

    /// <summary>Refreshes labels without changing the file's tracks, plan or outcome.</summary>
    public void Refresh()
    {
        foreach (var track in Tracks)
        {
            track.RefreshLocalization();
        }

        OnPropertyChanged(string.Empty);
    }

    /// <summary>Invalidates only this file's approval and the aggregate review.</summary>
    private void Invalidate()
    {
        if (!options.Session.Snapshot.Items.Any(current => current.Id == Id))
        {
            return;
        }

        planningFailure = null;
        options.Session.InvalidatePlan(Id);
        Refresh();
        options.Changed();
    }

    /// <summary>Uses the established naming convention without silently resolving collisions.</summary>
    private string DeriveDestination(string folder)
    {
        return Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(SourcePath)}.trimmed.mkv");
    }
}
