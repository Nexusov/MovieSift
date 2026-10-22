using System.Collections.Immutable;
using TrackTrim.Application.Batches;
using TrackTrim.Application.Inspection;
using TrackTrim.Application.Jobs;
using TrackTrim.Application.QueueLists;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Planning;
using Xunit;

namespace TrackTrim.Application.Tests.Batches;

/// <summary>Separates recoverable listing intentions from all live inspection, approval and media ownership authority.</summary>
public sealed class BatchQueueListTests
{
    /// <summary>Restores paths, inclusion and provenance through fresh instances without starting any inspection or job.</summary>
    [Fact]
    public async Task FreshSessionRestoresOnlyListingAndRequiresNewInspectionApprovalAndReview()
    {
        var writer = new BatchTestHarness();
        var original = writer.Session.Snapshot.Items;
        Assert.True(writer.Coordinator.StartNewBatch().Applied);
        var firstSource = original[0].DiscoveredSource!;
        var root = Path.GetDirectoryName(firstSource.FullPath)!;
        var origin = new FolderSourceOrigin(root, Path.GetFileName(firstSource.FullPath));
        Assert.True(writer.Session.AddDiscoveryResult(new([firstSource, original[1].DiscoveredSource!], [],
            FolderOrigins: ImmutableDictionary<string, FolderSourceOrigin>.Empty.Add(firstSource.FullPath, origin))).Applied);
        await writer.PrepareAsync();
        var before = writer.Session.Snapshot;
        var oldFirst = before.Items[0];
        var destinationRoot = Path.Combine(Path.GetTempPath(), "restored outputs Кириллица");
        var nestedDestination = Path.Combine(destinationRoot, "Season 1", "episode.trimmed.mkv");
        Assert.True(writer.Session.SetDestination(new(oldFirst.Id, nestedDestination, destinationRoot)).Applied);
        Assert.True(writer.Session.SetIncluded(before.Items[1].Id, false).Applied);
        var listing = writer.Session.CaptureQueueList();
        Assert.Equal(2, listing.Entries.Length);
        Assert.Equal(origin, listing.Entries[0].FolderOrigin);
        Assert.Equal(nestedDestination, listing.Entries[0].DestinationPath);
        Assert.Equal(destinationRoot, listing.Entries[0].OutputDirectoryRoot);
        Assert.False(listing.Entries[1].Included);

        var reader = new BatchTestHarness();
        Assert.True(reader.Coordinator.StartNewBatch().Applied);
        var empty = reader.Session.Snapshot;
        var restored = reader.Coordinator.RestoreQueueList(listing, empty.Revision);
        Assert.True(restored.Applied);
        var pending = reader.Session.Snapshot;
        Assert.Equal(empty.Revision + 1, pending.Revision);
        Assert.Equal(BatchPhase.Preparing, pending.Phase);
        Assert.Null(pending.Review);
        Assert.Null(pending.Failure);
        Assert.Equal(2, pending.Items.Length);
        Assert.All(pending.Items, item =>
        {
            Assert.DoesNotContain(item.Id, before.Items.Select(previous => previous.Id));
            Assert.Null(item.DiscoveredSource);
            Assert.Null(item.Inspection);
            Assert.Null(item.ApprovedPlan);
            Assert.Null(item.Result);
            Assert.Null(item.Failure);
            Assert.Empty(item.CurrentRetainedTrackIds);
            Assert.Equal(BatchItemState.PendingInspection, item.State);
        });
        Assert.Equal(origin, pending.Items[0].FolderOrigin);
        Assert.Equal(nestedDestination, pending.Items[0].IntendedDestinationPath);
        Assert.Equal(destinationRoot, pending.Items[0].IntendedOutputDirectoryRoot);
        Assert.False(pending.Items[1].Included);
        Assert.Empty(reader.InspectedPaths);
        Assert.Empty(reader.RemuxedPaths);
        Assert.Empty(reader.DestinationValidations);
        Assert.Empty(reader.Reservations);
        Assert.Equal(0, reader.CleanupCalls);
        Assert.False(reader.Session.ApprovePlan(new(pending.Items[0].Id, oldFirst.ApprovedPlan!)).Applied);
        Assert.Null((await reader.Coordinator.ReviewAsync(TestContext.Current.CancellationToken)).Review);
        Assert.Equal(ErrorCode.StalePlan,
            (await reader.Coordinator.RunAsync(new(before.Review!.Id))).Failure!.Code);
        Assert.Empty(reader.RemuxedPaths);

        await reader.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        var inspected = reader.Session.Snapshot.Items[0];
        Assert.NotNull(inspected.Inspection);
        Assert.Equal(inspected.Inspection!.Tracks.Select(track => track.Id), inspected.CurrentRetainedTrackIds);
        Assert.Null(inspected.ApprovedPlan);
        Assert.Equal(nestedDestination, inspected.IntendedDestinationPath);
        Assert.Null((await reader.Coordinator.ReviewAsync(TestContext.Current.CancellationToken)).Review);
        var plan = PlanBuilder.Build(new(inspected.Inspection, inspected.CurrentRetainedTrackIds,
            inspected.IntendedDestinationPath!, false, inspected.IntendedOutputDirectoryRoot)).Plan!;
        Assert.True(reader.Session.ApprovePlan(new(inspected.Id, plan)).Applied);
        Assert.NotNull((await reader.Coordinator.ReviewAsync(TestContext.Current.CancellationToken)).Review);
        Assert.Empty(reader.RemuxedPaths);
    }

