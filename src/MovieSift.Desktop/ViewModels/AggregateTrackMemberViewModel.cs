using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MovieSift.Application.Batches;
using MovieSift.Core.Media;

namespace MovieSift.Desktop.ViewModels;

/// <summary>Edits one explicitly represented occurrence through its captured inspection and session revision.</summary>
public sealed class AggregateTrackMemberViewModel : ObservableObject
{
    private readonly AggregateTracksViewModelOptions options;
    private BatchTrackMember member;
    private BatchTrackProjection projection;

    /// <summary>Retains no independent mutable selection or approval state.</summary>
    public AggregateTrackMemberViewModel(BatchTrackMember member, BatchTrackProjection projection, AggregateTracksViewModelOptions options)
    {
        this.member = member;
        this.projection = projection;
        this.options = options;
        ToggleSelectionCommand = new RelayCommand(ToggleSelection, () => CanEdit);
    }

    public BatchTrackMember Snapshot => member;
    public Localization.LocalizationService Localization => options.Localization;
    public string SourcePath => member.Inspection.Source.FullPath;
    public string IdentifierText => member.Track.Id.ToString(CultureInfo.InvariantCulture);
    public string TitleText => string.IsNullOrWhiteSpace(member.Track.Title) ? Localization["Untitled"] : member.Track.Title;
    public string LanguageText => string.Join(" / ", new[] { member.Track.Language, member.Track.LanguageIetf }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal));
    public string FlagsText => TrackPresentation.DescribeFlags(member.Track, Localization);
    public bool IsRequired => member.Track.Kind == TrackKind.Video;
    public string KeepAccessibleName => Localization.Format(new("AggregateMemberKeep",
        [Localization[member.Track.Kind.ToString()], IdentifierText, SourcePath,
            string.Join(", ", new[] { LanguageText, TitleText, FlagsText }.Where(value => !string.IsNullOrWhiteSpace(value)))]));
    public bool CanEdit => options.CanEdit() && member.IsEditable;
    public bool IsExcluded => !Keep;
    public string SelectionActionText => Localization[IsExcluded ? "TrackRestore" : "TrackExclude"];
    public string SelectionActionAccessibleName => $"{SelectionActionText}: {Localization[member.Track.Kind.ToString()]} {IdentifierText}, {SourcePath}, {TitleText}";
    public IRelayCommand ToggleSelectionCommand { get; }
    public string StateText => Localization[IsExcluded ? "TrackExcluded"
        : member.State is BatchItemState.Completed or BatchItemState.NoChange ? "AggregateHistorical"
        : IsRequired ? "AlwaysKept" : "AggregateEditable"];

    public bool Keep
    {
        get => member.Keep;
        set
        {
            if (!CanEdit || value == member.Keep)
            {
                OnPropertyChanged();
                return;
            }

            var result = options.Session.SetTrackSelection(new(projection, [member], value));
            options.SelectionEdited(result.Failure);
            OnPropertyChanged();
        }
    }

    /// <summary>Rechecks current member availability before forwarding an explicit reversible selection edit.</summary>
    private void ToggleSelection()
    {
        if (!CanEdit)
        {
            return;
        }

        Keep = !Keep;
    }

    /// <summary>Matches presentation identity only; metadata grouping is owned by Core/Application.</summary>
    internal bool Represents(BatchTrackMember candidate) => member.ItemId == candidate.ItemId &&
        member.Track.Id == candidate.Track.Id && ReferenceEquals(member.Inspection, candidate.Inspection);

    /// <summary>Receives a fresh projection after an actual per-file edit or presentation change.</summary>
    internal void Update(BatchTrackMember updated, BatchTrackProjection currentProjection)
    {
        member = updated;
        projection = currentProjection;
        OnPropertyChanged(string.Empty);
        ToggleSelectionCommand.NotifyCanExecuteChanged();
    }
}
