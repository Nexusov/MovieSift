using System.Collections.Immutable;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Media;
using TrackTrim.Core.Planning;

namespace TrackTrim.Application.Batches;

/// <summary>Stores immutable per-file approvals and results for one non-persistent batch.</summary>
public sealed class BatchSession
{
    public const int MaximumItems = BatchDiscoveryLimits.MaximumFiles;
    private readonly object synchronization = new();
    private BatchSnapshot snapshot = new(0, BatchPhase.Preparing, []);

    public event Action<BatchSnapshot>? Changed;

    public BatchSnapshot Snapshot
    {
        get
        {
            lock (synchronization)
            {
                return snapshot;
            }
        }
    }

    /// <summary>Starts an empty session or accepts a bounded set of discovered identities.</summary>
    public BatchSession(IEnumerable<SourceRevision>? sources = null)
    {
        if (sources is null)
        {
            return;
        }

        var result = AddSources(sources);

        if (!result.Applied)
        {
            snapshot = snapshot with { Failure = result.Failure };
        }
    }

    /// <summary>Adds valid sources and visible failed discovery rows without accepting a rejected subset.</summary>
    public BatchEditResult AddDiscoveryResult(BatchInputDiscoveryResult result)
    {
        if (result.Rejection is not null)
        {
            return new(false, result.Rejection);
        }

        var sources = result.Sources.Select(source => new BatchItem(Guid.NewGuid(), source.FullPath, source));
        var issues = result.Issues.Select(issue => new BatchItem(Guid.NewGuid(), issue.Path, null,
            State: BatchItemState.Failed, Failure: issue.Failure));
        return AddItems(sources.Concat(issues));
    }

    /// <summary>Adds independently discovered files, deduplicating canonical paths and file identities.</summary>
    public BatchEditResult AddSources(IEnumerable<SourceRevision> sources) => AddItems(
        sources.Select(source => new BatchItem(Guid.NewGuid(), source.FullPath, source)));

    /// <summary>Explicitly includes or excludes a row without changing its inspection or result.</summary>
    public BatchEditResult SetIncluded(Guid itemId, bool included) => EditItem(itemId, item =>
        new(item with { Included = included }, null));

    /// <summary>Removes an unstarted row; completed evidence stays in the session.</summary>
    public BatchEditResult Remove(Guid itemId)
    {
        BatchSnapshot updated;

        lock (synchronization)
        {
            var failure = EditFailure();
            var item = snapshot.Items.FirstOrDefault(item => item.Id == itemId);

            if (failure is not null || item is null)
            {
                return new(false, failure ?? new(ErrorCode.StalePlan));
            }

            if (item.Result is not null)
            {
                return new(false, new(ErrorCode.InvalidSelection));
            }

            updated = Edited(snapshot.Items.Remove(item));
        }

        Changed?.Invoke(updated);
        return new(true);
    }

    /// <summary>Accepts a newly reviewed plan only for the matching immutable inspection.</summary>
    public BatchEditResult ApprovePlan(BatchPlanApprovalRequest request) => EditItem(request.ItemId, item =>
    {
        var invalidPlan = item.Inspection is null || request.Plan.Inspection != item.Inspection ||
            item.Result?.JobId == request.Plan.Id;

        if (invalidPlan)
        {
            return new(null, new(ErrorCode.StalePlan));
        }

        var duplicateIdentity = request.Plan.Id == Guid.Empty || snapshot.Items.Any(other =>
            other.Id != item.Id && other.ApprovedPlan?.Id == request.Plan.Id);

        if (duplicateIdentity)
        {
            return new(null, new(ErrorCode.InvalidSelection));
        }

        var rebuilt = PlanBuilder.Build(new(item.Inspection!, request.Plan.RetainedTracks.Select(track => track.Id).ToArray(),
            request.Plan.DestinationPath, request.Plan.ZeroAudioConfirmed));

        if (rebuilt.Plan is null)
        {
            return new(null, rebuilt.Failure);
        }

        if (!rebuilt.Plan.RetainedTracks.SequenceEqual(request.Plan.RetainedTracks))
        {
            return new(null, new(ErrorCode.InvalidSelection));
        }

        return new(item with { ApprovedPlan = request.Plan, State = BatchItemState.Ready, Result = null, Failure = null }, null);
    });

    /// <summary>Invalidates only the affected selection or destination approval.</summary>
    public BatchEditResult InvalidatePlan(Guid itemId) => EditItem(itemId, item => new(item with
    {
        ApprovedPlan = null,
        State = item.Inspection is null ? BatchItemState.PendingInspection : BatchItemState.NeedsReview,
        Failure = null
    }, null));

    /// <summary>Atomically clears safe in-memory state after the coordinator has acquired its operation gate.</summary>
    internal BatchEditResult Reset(long? expectedRevision)
    {
        BatchSnapshot updated;

        lock (synchronization)
        {
            var failure = EditFailure();

            if (failure is not null)
            {
                return new(false, DescribeResetFailure(failure));
            }

            if (expectedRevision is { } revision && revision != snapshot.Revision)
            {
                return new(false, new(ErrorCode.StalePlan));
            }

            updated = snapshot = new(snapshot.Revision + 1, BatchPhase.Preparing, []);
        }

        Changed?.Invoke(updated);
        return new(true);
    }

    /// <summary>Claims an inspection or review operation while preventing conflicting edits.</summary>
    internal bool TryBegin(BatchPhase phase, out Failure? failure)
    {
        BatchSnapshot updated;

        lock (synchronization)
        {
            failure = EditFailure();

            if (failure is not null)
            {
                return false;
            }

            updated = snapshot = snapshot with { Phase = phase, Review = null, Failure = null };
        }

        Changed?.Invoke(updated);
        return true;
    }