    /// <summary>Preserves a reviewed snapshot for exact destination no-ops and invalidates only an actually edited row.</summary>
    [Fact]
    public async Task DestinationIntentUsesTheExistingChangedOnlyApprovalBoundary()
    {
        var harness = new BatchTestHarness();
        await harness.PrepareAsync();
        var before = harness.Session.Snapshot;
        var first = before.Items[0];
        var second = before.Items[1];
        Assert.Equal(first.ApprovedPlan!.DestinationPath, first.IntendedDestinationPath);
        Assert.True(harness.Session.SetDestination(new(first.Id, first.IntendedDestinationPath!)).Applied);
        Assert.Same(before, harness.Session.Snapshot);
        var changedDestination = Path.Combine(Path.GetTempPath(), "explicit new destination.mkv");
        Assert.True(harness.Session.SetDestination(new(first.Id, changedDestination)).Applied);
        var changed = harness.Session.Snapshot;
        Assert.Null(changed.Review);
        Assert.Null(changed.Items[0].ApprovedPlan);
        Assert.Equal(first.CurrentRetainedTrackIds, changed.Items[0].CurrentRetainedTrackIds);
        Assert.Same(first.Inspection, changed.Items[0].Inspection);
        Assert.Same(second, changed.Items[1]);
        Assert.Equal(changedDestination, harness.Session.CaptureQueueList().Entries[0].DestinationPath);
        var unsupportedRoot = harness.Session.SetDestination(new(first.Id, changedDestination, Path.GetTempPath()));
        Assert.False(unsupportedRoot.Applied);
        Assert.Equal(ErrorCode.InvalidDestination, unsupportedRoot.Failure!.Code);
        Assert.Same(changed, harness.Session.Snapshot);
    }

    /// <summary>Destination editing retains an uninspected discovery failure instead of inventing a pending usable source.</summary>
    [Fact]
    public async Task DestinationIntentPreservesUninspectedFailureWhileInvalidatingAggregateReview()
    {
        var harness = new BatchTestHarness(1);
        var sourcePath = Path.Combine(Path.GetTempPath(), "missing recoverable source.mkv");
        var discoveryFailure = new Failure(ErrorCode.InvalidSource, "Controlled failed discovery entry.");
        Assert.True(harness.Session.AddDiscoveryResult(new([], [new(sourcePath, discoveryFailure)])).Applied);
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        var valid = harness.Session.Snapshot.Items[0];
        harness.Approve(valid);
        var failed = Assert.Single(harness.Session.Snapshot.Items, item => item.SourcePath == sourcePath);
        Assert.True(harness.Session.SetIncluded(failed.Id, false).Applied);
        Assert.NotNull((await harness.Coordinator.ReviewAsync(TestContext.Current.CancellationToken)).Review);
        var reviewed = harness.Session.Snapshot;
        var destination = Path.Combine(Path.GetTempPath(), "missing source intended output.mkv");

        Assert.True(harness.Session.SetDestination(new(failed.Id, destination)).Applied);

        var changed = harness.Session.Snapshot;
        var retainedFailure = Assert.Single(changed.Items, item => item.Id == failed.Id);
        Assert.Equal(BatchItemState.Failed, retainedFailure.State);
        Assert.Same(discoveryFailure, retainedFailure.Failure);
        Assert.Null(retainedFailure.Inspection);
        Assert.Null(retainedFailure.ApprovedPlan);
        Assert.False(retainedFailure.Included);
        Assert.Equal(destination, retainedFailure.IntendedDestinationPath);
        Assert.Equal(destination, Assert.Single(harness.Session.CaptureQueueList().Entries,
            entry => entry.SourcePath == sourcePath).DestinationPath);
        Assert.Null(changed.Review);
        Assert.Same(reviewed.Items[0], changed.Items[0]);
        Assert.Empty(harness.RemuxedPaths);
    }

