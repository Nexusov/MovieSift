using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TrackTrim.Application.Batches;
using TrackTrim.Application.Ports;
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
    private string outputFolder;
    private string? outputDirectoryRoot;
    private bool zeroAudioConfirmed;
    private Failure? planningFailure;
    private bool applyingSnapshot;

    /// <summary>Creates an editor for a stable item ID without loading global preferences.</summary>
    public BatchItemViewModel(BatchItemViewModelOptions options)
    {
        this.options = options;
        item = options.Item;
        outputFolder = item.IntendedOutputDirectoryRoot ?? options.OutputFolder;
        outputDirectoryRoot = item.IntendedOutputDirectoryRoot ??
            (item.FolderOrigin is not null && !string.IsNullOrEmpty(outputFolder) ? outputFolder : null);
        outputPath = item.IntendedDestinationPath ?? (string.IsNullOrEmpty(options.OutputFolder) ? string.Empty : DeriveDestination(options.OutputFolder));
        ApplySnapshot(item);
    }

    public Guid Id => item.Id;
    public string SourcePath => item.SourcePath;
    public string FileName => Path.GetFileName(item.SourcePath);
    public string ContextPath => DescribeContextPath();
    public string RelativeSourcePath => item.FolderOrigin?.RelativePath ?? string.Empty;
    public bool HasFolderOrigin => item.FolderOrigin is not null;
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
    public string BackendDescription => item.Inspection is { } inspection ? $"{inspection.Backend.Name} {inspection.Backend.Version}" : string.Empty;
    public string MediaFeaturesText => item.Inspection is { } inspection
        ? Localization.Format(new("MediaFeatures", [inspection.Attachments.Length, Localization[inspection.HasChapters ? "Yes" : "No"], Localization[inspection.HasTags ? "Yes" : "No"]]))
        : string.Empty;
    public string PublishedPath => item.Result?.PublishedPath ?? string.Empty;
    public bool HasPublishedPath => item.Result?.PublishedPath is not null;
    public bool HasSizeDifference => item.Inspection is not null && item.Result is { PublishedPath: not null, OutputLength: not null };
    public string SizeDifferenceText => DescribeSizeDifference();
    public string PlanTracksText => PlanPresentation.DescribeTracks(item.ApprovedPlan, Localization);
    public string PlanDefaultAudioText => PlanPresentation.DescribeDefaultAudio(item.ApprovedPlan, Localization);
    public bool RemovesDefaultAudio => item.ApprovedPlan?.RemovesDefaultAudio == true;
    public bool NeedsZeroAudioConfirmation => item.Inspection?.Tracks.Any(track => track.Kind == TrackKind.Audio) == true &&
        !Tracks.Any(track => track.Track.Kind == TrackKind.Audio && track.Keep);
    private Failure? EffectiveFailure => planningFailure ?? item.Failure ?? item.Result?.Failure;

    /// <summary>Disambiguates folder-derived episodes by their selected root and relative parent without accessing disk.</summary>
    private string DescribeContextPath()
    {
        if (item.FolderOrigin is not { } origin)
        {
            return Path.GetDirectoryName(item.SourcePath) ?? string.Empty;
        }

        var rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(origin.RootPath));
        var rootLabel = rootName.Length == 0 ? origin.RootPath : rootName;
        return Path.Combine(rootLabel, Path.GetDirectoryName(origin.RelativePath) ?? string.Empty);
    }

    /// <summary>Reports recorded source/output sizes only for a known publication, independently of cleanup.</summary>
    private string DescribeSizeDifference()
    {
        if (!HasSizeDifference)
        {
            return string.Empty;
        }

        var difference = item.Inspection!.Source.Length - item.Result!.OutputLength!.Value;

        if (difference == 0)
        {
            return Localization["SizeEqual"];
        }

        var key = difference < 0 ? "SizeIncrease" : "SizeDifference";
        return Localization.Format(new(key, [Math.Abs(difference)]));
    }

    /// <summary>Keeps approved-warning publication visibly distinct from a clean result.</summary>
    private string GetStateKey()
    {
        if (item.Result is { PublishedPath: not null, Tool.EffectiveDisposition: ToolDisposition.WarningReview })
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

            outputDirectoryRoot = item.FolderOrigin is not null && OutputPathPolicy.IsWithinDirectory(outputFolder, value)
                ? outputFolder : null;
            StoreDestination();
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

        var selectionChanged = !snapshot.CurrentRetainedTrackIds.SequenceEqual(item.CurrentRetainedTrackIds);
        item = snapshot;

        if (snapshot.IntendedDestinationPath is { } intended)
        {
            outputPath = intended;
            outputDirectoryRoot = snapshot.IntendedOutputDirectoryRoot;
        }

        if (snapshot.Inspection is { } inspection && !ReferenceEquals(displayedInspection, inspection))
        {
            displayedInspection = inspection;
            zeroAudioConfirmed = false;
            planningFailure = null;
            Tracks.Clear();

            foreach (var track in inspection.Tracks)
            {
                Tracks.Add(new(new(track, Localization, OnTrackSelectionChanged)));
            }
        }

        if (snapshot.Inspection is null && displayedInspection is not null)
        {
            displayedInspection = null;
            Tracks.Clear();
            zeroAudioConfirmed = false;
            planningFailure = null;
        }

        if (selectionChanged)
        {
            zeroAudioConfirmed = false;
            planningFailure = null;
        }

        applyingSnapshot = true;

        try
        {
            var retained = snapshot.CurrentRetainedTrackIds.ToHashSet();

            foreach (var track in Tracks)
            {
                track.Keep = retained.Contains(track.Track.Id);
            }
        }
        finally
        {
            applyingSnapshot = false;
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
        var result = PlanBuilder.Build(new(item.Inspection, identifiers, OutputPath, ZeroAudioConfirmed, outputDirectoryRoot));
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

        var destination = DeriveDestination(folder);
        var directoryRoot = item.FolderOrigin is not null ? folder : null;
        var unchanged = outputPath == destination && outputDirectoryRoot == directoryRoot &&
            item.IntendedDestinationPath == destination && item.IntendedOutputDirectoryRoot == directoryRoot;
        outputFolder = folder;

        if (unchanged)
        {
            return;
        }

        outputPath = destination;
        outputDirectoryRoot = directoryRoot;

        StoreDestination();
    }

    /// <summary>Retains destination intent below presentation without creating approval authority.</summary>
    private void StoreDestination()
    {
        // An uninspected failure may have no supported filename convention, so it has no derived destination intent.
        if (item.Inspection is null && string.IsNullOrEmpty(outputPath))
        {
            Refresh();
            return;
        }

        var changed = options.Session.SetDestination(new(Id, outputPath, outputDirectoryRoot));
        planningFailure = changed.Failure;
        Refresh();
        options.Changed();
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

    /// <summary>Stores the current file's choices and never carries silent-output consent into a changed selection.</summary>
    private void OnTrackSelectionChanged()
    {
        if (applyingSnapshot || !options.CanEdit())
        {
            return;
        }

        var identifiers = Tracks.Where(track => track.Keep).Select(track => track.Track.Id).ToImmutableArray();
        var edit = options.Session.SetSelection(new(Id, identifiers));

        var current = options.Session.Snapshot.Items.FirstOrDefault(current => current.Id == Id);

        if (!edit.Applied && current is not null)
        {
            ApplySnapshot(current);
        }

        planningFailure = edit.Failure;
        Refresh();
        options.Changed();
    }

    /// <summary>Uses the established naming convention without silently resolving collisions.</summary>
    private string DeriveDestination(string folder)
    {
        if (item.FolderOrigin is { } origin)
        {
            var result = OutputPathPolicy.DeriveFolderDestination(new(folder, origin.RelativePath));
            planningFailure = result.Failure;
            return result.DestinationPath ?? string.Empty;
        }

        return MediaContainerPolicy.FromExtension(SourcePath) is null
            ? string.Empty : Path.Combine(folder, MediaContainerPolicy.TrimmedFileName(SourcePath));
    }
}