    /// <summary>Consumes exactly the latest reviewed batch before dispatching any job.</summary>
    internal BatchReview? TakeReview(Guid reviewId)
    {
        BatchSnapshot updated;
        BatchReview review;

        lock (synchronization)
        {
            var invalidReview = EditFailure() is not null || snapshot.Review is null ||
                snapshot.Review.Id != reviewId || snapshot.Review.Revision != snapshot.Revision;

            if (invalidReview)
            {
                return null;
            }

            review = snapshot.Review!;
            updated = snapshot = snapshot with
            {
                Phase = BatchPhase.Running,
                Review = null,
                PauseRequested = false,
                PauseReason = BatchPauseReason.None,
                Failure = null
            };
        }

        Changed?.Invoke(updated);
        return review;
    }

    /// <summary>Publishes a coordinator-owned state transition as a complete immutable snapshot.</summary>
    internal void Update(Func<BatchSnapshot, BatchSnapshot> change)
    {
        BatchSnapshot updated;

        lock (synchronization)
        {
            updated = snapshot = change(snapshot);
        }

        Changed?.Invoke(updated);
    }

    /// <summary>Stops future dispatch while leaving the active job's ownership unchanged.</summary>
    internal void RequestPause()
    {
        Update(current => current.Phase == BatchPhase.Running
            ? current with { PauseRequested = true } : current);
    }

    /// <summary>Rejects edits while work or unresolved candidate ownership is present.</summary>
    private Failure? EditFailure()
    {
        if (snapshot.HasSafetyBlock)
        {
            return new(ErrorCode.OwnershipInvalid);
        }

        var busy = snapshot.Phase is BatchPhase.Inspecting or BatchPhase.Reviewing or BatchPhase.Running ||
            snapshot.Items.Any(item => item.State == BatchItemState.AwaitingWarningReview);

        return busy ? new(ErrorCode.Busy) : null;
    }

    /// <summary>Preserves a concrete ownership failure instead of replacing it with a generic reset refusal.</summary>
    private Failure DescribeResetFailure(Failure fallback)
    {
        if (fallback.Code != ErrorCode.OwnershipInvalid)
        {
            return fallback;
        }

        var ownershipFailure = snapshot.Items.SelectMany(item => new[] { item.Failure, item.Result?.Failure })
            .FirstOrDefault(failure => failure?.Code is ErrorCode.ProcessTerminationFailed or ErrorCode.PublicationUncertain);
        return ownershipFailure ?? (snapshot.PauseReason == BatchPauseReason.UnsafeOwnership ? snapshot.Failure : null) ?? fallback;
    }

    /// <summary>Adds all unique rows atomically or rejects the entire addition at the session limit.</summary>
    private BatchEditResult AddItems(IEnumerable<BatchItem> additions)
    {
        BatchSnapshot updated;

        lock (synchronization)
        {
            var failure = EditFailure();

            if (failure is not null)
            {
                return new(false, failure);
            }

            var items = snapshot.Items.ToBuilder();
            var paths = items.Select(item => CanonicalPath(item.SourcePath)).ToHashSet(PathComparer);
            var identities = items.Where(item => item.DiscoveredSource is not null)
                .Select(item => item.DiscoveredSource!.FileIdentity).ToHashSet(StringComparer.Ordinal);

            foreach (var item in additions)
            {
                var path = CanonicalPath(item.SourcePath);
                var duplicate = paths.Contains(path) || item.DiscoveredSource is { } source && identities.Contains(source.FileIdentity);

                if (duplicate)
                {
                    continue;
                }

                if (items.Count == MaximumItems)
                {
                    return new(false, new(ErrorCode.InvalidSource, Reason: ErrorReason.BatchFileLimit));
                }

                paths.Add(path);

                if (item.DiscoveredSource is not null)
                {
                    identities.Add(item.DiscoveredSource.FileIdentity);
                }

                items.Add(item);
            }

            if (items.Count == snapshot.Items.Length)
            {
                return new(true);
            }

            updated = Edited(items.ToImmutable());
        }

        Changed?.Invoke(updated);
        return new(true);
    }

    /// <summary>Applies a local edit while preserving completed jobs and unsafe ownership evidence.</summary>
    private BatchEditResult EditItem(Guid itemId, Func<BatchItem, ItemEdit> edit)
    {
        BatchSnapshot updated;

        lock (synchronization)
        {
            var failure = EditFailure();
            var item = snapshot.Items.FirstOrDefault(item => item.Id == itemId);

            if (failure is not null || item is null)
            {
                return new(false, failure ?? new(ErrorCode.StalePlan));
            }

            if (item.State is BatchItemState.Completed or BatchItemState.NoChange)
            {
                return new(false, new(ErrorCode.InvalidSelection));
            }

            var result = edit(item);

            if (result.Item is null)
            {
                return new(false, result.Failure ?? new(ErrorCode.InvalidSelection));
            }

            updated = Edited(snapshot.Items.Replace(item, result.Item));
        }

        Changed?.Invoke(updated);
        return new(true);
    }

    /// <summary>Invalidates batch review whenever an input or approval changes.</summary>
    private BatchSnapshot Edited(ImmutableArray<BatchItem> items) => snapshot = snapshot with
    {
        Revision = snapshot.Revision + 1,
        Items = items,
        Review = null,
        Failure = null,
        Phase = snapshot.Phase == BatchPhase.Paused ? BatchPhase.Paused : BatchPhase.Preparing
    };

    /// <summary>Compares local paths using the platform's ordinary path casing policy.</summary>
    internal static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Normalizes lexical aliases without opening or modifying a source.</summary>
    internal static string CanonicalPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private sealed record ItemEdit(BatchItem? Item, Failure? Failure);
}