    /// <summary>Never proposes an already published result again, including one whose final temporary cleanup failed.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureExcludesPublishedOutputsDespiteLaterFailureOrCleanup(bool cleanupFails)
    {
        var harness = new BatchTestHarness();
        var review = await harness.PrepareAsync();
        var secondPath = harness.Session.Snapshot.Items[1].SourcePath;
        harness.CleanupFails = cleanupFails;
        harness.OnRemux = request => request.Plan.Inspection.Source.FullPath == secondPath
            ? throw new OperationFailureException(new(ErrorCode.ToolFailed, "controlled later-item failure"))
            : Task.CompletedTask;
        var result = await harness.Coordinator.RunAsync(new(review.Id));
        Assert.NotNull(result.Items[0].Result!.PublishedPath);
        var listing = harness.Session.CaptureQueueList();
        Assert.Equal(secondPath, Assert.Single(listing.Entries).SourcePath);
        Assert.Single(harness.PublishedPaths);
        Assert.Equal(cleanupFails, result.HasSafetyBlock);
    }

    /// <summary>A post-publication presentation failure cannot turn a published file into a recoverable job.</summary>
    [Fact]
    public async Task CaptureExcludesFailedStateWhosePublicationAlreadySucceeded()
    {
        var harness = new BatchTestHarness(1);
        var review = await harness.PrepareAsync();
        var result = await harness.Coordinator.RunAsync(new(review.Id, new CompletedProgressFailure(),
            TestContext.Current.CancellationToken));
        var failed = Assert.Single(result.Items);
        Assert.Equal(BatchItemState.Failed, failed.State);
        Assert.Equal(ErrorCode.IoFailure, failed.Result!.Failure!.Code);
        Assert.NotNull(failed.Result.PublishedPath);
        Assert.Empty(harness.Session.CaptureQueueList().Entries);
        Assert.Single(harness.PublishedPaths);
        Assert.Equal(1, harness.CleanupCalls);
    }

    /// <summary>Leaves no-change rows out of recovery and performs no I/O while capturing a finished session.</summary>
    [Fact]
    public async Task CompletedAndNoChangeRowsDoNotReappearInASavedList()
    {
        var harness = new BatchTestHarness(1);
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        harness.Approve(harness.Session.Snapshot.Items[0], noChange: true);
        var review = (await harness.Coordinator.ReviewAsync(TestContext.Current.CancellationToken)).Review!;
        await harness.Coordinator.RunAsync(new(review.Id));
        Assert.Empty(harness.Session.CaptureQueueList().Entries);
        Assert.Empty(harness.RemuxedPaths);
        Assert.Empty(harness.PublishedPaths);
        Assert.Equal(0, harness.CleanupCalls);
    }

    /// <summary>Rejects invalid, nonempty and stale recoveries atomically without erasing current list or review state.</summary>
    [Fact]
    public async Task RestoreRejectsInvalidOrIncompatibleSessionWithoutAcceptingASubset()
    {
        var harness = new BatchTestHarness();
        await harness.PrepareAsync();
        var approved = harness.Session.Snapshot;
        var listing = harness.Session.CaptureQueueList();
        Assert.False(harness.Coordinator.RestoreQueueList(listing, approved.Revision).Applied);
        Assert.Same(approved, harness.Session.Snapshot);
        Assert.True(harness.Coordinator.StartNewBatch().Applied);
        var empty = harness.Session.Snapshot;
        Assert.False(harness.Coordinator.RestoreQueueList(listing, empty.Revision - 1).Applied);
        Assert.Same(empty, harness.Session.Snapshot);
        var invalid = new QueueList([listing.Entries[0], new("relative path.mkv", null)]);
        Assert.Equal(ErrorCode.InvalidSelection, harness.Coordinator.RestoreQueueList(invalid).Failure!.Code);
        Assert.Same(empty, harness.Session.Snapshot);
        Assert.False(harness.Coordinator.RestoreQueueList(new(default)).Applied);
        Assert.Same(empty, harness.Session.Snapshot);
        Assert.Empty(harness.RemuxedPaths);
        Assert.Equal(0, harness.CleanupCalls);
    }

