using CommunityToolkit.Mvvm.ComponentModel;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Planning;
using TrackTrim.Desktop.Localization;

namespace TrackTrim.Desktop.ViewModels;

/// <summary>Presents one actual immutable plan or a current per-file validation failure.</summary>
public sealed class BatchReviewRowViewModel : ObservableObject
{
    private Failure? validationFailure;

    /// <summary>Retains the existing file editor so consent and destination edits use its established revision path.</summary>
    public BatchReviewRowViewModel(BatchItemViewModel item, RemuxPlan? plan, Failure? validationFailure)
    {
        Item = item;
        Plan = plan;
        this.validationFailure = validationFailure;
    }

    public BatchItemViewModel Item { get; }
    public RemuxPlan? Plan { get; private set; }
    public LocalizationService Localization => Item.Localization;
    public string SourceName => Item.FileName;
    public string SourceContext => Item.ContextPath;
    public string SourcePath => Item.SourcePath;
    public string DestinationPath => Plan?.DestinationPath ?? Item.OutputPath;
    public bool HasPlan => Plan is not null;
    public bool NoChange => Plan?.IsNoChange == true;
    public bool HasFailure => Failure is { Code: not ErrorCode.Cancelled };
    public string FailureText => Failure is { } failure ? Localization[$"Error{failure.Code}"] : string.Empty;
    public string FailureReasonText => Failure is { Reason: not ErrorReason.None } failure
        ? Localization[$"ErrorReason{failure.Reason}"] : string.Empty;
    public string DefaultAudioText => PlanPresentation.DescribeDefaultAudio(Plan, Localization);
    public bool RemovesDefaultAudio => Plan?.RemovesDefaultAudio == true;
    public bool NeedsZeroAudioConfirmation => Item.NeedsZeroAudioConfirmation;
    public string RetainedTracksText => DescribeTracks(true);
    public string RemovedTracksText => DescribeTracks(false);
    public string NextActionText => Failure?.Code switch
    {
        ErrorCode.ZeroAudioConfirmationRequired => Localization["ReviewNextConfirmAudio"],
        ErrorCode.InvalidSource or ErrorCode.SourceChanged or ErrorCode.StalePlan => Localization["ReviewNextInspect"],
        ErrorCode.InvalidDestination or ErrorCode.DestinationExists => Localization["ReviewNextDestination"],
        null => string.Empty,
        _ => Localization["ReviewNextSelection"]
    };
    private Failure? Failure => validationFailure ?? Item.Snapshot.Failure ?? Item.Snapshot.Result?.Failure;

    /// <summary>Delegates explicit file-specific consent without creating or approving a plan.</summary>
    public bool ZeroAudioConfirmed
    {
        get => Item.ZeroAudioConfirmed;
        set => Item.ZeroAudioConfirmed = value;
    }

    /// <summary>Refreshes presentation from current immutable snapshots while preserving row identity.</summary>
    internal void Update(RemuxPlan? plan, Failure? failure)
    {
        Plan = plan;
        validationFailure = failure;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Lists actual retained or removed track identities without substituting another file's metadata.</summary>
    private string DescribeTracks(bool retained)
    {
        if (Plan is null)
        {
            return retained ? Localization["ReviewNoRetainedTracks"] : string.Empty;
        }

        var identifiers = Plan.RetainedTracks.Select(track => track.Id).ToHashSet();
        var tracks = Plan.Inspection.Tracks.Where(track => identifiers.Contains(track.Id) == retained).ToArray();

        if (tracks.Length == 0)
        {
            return Localization["ReviewNoRemovedTracks"];
        }

        return string.Join(Environment.NewLine, tracks.Select(track =>
        {
            var title = string.IsNullOrWhiteSpace(track.Title) ? Localization["Untitled"] : track.Title;
            return $"{Localization[track.Kind.ToString()]} {track.Id} — {title} ({track.Codec}, {track.LanguageIetf ?? track.Language})";
        }));
    }
}
