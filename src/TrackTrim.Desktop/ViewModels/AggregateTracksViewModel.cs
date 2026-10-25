using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TrackTrim.Application.Batches;

namespace TrackTrim.Desktop.ViewModels;

/// <summary>Projects the included inspection union without storing another set of Keep choices.</summary>
public sealed class AggregateTracksViewModel : ObservableObject
{
    private readonly AggregateTracksViewModelOptions options;
    private BatchTrackProjection projection;

    /// <summary>Connects the aggregate presentation to the existing revision-bound session edits.</summary>
    public AggregateTracksViewModel(AggregateTracksViewModelOptions options)
    {
        this.options = options;
        projection = options.Session.CreateTrackProjection();
        Refresh();
    }

    public ObservableCollection<AggregateTrackGroupViewModel> Groups { get; } = [];
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

        OnPropertyChanged(string.Empty);
    }
}