    /// <summary>Rejects both an active coordinator and another dispatcher's busy session without disturbing late inspection ownership.</summary>
    [Fact]
    public async Task ActiveInspectionPreventsRestoreUntilItsAwaitedCompletion()
    {
        var harness = new BatchTestHarness(1);
        var listing = harness.Session.CaptureQueueList();
        var entered = BatchTestHarness.Signal();
        var release = BatchTestHarness.Signal();
        harness.OnInspect = async request => { entered.TrySetResult(); await release.Task.WaitAsync(request.CancellationToken); };
        var inspection = harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);

        try
        {
            await BatchTestHarness.Await(entered.Task);
            var active = harness.Session.Snapshot;
            var other = new BatchCoordinator(harness.Session, new InspectionService(harness, harness),
                new JobCoordinator(harness, harness, harness), harness);
            Assert.Equal(ErrorCode.Busy, harness.Coordinator.RestoreQueueList(listing).Failure!.Code);
            Assert.Equal(ErrorCode.Busy, other.RestoreQueueList(listing).Failure!.Code);
            Assert.Same(active, harness.Session.Snapshot);
        }
        finally
        {
            release.TrySetResult();
            await BatchTestHarness.Await(inspection);
        }

        var oldItem = harness.Session.Snapshot.Items[0];
        Assert.True(harness.Coordinator.StartNewBatch().Applied);
        Assert.True(harness.Coordinator.RestoreQueueList(listing).Applied);
        var restored = harness.Session.Snapshot;
        Assert.Null(restored.Items[0].Inspection);
        Assert.False(harness.Session.SetSelection(new(oldItem.Id, oldItem.CurrentRetainedTrackIds)).Applied);
        Assert.Same(restored, harness.Session.Snapshot);
    }

    /// <summary>Preserves warning candidates and unsafe job evidence; listing recovery never substitutes for cleanup.</summary>
    [Theory]
    [InlineData("warning", ErrorCode.Busy)]
    [InlineData("cleanup", ErrorCode.OwnershipInvalid)]
    [InlineData("termination", ErrorCode.ProcessTerminationFailed)]
    [InlineData("publication", ErrorCode.PublicationUncertain)]
    public async Task UnresolvedJobOwnershipRejectsRestoreWithoutInvokingCleanup(string boundary, ErrorCode code)
    {
        var harness = new BatchTestHarness(1);
        var review = await harness.PrepareAsync();
        var listing = harness.Session.CaptureQueueList();

        switch (boundary)
        {
            case "warning":
                harness.ExitCodes[harness.Session.Snapshot.Items[0].SourcePath] = 1;
                break;
            case "cleanup":
                harness.CleanupFails = true;
                break;
            case "termination":
                harness.RemuxFailure = new(ErrorCode.ProcessTerminationFailed);
                break;
            case "publication":
                harness.PublicationFailure = new(ErrorCode.PublicationUncertain);
                break;
        }

        var stopped = await harness.Coordinator.RunAsync(new(review.Id));
        var cleanupCalls = harness.CleanupCalls;
        var result = harness.Coordinator.RestoreQueueList(listing);
        Assert.False(result.Applied);
        Assert.Equal(code, result.Failure!.Code);
        Assert.Same(stopped, harness.Session.Snapshot);
        Assert.Equal(cleanupCalls, harness.CleanupCalls);
        Assert.Single(harness.RemuxedPaths);
    }

    /// <summary>Freshly inspects a changed source and checks later source/collision changes through normal Review.</summary>
    [Fact]
    public async Task RestoredSourcesReceiveFreshIdentityAndNormalLateValidation()
    {
        var harness = new BatchTestHarness();
        await harness.PrepareAsync();
        var old = harness.Session.Snapshot;
        var listing = harness.Session.CaptureQueueList();
        Assert.True(harness.Coordinator.StartNewBatch().Applied);
        var changedPath = old.Items[0].SourcePath;
        var changed = harness.Revisions[changedPath] with { FileIdentity = "replacement identity", Length = 999 };
        harness.Revisions[changedPath] = changed;
        harness.Inspections[changedPath] = harness.Inspections[changedPath] with { Source = changed };
        Assert.True(harness.Coordinator.RestoreQueueList(listing).Applied);
        await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        var items = harness.Session.Snapshot.Items;
        Assert.Equal(changed, items[0].Inspection!.Source);
        Assert.NotEqual(old.Items[0].Inspection!.Source, items[0].Inspection!.Source);
        Assert.Null(items[0].ApprovedPlan);
        harness.Approve(items[0]);
        var secondPlan = harness.Approve(items[1]);
        harness.Revisions[changedPath] = changed with { Length = changed.Length + 1 };
        harness.DestinationFailures[secondPlan.DestinationPath] = new(ErrorCode.DestinationExists);
        var reviewed = await harness.Coordinator.ReviewAsync(TestContext.Current.CancellationToken);
        Assert.Null(reviewed.Review);
        Assert.Equal([ErrorCode.SourceChanged, ErrorCode.DestinationExists], reviewed.ItemFailures.Select(item => item.Failure.Code));
        Assert.Empty(harness.RemuxedPaths);
    }

    /// <summary>A missing restored source is a visible inspection failure and cannot produce an approved executable plan.</summary>
    [Fact]
    public async Task MissingRestoredSourceRemainsVisibleWithoutInventedTracks()
    {
        var harness = new BatchTestHarness(1);
        var listing = harness.Session.CaptureQueueList();
        Assert.True(harness.Coordinator.StartNewBatch().Applied);
        harness.OnInspect = _ => throw new OperationFailureException(new(ErrorCode.InvalidSource, "controlled missing source"));
        Assert.True(harness.Coordinator.RestoreQueueList(listing).Applied);
        var inspected = await harness.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        var item = Assert.Single(inspected.Items);
        Assert.Equal(listing.Entries[0].SourcePath, item.SourcePath);
        Assert.Equal(BatchItemState.Failed, item.State);
        Assert.Equal(ErrorCode.InvalidSource, item.Failure!.Code);
        Assert.Null(item.Inspection);
        Assert.Null(item.ApprovedPlan);
        Assert.Empty(item.CurrentRetainedTrackIds);
        Assert.Null((await harness.Coordinator.ReviewAsync(TestContext.Current.CancellationToken)).Review);
        Assert.Empty(harness.RemuxedPaths);
        Assert.Equal(0, harness.CleanupCalls);
    }

    /// <summary>Invalidates issued proposals and old row callbacks across New batch plus restoration.</summary>
    [Fact]
    public async Task NewBatchAndRestoreCannotReuseOldPreviewOrItemAuthority()
    {
        var harness = new BatchTestHarness(1);
        await harness.PrepareAsync();
        var old = harness.Session.Snapshot;
        var preview = harness.Coordinator.PreviewLanguageRules(BatchLanguageTestData.Unchanged).Preview!;
        var listing = harness.Session.CaptureQueueList();
        Assert.True(harness.Coordinator.StartNewBatch().Applied);
        Assert.True(harness.Coordinator.RestoreQueueList(listing).Applied);
        var restored = harness.Session.Snapshot;
        Assert.False(harness.Coordinator.ApplyLanguageRules(preview).Applied);
        Assert.False(harness.Session.ApprovePlan(new(old.Items[0].Id, old.Items[0].ApprovedPlan!)).Applied);
        Assert.False(harness.Session.SetDestination(new(old.Items[0].Id, old.Items[0].IntendedDestinationPath!)).Applied);
        Assert.Same(restored, harness.Session.Snapshot);
        Assert.Empty(harness.RemuxedPaths);
        Assert.Empty(harness.PublishedPaths);
    }

    /// <summary>Fails only after actual controlled publication, without changing the recorded media-job result.</summary>
    private sealed class CompletedProgressFailure : IProgress<BatchProgress>
    {
        /// <summary>Exercises the existing job owner's preservation of publication facts after a late consumer failure.</summary>
        public void Report(BatchProgress value)
        {
            if (value.Job.Phase == JobPhase.Completed)
            {
                throw new IOException("Controlled post-publication presentation failure.");
            }
        }
    }
}
