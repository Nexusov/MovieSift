using System.Collections.Immutable;
using MovieSift.Application.Batches;
using MovieSift.Core.Failures;
using MovieSift.Core.Media;
using MovieSift.Core.Planning;
using Xunit;

namespace MovieSift.Application.Tests.Batches;

/// <summary>Protects accepted folder identities and reviews independently from filesystem discovery.</summary>
public sealed class BatchFolderProvenanceTests
{
    /// <summary>Never rewrites an accepted row when a duplicate arrives through another picker/root or hardlink identity.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedRowWinsAcrossExplicitFolderAndIdentityAliases(bool firstAddedThroughFolder)
    {
        var harness = new BatchTestHarness(1);
        var original = Assert.Single(harness.Session.Snapshot.Items).DiscoveredSource!;
        Assert.True(harness.Coordinator.StartNewBatch().Applied);
        var originalRoot = Path.GetDirectoryName(original.FullPath)!;
        var origins = firstAddedThroughFolder
            ? ImmutableDictionary<string, FolderSourceOrigin>.Empty.Add(original.FullPath, new(originalRoot, Path.GetFileName(original.FullPath)))
            : null;
        Assert.True(harness.Session.AddDiscoveryResult(new([original], [], FolderOrigins: origins)).Applied);
        await harness.PrepareAsync();
        var before = harness.Session.Snapshot;
        var aliasRoot = Path.Combine(Path.GetTempPath(), "other selected root");
        var alias = original with { FullPath = Path.Combine(aliasRoot, "Season 3", "alias.mkv") };
        var addition = new BatchInputDiscoveryResult([alias, original], [], FolderOrigins:
            ImmutableDictionary<string, FolderSourceOrigin>.Empty
                .Add(alias.FullPath, new(aliasRoot, Path.Combine("Season 3", "alias.mkv")))
                .Add(original.FullPath, new(Path.GetPathRoot(original.FullPath)!, Path.GetRelativePath(Path.GetPathRoot(original.FullPath)!, original.FullPath))));

        Assert.True(harness.Session.AddDiscoveryResult(addition).Applied);
        Assert.True(harness.Session.AddSources([original]).Applied);

        Assert.Same(before, harness.Session.Snapshot);
        Assert.Single(before.Items);
        Assert.Equal(firstAddedThroughFolder, before.Items[0].FolderOrigin is not null);
        Assert.NotNull(before.Review);
        Assert.NotNull(before.Items[0].ApprovedPlan);
        Assert.Empty(harness.RemuxedPaths);
    }

    /// <summary>Reports identical relative outputs from different roots through the existing aggregate collision checks.</summary>
    [Fact]
    public async Task DifferentSourceRootsDoNotSilentlyRenameCollidingRelativeDestinations()
    {
        var harness = new BatchTestHarness();
        var templates = harness.Inspections.Values.ToArray();
        Assert.True(harness.Coordinator.StartNewBatch().Applied);
        var sources = ImmutableArray.CreateBuilder<SourceRevision>();
        var origins = ImmutableDictionary.CreateBuilder<string, FolderSourceOrigin>();
        var relative = Path.Combine("Season 1", "episode.mkv");

        for (var index = 0; index < templates.Length; index++)
        {
            var root = Path.Combine(Path.GetTempPath(), $"separate series root {index}");
            var path = Path.Combine(root, relative);
            var inspection = templates[index] with { Source = templates[index].Source with { FullPath = path } };
            harness.Inspections.Add(path, inspection);
            harness.Revisions.Add(path, inspection.Source);
            sources.Add(inspection.Source);
            origins.Add(path, new(root, relative));
        }

        Assert.True(harness.Session.AddDiscoveryResult(new(sources.ToImmutable(), [], FolderOrigins: origins.ToImmutable())).Applied);
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        var outputRoot = Path.Combine(Path.GetTempPath(), "shared output root");

        foreach (var item in harness.Session.Snapshot.Items)
        {
            var destination = OutputPathPolicy.DeriveFolderDestination(new(outputRoot, item.FolderOrigin!.RelativePath));
            Assert.Null(destination.Failure);
            var retained = item.Inspection!.Tracks.Where(track => track.Kind != TrackKind.Subtitles)
                .Select(track => track.Id).ToImmutableArray();
            Assert.True(harness.Session.SetSelection(new(item.Id, retained)).Applied);
            Assert.True(harness.Session.SetDestination(new(item.Id, destination.DestinationPath!, outputRoot)).Applied);
        }

        var review = await harness.Coordinator.ReviewSelectionsAsync(new(harness.Session.Snapshot.Revision, [], TestContext.Current.CancellationToken));

        Assert.Null(review.Review);
        Assert.Equal(2, review.ItemFailures.Length);
        Assert.All(review.ItemFailures, item => Assert.Equal(ErrorCode.InvalidDestination, item.Failure.Code));
        Assert.Empty(harness.RemuxedPaths);
        Assert.Empty(harness.Reservations);
    }

    /// <summary>Does not grant missing-parent directory creation to an explicit-file row without folder provenance.</summary>
    [Fact]
    public async Task ExplicitRowCannotAcquireFolderDirectoryCreationScopeThroughApproval()
    {
        var harness = new BatchTestHarness(1);
        await harness.PrepareAsync();
        var before = harness.Session.Snapshot;
        var item = Assert.Single(before.Items);
        var outputRoot = Path.Combine(Path.GetTempPath(), "unapproved output root");
        var destination = Path.Combine(outputRoot, "nested", "episode.trimmed.mkv");
        var result = harness.Session.SetDestination(new(item.Id, destination, outputRoot));

        Assert.False(result.Applied);
        Assert.Equal(ErrorCode.InvalidDestination, result.Failure!.Code);
        Assert.Same(before, harness.Session.Snapshot);
        Assert.Empty(harness.RemuxedPaths);
    }
}
