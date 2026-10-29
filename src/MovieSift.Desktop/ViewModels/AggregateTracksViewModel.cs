using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MovieSift.Application.Batches;
using MovieSift.Core.Media;

namespace MovieSift.Desktop.ViewModels;

/// <summary>Projects the included inspection union without storing another set of Keep choices.</summary>
public sealed class AggregateTracksViewModel : ObservableObject
{
    private readonly AggregateTracksViewModelOptions options;
    private BatchTrackProjection projection;
    private int selectedTrackTab;

    /// <summary>Connects the aggregate presentation to the existing revision-bound session edits.</summary>
    public AggregateTracksViewModel(AggregateTracksViewModelOptions options)
    {
        this.options = options;
        projection = options.Session.CreateTrackProjection();
        RemoveAllSubtitlesCommand = new RelayCommand(RemoveAllSubtitles, () => CanRemoveSubtitles);
        Refresh();
    }

    public ObservableCollection<AggregateTrackGroupViewModel> Groups { get; } = [];
    public ObservableCollection<AggregateTrackGroupViewModel> VideoGroups { get; } = [];
    public ObservableCollection<AggregateTrackGroupViewModel> AudioGroups { get; } = [];
    public ObservableCollection<AggregateTrackGroupViewModel> SubtitleGroups { get; } = [];
    public int SelectedTrackTab { get => selectedTrackTab; set => SetProperty(ref selectedTrackTab, value); }
    public string VideoSummaryText => DescribeSection(TrackKind.Video);
    public string AudioSummaryText => DescribeSection(TrackKind.Audio);
    public string SubtitleSummaryText => DescribeSection(TrackKind.Subtitles);
    public bool CanRemoveSubtitles => options.CanEdit() && EditableKeptSubtitles().Any();
    public IRelayCommand RemoveAllSubtitlesCommand { get; }
    public Localization.LocalizationService Localization => options.Localization;
    public bool HasGroups => Groups.Count != 0;
    public bool HasUnresolved => projection.UnresolvedFileCount != 0;
    public string ScopeText => Localization.Format(new("AggregateScope", [projection.IncludedFileCount, projection.UnresolvedFileCount]));
    public string UnresolvedText => string.Join(Environment.NewLine, options.Session.Snapshot.Items
        .Where(item => item.Included && item.Inspection is null)
        .Select(item => $"{item.SourcePath} — {Localization[$"BatchItem{item.State}"]}"));

    /// <summary>Reconciles stable rows while always using current per-file snapshots as selection authority.</summary>
    public void Refresh()
    {
        projection = options.Session.CreateTrackProjection();
        var remaining = Groups.ToList();

        for (var index = 0; index < projection.Groups.Length; index++)
        {
            var group = projection.Groups[index];
            var row = remaining.FirstOrDefault(current => current.Represents(group));

            if (row is null)
            {
                row = new(group, projection, options);
                Groups.Insert(index, row);
            }

            remaining.Remove(row);
            var previousIndex = Groups.IndexOf(row);

            if (previousIndex != index)
            {
                Groups.Move(previousIndex, index);
            }

            row.Update(group, projection);
        }

        foreach (var removed in remaining)
        {
            Groups.Remove(removed);
        }

        RefreshSection(VideoGroups, TrackKind.Video);
        RefreshSection(AudioGroups, TrackKind.Audio);
        RefreshSection(SubtitleGroups, TrackKind.Subtitles);
        OnPropertyChanged(string.Empty);
        RemoveAllSubtitlesCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Keeps section collections stable so selection edits do not replace controls or lose keyboard focus.</summary>
    private void RefreshSection(ObservableCollection<AggregateTrackGroupViewModel> section, TrackKind kind)
    {
        var expected = Groups.Where(group => group.Snapshot.Key.Kind == kind).ToArray();
        for (var index = 0; index < expected.Length; index++)
        {
            var row = expected[index];
            var previousIndex = section.IndexOf(row);

            if (previousIndex < 0)
            {
                section.Insert(index, row);
                continue;
            }

            if (previousIndex != index)
            {
                section.Move(previousIndex, index);
            }
        }

        foreach (var removed in section.Where(row => !expected.Contains(row)).ToArray())
        {
            section.Remove(removed);
        }
    }

    /// <summary>Describes actual category groups and occurrences, including duplicates and uncertain metadata.</summary>
    private string DescribeSection(TrackKind kind)
    {
        var groups = projection.Groups.Where(group => group.Key.Kind == kind).ToArray();
        return Localization.Format(new("AggregateSectionSummary",
            [Localization[kind.ToString()], groups.Length, groups.Sum(group => group.OccurrenceCount)]));
    }

    /// <summary>Targets only included editable subtitle occurrences whose Keep value would actually change.</summary>
    private IEnumerable<BatchTrackMember> EditableKeptSubtitles() => projection.Groups
        .Where(group => group.Key.Kind == TrackKind.Subtitles)
        .SelectMany(group => group.Members).Where(member => member.IsEditable && member.Keep);

    /// <summary>Applies one atomic, revision-bound selection edit without interpreting language or approving plans.</summary>
    private void RemoveAllSubtitles()
    {
        if (!CanRemoveSubtitles)
        {
            return;
        }

        var members = EditableKeptSubtitles().ToImmutableArray();
        var result = options.Session.SetTrackSelection(new(projection, members, false));
        options.SelectionEdited(result.Failure);
    }
}
