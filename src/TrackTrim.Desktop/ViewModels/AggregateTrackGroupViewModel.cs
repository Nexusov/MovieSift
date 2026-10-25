using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrackTrim.Application.Batches;
using TrackTrim.Core.Media;

namespace TrackTrim.Desktop.ViewModels;

/// <summary>Presents one deterministic metadata group and all of its actual occurrences.</summary>
public sealed class AggregateTrackGroupViewModel : ObservableObject
{
    private readonly AggregateTracksViewModelOptions options;
    private BatchTrackGroup group;
    private BatchTrackProjection projection;
    private bool isExpanded;

    /// <summary>Captures the projection that must still be current when a group command is applied.</summary>
    public AggregateTrackGroupViewModel(BatchTrackGroup group, BatchTrackProjection projection, AggregateTracksViewModelOptions options)
    {
        this.group = group;
        this.projection = projection;
        this.options = options;
        ToggleKeepCommand = new RelayCommand(ToggleKeep, () => CanEdit);
    }

    public ObservableCollection<AggregateTrackMemberViewModel> Members { get; } = [];
    public Localization.LocalizationService Localization => options.Localization;
    public BatchTrackGroup Snapshot => group;
    public bool? Keep => group.Keep;
    public bool CanEdit => options.CanEdit() && group.Members.Any(member => member.IsEditable);
    public bool IsRequired => group.Key.Kind == TrackKind.Video;
    public string KindText => Localization[group.Key.Kind.ToString()];
    public string LanguageText => group.Key.CanonicalLanguage ?? Localization["Unknown"];
    public string TitleText => string.IsNullOrWhiteSpace(group.Key.Title) ? Localization["Untitled"] : group.Key.Title;
    public string CodecText => group.Key.Codec;
    public string FlagsText => TrackPresentation.DescribeGroupFlags(group.Members.Select(member => member.Track), Localization);
    public string ChannelsText => group.Key.Kind == TrackKind.Audio
        ? Localization.Format(new("AggregateChannels", [group.Key.AudioChannels?.ToString(CultureInfo.InvariantCulture) ?? Localization["Unknown"]]))
        : string.Empty;
    public string CoverageText => Localization.Format(new("AggregateCoverage", [group.FileCoverage, projection.IncludedFileCount, group.OccurrenceCount]));
    public bool HasLanguageIssue => group.LanguageIssue is not null;
    public string LanguageIssueText => group.LanguageIssue is { } issue ? Localization[$"RuleIssue{issue}"] : string.Empty;
    public string KeepAccessibleName => string.Join(", ", new[]
    {
        $"{Localization["Keep"]}: {KindText}", LanguageText, TitleText, CodecText, ChannelsText, FlagsText, CoverageText
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public bool HasChoiceNotice => Keep != true;
    public string ChoiceText => Localization[Keep is true ? "AggregateAllKept" : Keep is false ? "AggregateAllRemoved" : "AggregateMixed"];
    public IRelayCommand ToggleKeepCommand { get; }

    public bool IsExpanded
    {
        get => isExpanded;
        set => SetProperty(ref isExpanded, value);
    }

    /// <summary>Reuses metadata rows without treating local identifiers as grouping keys.</summary>
    internal bool Represents(BatchTrackGroup candidate)
    {
        if (group.Key != candidate.Key || group.LanguageIssue != candidate.LanguageIssue)
        {
            return false;
        }

        if (group.LanguageIssue is null)
        {
            return true;
        }

        var previous = group.Members[0];
        var next = candidate.Members[0];
        return previous.ItemId == next.ItemId && previous.Track.Id == next.Track.Id && ReferenceEquals(previous.Inspection, next.Inspection);
    }

    /// <summary>Refreshes concrete members and command gates without clearing expansion or file choices.</summary>
    internal void Update(BatchTrackGroup updated, BatchTrackProjection currentProjection)
    {
        group = updated;
        projection = currentProjection;
        var remaining = Members.ToList();

        for (var index = 0; index < group.Members.Length; index++)
        {
            var member = group.Members[index];
            var row = remaining.FirstOrDefault(current => current.Represents(member));

            if (row is null)
            {
                row = new(member, projection, options);
                Members.Insert(index, row);
            }

            remaining.Remove(row);
            var previousIndex = Members.IndexOf(row);

            if (previousIndex != index)
            {
                Members.Move(previousIndex, index);
            }

            row.Update(member, projection);
        }

        foreach (var removed in remaining)
        {
            Members.Remove(removed);
        }

        OnPropertyChanged(string.Empty);
        ToggleKeepCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Mixed and unchecked keep all editable members; checked removes only those represented members.</summary>
    private void ToggleKeep()
    {
        if (!CanEdit)
        {
            return;
        }

        var result = options.Session.SetTrackSelection(new(projection,
            group.Members.Where(member => member.IsEditable).ToImmutableArray(), Keep != true));
        options.SelectionEdited(result.Failure);
        OnPropertyChanged(nameof(Keep));
    }
}
