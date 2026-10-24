using System.Collections.Immutable;
using TrackTrim.Core.Media;

namespace TrackTrim.Application.Batches;

/// <summary>Builds the union from immutable snapshots without probing media or storing a second selection.</summary>
internal static class BatchTrackProjectionBuilder
{
    /// <summary>Retains every duplicate and keeps uncertain language occurrences independently editable.</summary>
    internal static BatchTrackProjection Build(BatchSnapshot snapshot)
    {
        var groups = new List<BatchTrackGroup>();
        var knownGroups = new Dictionary<TrackGroupingKey, int>();
        var included = snapshot.Items.Where(item => item.Included).ToArray();

        foreach (var item in included.Where(item => item.Inspection is not null))
        {
            var retained = item.CurrentRetainedTrackIds.ToHashSet();
            var editable = item.State is not (BatchItemState.Completed or BatchItemState.NoChange);

            foreach (var track in item.Inspection!.Tracks)
            {
                var description = TrackGroupingPolicy.Describe(track);
                var member = new BatchTrackMember(item.Id, item.Inspection, track, retained.Contains(track.Id),
                    editable && track.Kind != TrackKind.Video, item.State);

                if (description.LanguageIssue is null && knownGroups.TryGetValue(description.Key, out var groupIndex))
                {
                    var group = groups[groupIndex];
                    groups[groupIndex] = group with { Members = group.Members.Add(member) };
                    continue;
                }

                if (description.LanguageIssue is null)
                {
                    knownGroups.Add(description.Key, groups.Count);
                }

                groups.Add(new(description.Key, [member], description.LanguageIssue));
            }
        }

        return new(snapshot.Revision, groups.ToImmutableArray(), included.Length,
            included.Count(item => item.Inspection is null));
    }
}
