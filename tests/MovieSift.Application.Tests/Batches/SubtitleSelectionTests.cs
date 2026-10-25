using System.Collections.Immutable;
using MovieSift.Application.Batches;
using MovieSift.Core.Failures;
using MovieSift.Core.Media;
using Xunit;

namespace MovieSift.Application.Tests.Batches;

/// <summary>Checks all-subtitle edits at the existing atomic boundary with actual file-local occurrences.</summary>
public sealed class SubtitleSelectionTests
{
    /// <summary>Includes fourth-file-only, duplicate and unknown subtitles without disturbing unrelated choices.</summary>
    [Fact]
    public async Task RemovingEverySubtitleChangesOnlyItsIncludedEditableSourcesOnce()
    {
        var harness = new BatchTestHarness(4);
        var paths = harness.Inspections.Keys.ToArray();
        foreach (var path in paths.Take(3))
        {
            var inspection = harness.Inspections[path];
            harness.Inspections[path] = inspection with
            {
                Tracks = inspection.Tracks.Where(track => track.Kind != TrackKind.Subtitles).ToImmutableArray()
            };
        }

        var fourth = harness.Inspections[paths[3]];
        var subtitle = fourth.Tracks.Single(track => track.Kind == TrackKind.Subtitles);
        harness.Inspections[paths[3]] = fourth with
        {
            Tracks = fourth.Tracks.AddRange(new[] {
                subtitle with { Id = 90, Uid = 90 },
                subtitle with { Id = 91, Uid = 91, Language = "und", LanguageIetf = null },
                subtitle with { Id = 92, Uid = 92, Language = "rus", LanguageIetf = "ru" } })
        };
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        foreach (var item in harness.Session.Snapshot.Items)
        {
            harness.Session.SetDestination(new(item.Id, Path.ChangeExtension(item.SourcePath, ".trimmed.mkv")));
        }

        await harness.Coordinator.ReviewSelectionsAsync(new(harness.Session.Snapshot.Revision, []));
        var first = harness.Session.Snapshot.Items[0];
        harness.Session.SetSelection(new(first.Id, first.CurrentRetainedTrackIds.Where(identifier =>
            identifier != first.Inspection!.Tracks.Single(track => track.Kind == TrackKind.Audio).Id).ToImmutableArray()));
        var before = harness.Session.Snapshot;
        var projection = harness.Session.CreateTrackProjection();
        var members = Subtitles(projection);
        Assert.Equal(4, members.Length);
        Assert.Contains(members, member => member.Track.Language == "und");
        Assert.True(harness.Session.SetTrackSelection(new(projection, members, false)).Applied);
        var after = harness.Session.Snapshot;
        Assert.Equal(before.Revision + 1, after.Revision);
        Assert.Null(after.Review);
        Assert.All(after.Items.Take(3), item => Assert.Same(before.Items.Single(previous => previous.Id == item.Id), item));
        Assert.DoesNotContain(after.Items[3].Inspection!.Tracks.Where(track => track.Kind == TrackKind.Subtitles),
            track => after.Items[3].CurrentRetainedTrackIds.Contains(track.Id));
        Assert.Equal(before.Items[3].CurrentRetainedTrackIds.Except(members.Select(member => member.Track.Id)),
            after.Items[3].CurrentRetainedTrackIds);
        Assert.Null(after.Items[3].ApprovedPlan);
        Assert.Empty(harness.RemuxedPaths);
        Assert.Empty(harness.Reservations);
    }

    /// <summary>Excludes published and excluded rows and rejects obsolete multi-member requests atomically.</summary>
    [Fact]
    public async Task PublishedExcludedAndStaleMembersStayProtected()
    {
        var harness = new BatchTestHarness(3);
        var firstPath = harness.Inspections.Keys.First();
        var firstInspection = harness.Inspections[firstPath];
        harness.Inspections[firstPath] = firstInspection with
        {
            Tracks = firstInspection.Tracks.Add(firstInspection.Tracks.Single(track => track.Kind == TrackKind.Subtitles)
                with
            { Id = 99, Uid = 999 })
        };
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        var initial = harness.Session.Snapshot.Items;
        harness.Session.SetSelection(new(initial[0].Id, initial[0].CurrentRetainedTrackIds.Where(identifier => identifier != 99).ToImmutableArray()));
        harness.Session.SetDestination(new(initial[0].Id, Path.ChangeExtension(initial[0].SourcePath, ".trimmed.mkv")));
        harness.Session.SetIncluded(initial[1].Id, false);
        harness.Session.SetIncluded(initial[2].Id, false);
        var review = await harness.Coordinator.ReviewSelectionsAsync(new(harness.Session.Snapshot.Revision, []));
        await harness.Coordinator.RunAsync(new(review.Review!.Id));
        harness.Session.SetIncluded(initial[1].Id, true);
        var before = harness.Session.Snapshot;
        var projection = harness.Session.CreateTrackProjection();
        Assert.Contains(projection.Groups.SelectMany(group => group.Members), member =>
            member.ItemId == initial[0].Id && member.Track.Kind == TrackKind.Subtitles && member.Keep && !member.IsEditable);
        var request = new BatchTrackSelectionRequest(projection, Subtitles(projection), false);
        Assert.Single(request.Members);
        Assert.True(harness.Session.SetTrackSelection(request).Applied);
        var after = harness.Session.Snapshot;
        Assert.Same(before.Items[0], after.Items[0]);
        Assert.Same(before.Items[2], after.Items[2]);
        Assert.Single(harness.PublishedPaths);
        Assert.Single(harness.RemuxedPaths);
        Assert.Equal(ErrorCode.StalePlan, harness.Session.SetTrackSelection(request).Failure!.Code);
        Assert.Same(after, harness.Session.Snapshot);
    }

    /// <summary>Builds the same explicit editable-occurrence request as the workspace action.</summary>
    private static ImmutableArray<BatchTrackMember> Subtitles(BatchTrackProjection projection) => projection.Groups
        .Where(group => group.Key.Kind == TrackKind.Subtitles).SelectMany(group => group.Members)
        .Where(member => member.IsEditable && member.Keep).ToImmutableArray();
}
