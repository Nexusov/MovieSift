using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using TrackTrim.Application.Batches;
using TrackTrim.Core.Media;

namespace TrackTrim.Desktop.ViewModels;

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
    }

    public BatchTrackMember Snapshot => member;
    public Localization.LocalizationService Localization => options.Localization;
    public string SourcePath => member.Inspection.Source.FullPath;
    public string IdentifierText => member.Track.Id.ToString(CultureInfo.InvariantCulture);
    public string TitleText => string.IsNullOrWhiteSpace(member.Track.Title) ? Localization["Untitled"] : member.Track.Title;
    public string LanguageText => string.Join(" / ", new[] { member.Track.Language, member.Track.LanguageIetf }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal));
    public string FlagsText => TrackPresentation.DescribeFlags(member.Track, Localization);
    public bool CanEdit => options.CanEdit() && member.IsEditable;
    public string StateText => Localization[member.State is BatchItemState.Completed or BatchItemState.NoChange ? "AggregateHistorical" : member.Track.Kind == TrackKind.Video ? "AlwaysKept" : "AggregateEditable"];

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

    /// <summary>Matches presentation identity only; metadata grouping is owned by Core/Application.</summary>
    internal bool Represents(BatchTrackMember candidate) => member.ItemId == candidate.ItemId &&
        member.Track.Id == candidate.Track.Id && ReferenceEquals(member.Inspection, candidate.Inspection);

    /// <summary>Receives a fresh projection after an actual per-file edit or presentation change.</summary>
    internal void Update(BatchTrackMember updated, BatchTrackProjection currentProjection)
    {
        member = updated;
        projection = currentProjection;
        OnPropertyChanged(string.Empty);
    }
}
