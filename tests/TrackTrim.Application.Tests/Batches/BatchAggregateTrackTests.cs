using System.Collections.Immutable;
using TrackTrim.Application.Batches;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Media;
using Xunit;

namespace TrackTrim.Application.Tests.Batches;

/// <summary>Exercises a revision-bound union over the real per-file selections, not another selection store.</summary>
public sealed class BatchAggregateTrackTests
{
    /// <summary>Finds Spanish in only episode four and keeps unchanged episodes as genuine no-change jobs.</summary>
    [Fact]
    public async Task ExtraSpanishGroupChangesOnlyItsActualFileAndReviewBuildsEveryPlan()
    {
        var harness = new BatchTestHarness(4);
        var lastPath = harness.Inspections.Keys.Last();
        var last = harness.Inspections[lastPath];
        harness.Inspections[lastPath] = last with
        {
            Tracks = last.Tracks.Add(last.Tracks[1] with { Id = 99, Uid = 999, Language = "spa", LanguageIetf = "es" })
        };
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        SetDestinations(harness);
        var before = harness.Session.Snapshot;
        var projection = harness.Session.CreateTrackProjection();
        var spanish = Assert.Single(projection.Groups, group => group.Key.CanonicalLanguage == "spa");
        Assert.Equal(4, projection.IncludedFileCount);
        Assert.Equal(1, spanish.FileCoverage);
        Assert.Equal(1, spanish.OccurrenceCount);
        Assert.True(harness.Session.SetTrackSelection(new(projection, spanish.Members, false)).Applied);
        Assert.All(harness.Session.Snapshot.Items.Take(3), item =>
            Assert.Same(before.Items.Single(original => original.Id == item.Id), item));
        Assert.DoesNotContain(99, harness.Session.Snapshot.Items[3].CurrentRetainedTrackIds);
        Assert.Empty(harness.RemuxedPaths);

        var review = await harness.Coordinator.ReviewSelectionsAsync(new(harness.Session.Snapshot.Revision, []));

        Assert.Null(review.Failure);
        Assert.Empty(review.ItemFailures);
        Assert.Equal(4, review.Review!.Items.Length);
        Assert.Equal(3, review.Review.Items.Count(item => item.Plan.IsNoChange));
        await harness.Coordinator.RunAsync(new(review.Review.Id, CancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(lastPath, Assert.Single(harness.RemuxedPaths));
        Assert.Equal(3, harness.Session.Snapshot.Items.Count(item => item.State == BatchItemState.NoChange));
        Assert.Equal(1, harness.MaximumActiveRemuxes);
    }

    /// <summary>Retains duplicate occurrences, distinguishes coverage and derives mixed after an individual exception.</summary>
    [Fact]
    public async Task DuplicateOccurrencesAndIndividualExceptionsRemainVisibleAcrossDifferentOrders()
    {
        var harness = new BatchTestHarness();
        var firstPath = harness.Inspections.Keys.First();
        var first = harness.Inspections[firstPath];
        harness.Inspections[firstPath] = first with { Tracks = first.Tracks.Add(first.Tracks[1] with { Id = 7, Uid = 70 }) };
        var secondPath = harness.Inspections.Keys.Last();
        var second = harness.Inspections[secondPath];
        harness.Inspections[secondPath] = second with { Tracks = [second.Tracks[2], second.Tracks[1], second.Tracks[0]] };
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        var projection = harness.Session.CreateTrackProjection();
        var audio = Assert.Single(projection.Groups, group => group.Key.Kind == TrackKind.Audio);
        Assert.Equal(2, audio.FileCoverage);
        Assert.Equal(3, audio.OccurrenceCount);
        Assert.True(harness.Session.SetTrackSelection(new(projection, [audio.Members[0]], false)).Applied);
        var currentProjection = harness.Session.CreateTrackProjection();
        var mixed = Assert.Single(currentProjection.Groups, group => group.Key.Kind == TrackKind.Audio);
        Assert.Null(mixed.Keep);
        Assert.False(mixed.Members[0].Keep);
        Assert.True(mixed.Members[1].Keep);
        Assert.True(mixed.Members[2].Keep);
        Assert.True(harness.Session.SetTrackSelection(new(currentProjection, mixed.Members, true)).Applied);
        Assert.All(harness.Session.Snapshot.Items, item => Assert.Equal(item.Inspection!.Tracks.Select(track => track.Id), item.CurrentRetainedTrackIds));
    }

    /// <summary>Unknown occurrences remain separate and movie-only titles contribute to the union.</summary>
    [Fact]
    public async Task UnknownAndDistinctMovieMetadataNeverDisappearFromUnion()
    {
        var harness = new BatchTestHarness(3);
        var paths = harness.Inspections.Keys.ToArray();
        foreach (var path in paths.Take(2))
        {
            var inspection = harness.Inspections[path];
            harness.Inspections[path] = inspection with
            {
                Tracks = inspection.Tracks.Add(inspection.Tracks[1] with
                { Id = 99, Language = "und", LanguageIetf = null })
            };
        }
        var movie = harness.Inspections[paths[2]];
        harness.Inspections[paths[2]] = movie with
        {
            Tracks = movie.Tracks.Add(movie.Tracks[1] with
            { Id = 98, Title = "Movie commentary", Dispositions = [TrackDisposition.Commentary] })
        };
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        var groups = harness.Session.CreateTrackProjection().Groups;
        Assert.Equal(2, groups.Count(group => group.LanguageIssue is not null));
        Assert.All(groups.Where(group => group.LanguageIssue is not null), group => Assert.Single(group.Members));
        Assert.Single(groups, group => group.Key.Title == "Movie commentary");
    }

    /// <summary>Rejects stale lists, inspections, video and completed edits without damaging current authority.</summary>
    [Fact]
    public async Task StaleOrLockedMembershipCannotEditAnotherRevision()
    {
        var harness = new BatchTestHarness(1);
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        var projection = harness.Session.CreateTrackProjection();
        var audio = Assert.Single(projection.Groups, group => group.Key.Kind == TrackKind.Audio);
        var video = Assert.Single(projection.Groups, group => group.Key.Kind == TrackKind.Video);
        var before = harness.Session.Snapshot;
        Assert.False(harness.Session.SetTrackSelection(new(projection, video.Members, false)).Applied);
        Assert.Same(before, harness.Session.Snapshot);
        await harness.Coordinator.InspectItemAsync(new(before.Items[0].Id));
        var stale = harness.Session.SetTrackSelection(new(projection, audio.Members, false));
        Assert.Equal(ErrorCode.StalePlan, stale.Failure!.Code);
        var current = harness.Session.CreateTrackProjection();
        Assert.True(harness.Session.SetIncluded(before.Items[0].Id, false).Applied);
        Assert.False(harness.Session.SetTrackSelection(new(current, current.Groups[1].Members, false)).Applied);
    }

    /// <summary>A newly added occurrence starts kept rather than inheriting a previous group's manual removal.</summary>
    [Fact]
    public async Task AddingNewMemberProducesMixedAndRemovalUpdatesCoverage()
    {
        var harness = new BatchTestHarness(1);
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        var projection = harness.Session.CreateTrackProjection();
        var subtitles = Assert.Single(projection.Groups, group => group.Key.Kind == TrackKind.Subtitles);
        Assert.True(harness.Session.SetTrackSelection(new(projection, subtitles.Members, false)).Applied);
        var original = harness.Inspections.Values.Single();
        var extra = original with { Source = original.Source with { FullPath = original.Source.FullPath + ".extra.mkv", FileIdentity = "extra" } };
        harness.Inspections.Add(extra.Source.FullPath, extra);
        harness.Revisions.Add(extra.Source.FullPath, extra.Source);
        Assert.True(harness.Session.AddSources([extra.Source]).Applied);
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        var afterAdd = Assert.Single(harness.Session.CreateTrackProjection().Groups, group => group.Key.Kind == TrackKind.Subtitles);
        Assert.Null(afterAdd.Keep);
        Assert.Equal(2, afterAdd.FileCoverage);
        Assert.True(harness.Session.Remove(harness.Session.Snapshot.Items.Last().Id).Applied);
        Assert.False(Assert.Single(harness.Session.CreateTrackProjection().Groups, group => group.Key.Kind == TrackKind.Subtitles).Keep);
    }

    /// <summary>Historical occurrences stay visible but do not distort an editable group's toggle state.</summary>
    [Fact]
    public async Task CompletedMembersStayHistoricalWhileRemainingMembersAreEditable()
    {
        var harness = new BatchTestHarness();
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        SetDestinations(harness);
        var projection = harness.Session.CreateTrackProjection();
        var subtitles = Assert.Single(projection.Groups, group => group.Key.Kind == TrackKind.Subtitles);
        Assert.True(harness.Session.SetTrackSelection(new(projection, [subtitles.Members[0]], false)).Applied);
        var reviewed = await harness.Coordinator.ReviewSelectionsAsync(new(harness.Session.Snapshot.Revision, []));
        harness.OnRemux = _ => { harness.Coordinator.Pause(); return Task.CompletedTask; };
        await harness.Coordinator.RunAsync(new(reviewed.Review!.Id));
        var completed = harness.Session.Snapshot.Items[0];
        Assert.Equal(BatchItemState.Completed, completed.State);
        projection = harness.Session.CreateTrackProjection();
        subtitles = Assert.Single(projection.Groups, group => group.Key.Kind == TrackKind.Subtitles);
        Assert.True(subtitles.Keep);
        Assert.False(subtitles.Members[0].IsEditable);
        Assert.Equal(BatchItemState.Completed, subtitles.Members[0].State);
        Assert.False(harness.Session.SetTrackSelection(new(projection, subtitles.Members, false)).Applied);
        Assert.True(harness.Session.SetTrackSelection(new(projection,
            subtitles.Members.Where(member => member.IsEditable).ToImmutableArray(), false)).Applied);
        Assert.Same(completed, harness.Session.Snapshot.Items[0]);
        Assert.DoesNotContain(12, harness.Session.Snapshot.Items[1].CurrentRetainedTrackIds);
        Assert.Single(harness.PublishedPaths);
    }

    /// <summary>The same ownership gates protect both aggregate editing and its explicit approval boundary.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WarningAndUncertainCleanupRejectAggregateEdits(bool cleanupFailure)
    {
        var harness = new BatchTestHarness(1);
        var review = await harness.PrepareAsync();
        harness.CleanupFails = cleanupFailure;
        harness.ExitCodes[harness.Session.Snapshot.Items[0].SourcePath] = cleanupFailure ? 0 : 1;
        await harness.Coordinator.RunAsync(new(review.Id));
        var before = harness.Session.Snapshot;
        var projection = harness.Session.CreateTrackProjection();
        var audio = Assert.Single(projection.Groups, group => group.Key.Kind == TrackKind.Audio);
        var edit = harness.Session.SetTrackSelection(new(projection, audio.Members, false));
        Assert.False(edit.Applied);
        Assert.Equal(cleanupFailure ? ErrorCode.OwnershipInvalid : ErrorCode.Busy, edit.Failure!.Code);
        var reviewed = await harness.Coordinator.ReviewSelectionsAsync(new(before.Revision, []));
        Assert.Null(reviewed.Review);
        Assert.Equal(cleanupFailure ? ErrorCode.OwnershipInvalid : ErrorCode.Busy, reviewed.Failure!.Code);
        Assert.Same(before, harness.Session.Snapshot);
    }

    /// <summary>Assigns independent output intentions without approving plans or invoking any media work.</summary>
    private static void SetDestinations(BatchTestHarness harness)
    {
        foreach (var item in harness.Session.Snapshot.Items)
        {
            Assert.True(harness.Session.SetDestination(new(item.Id, Path.ChangeExtension(item.SourcePath, ".trimmed.mkv"))).Applied);
        }
    }
}
