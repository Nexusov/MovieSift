using System.Collections.Immutable;
using TrackTrim.Application.QueueLists;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Media;
using TrackTrim.Core.Planning;
using TrackTrim.Core.Rules;

namespace TrackTrim.Application.Batches;

/// <summary>Stores live per-file authority separately from recoverable source and destination intentions.</summary>
public sealed partial class BatchSession
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

        var sources = result.Sources.Select(source => new BatchItem(Guid.NewGuid(), source.FullPath, source,
            FolderOrigin: result.FolderOrigins?.GetValueOrDefault(source.FullPath)));
        var issues = result.Issues.Select(issue => new BatchItem(Guid.NewGuid(), issue.Path, null,
            State: BatchItemState.Failed, Failure: issue.Failure,
            FolderOrigin: result.FolderOrigins?.GetValueOrDefault(issue.Path)));
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
            item.Result?.JobId == request.Plan.Id ||
            request.Plan.OutputDirectoryRoot is not null && item.FolderOrigin is null;

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
            request.Plan.DestinationPath, request.Plan.ZeroAudioConfirmed, request.Plan.OutputDirectoryRoot));

        if (rebuilt.Plan is null)
        {
            return new(null, rebuilt.Failure);
        }

        if (!rebuilt.Plan.RetainedTracks.SequenceEqual(request.Plan.RetainedTracks))
        {
            return new(null, new(ErrorCode.InvalidSelection));
        }

        return new(item with
        {
            ApprovedPlan = request.Plan,
            State = BatchItemState.Ready,
            Result = null,
            Failure = null,
            RetainedTrackIds = request.Plan.RetainedTracks.Select(track => track.Id).ToImmutableArray(),
            IntendedDestinationPath = request.Plan.DestinationPath,
            IntendedOutputDirectoryRoot = request.Plan.OutputDirectoryRoot
        }, null);
    });

    /// <summary>Records destination intent without granting approval or changing an actual Keep selection.</summary>
    public BatchEditResult SetDestination(BatchDestinationRequest request) => EditItem(request.ItemId, item =>
    {
        if (request.OutputDirectoryRoot is not null && item.FolderOrigin is null)
        {
            return new(null, new(ErrorCode.InvalidDestination));
        }

        var unchanged = item.IntendedDestinationPath == request.DestinationPath &&
            item.IntendedOutputDirectoryRoot == request.OutputDirectoryRoot;

        if (unchanged)
        {
            return new(item, null);
        }

        return new(item with
        {
            IntendedDestinationPath = request.DestinationPath,
            IntendedOutputDirectoryRoot = request.OutputDirectoryRoot,
            ApprovedPlan = null,
            State = item.Inspection is null ? item.State : BatchItemState.NeedsReview,
            Failure = item.Inspection is null ? item.Failure : null
        }, null);
    });

    /// <summary>Stores a file-local Keep selection while preserving exact state for a selection no-op.</summary>
    public BatchEditResult SetSelection(BatchSelectionRequest request) => EditItem(request.ItemId, item =>
    {
        if (item.Inspection is null)
        {
            return new(null, new(ErrorCode.StalePlan));
        }

        var identifiers = NormalizeSelection(request.RetainedTrackIds, item.Inspection);

        if (identifiers.IsDefault)
        {
            return new(null, new(ErrorCode.InvalidSelection));
        }

        if (item.CurrentRetainedTrackIds.SequenceEqual(identifiers))
        {
            return new(item, null);
        }

        return new(item with
        {
            RetainedTrackIds = identifiers,
            ApprovedPlan = null,
            State = BatchItemState.NeedsReview,
            Failure = null
        }, null);
    });

    /// <summary>Invalidates only the affected selection or destination approval.</summary>
    public BatchEditResult InvalidatePlan(Guid itemId) => EditItem(itemId, item => new(item with
    {
        ApprovedPlan = null,
        State = item.Inspection is null ? BatchItemState.PendingInspection : BatchItemState.NeedsReview,
        Failure = null
    }, null));

    /// <summary>Captures unfinished listing intentions only; publication facts exclude a row even after cleanup failure.</summary>
    public QueueList CaptureQueueList()
    {
        lock (synchronization)
        {
            var entries = snapshot.Items.Where(item =>
                item.State is not (BatchItemState.Completed or BatchItemState.NoChange) && item.Result?.PublishedPath is null)
                .Select(item => new QueueListEntry(item.SourcePath, item.IntendedDestinationPath, item.Included,
                    item.FolderOrigin, item.IntendedOutputDirectoryRoot)).ToImmutableArray();
            return new(entries);
        }
    }

    /// <summary>Restores a complete listing into an empty safe session without trusting old identities or authority.</summary>
    internal BatchEditResult RestoreQueueList(QueueList list, long? expectedRevision)
    {
        BatchSnapshot updated;

        lock (synchronization)
        {
            var failure = EditFailure();

            if (failure is not null)
            {
                return new(false, DescribeResetFailure(failure));
            }

            var incompatibleSession = !snapshot.Items.IsEmpty ||
                expectedRevision is { } revision && revision != snapshot.Revision;

            if (incompatibleSession)
            {
                return new(false, new(ErrorCode.StalePlan));
            }

            var validation = QueueListPolicy.Validate(list);

            if (validation is not null)
            {
                return new(false, new(ErrorCode.InvalidSelection, validation.Detail));
            }

            var items = list.Entries.Select(entry => new BatchItem(Guid.NewGuid(), entry.SourcePath, null,
                Included: entry.Included, FolderOrigin: entry.FolderOrigin,
                IntendedDestinationPath: entry.DestinationPath,
                IntendedOutputDirectoryRoot: entry.OutputDirectoryRoot)).ToImmutableArray();
            updated = snapshot = new(snapshot.Revision + 1, BatchPhase.Preparing, items);
        }

        Changed?.Invoke(updated);
        return new(true);
    }

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

    /// <summary>Checks edit availability and captures a pure rule preview under the current session lock.</summary>
    internal BatchLanguagePreviewResult PreviewLanguageRules(LanguageRules rules)
    {
        lock (synchronization)
        {
            var failure = EditFailure();

            if (failure is not null)
            {
                return new(null, DescribeResetFailure(failure));
            }

            if (rules is null)
            {
                return new(null, new(ErrorCode.InvalidSelection));
            }

            return new(BatchLanguagePreviewBuilder.Build(new(snapshot, rules)));
        }
    }

    /// <summary>Atomically applies reliable changed rows after checking the issued preview and every captured input.</summary>
    internal BatchLanguageApplyResult ApplyLanguageRules(BatchLanguageApplyRequest request)
    {
        BatchSnapshot updated;
        ImmutableArray<Guid> changedItems;

        lock (synchronization)
        {
            var failure = EditFailure();

            if (failure is not null)
            {
                return new(false, [], DescribeResetFailure(failure));
            }

            if (!request.IsIssued)
            {
                return new(false, [], new(ErrorCode.StalePlan));
            }

            var preview = request.Preview;
            var stale = preview.Revision != snapshot.Revision || preview.Items.Length != snapshot.Items.Length ||
                !preview.Items.Zip(snapshot.Items).All(pair => MatchesPreviewItem(pair.First, pair.Second));

            if (stale)
            {
                return new(false, [], new(ErrorCode.StalePlan));
            }

            changedItems = preview.Items.Where(item => item.State == BatchLanguagePreviewState.Changed)
                .Select(item => item.ItemId).ToImmutableArray();

            if (changedItems.IsEmpty)
            {
                return new(true, []);
            }

            var proposals = preview.Items.Where(item => item.State == BatchLanguagePreviewState.Changed)
                .ToDictionary(item => item.ItemId);
            var items = snapshot.Items.Select(item => proposals.TryGetValue(item.Id, out var proposal)
                ? item with
                {
                    RetainedTrackIds = proposal.ProposedRetainedTrackIds,
                    ApprovedPlan = null,
                    State = BatchItemState.NeedsReview,
                    Failure = null
                } : item).ToImmutableArray();
            updated = Edited(items);
        }

        Changed?.Invoke(updated);
        return new(true, changedItems);
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

            if (ReferenceEquals(result.Item, item))
            {
                return new(true);
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

    /// <summary>Checks preview bindings without treating equal metadata from another inspection as the same snapshot.</summary>
    private static bool MatchesPreviewItem(BatchLanguagePreviewItem preview, BatchItem item)
    {
        var matches = preview.ItemId == item.Id && preview.SourcePath == item.SourcePath &&
            ReferenceEquals(preview.Inspection, item.Inspection) &&
            preview.BeforeRetainedTrackIds.SequenceEqual(item.CurrentRetainedTrackIds);
        return matches;
    }

    /// <summary>Validates actual file-local IDs and every required video while preserving inspected track order.</summary>
    private static ImmutableArray<int> NormalizeSelection(ImmutableArray<int> identifiers, MediaInspection inspection)
    {
        if (identifiers.IsDefault)
        {
            return default;
        }

        var selected = identifiers.ToHashSet();
        var available = inspection.Tracks.Select(track => track.Id).ToHashSet();
        var invalid = selected.Count != identifiers.Length || selected.Any(identifier => !available.Contains(identifier)) ||
            inspection.Tracks.Any(track => track.Kind == TrackKind.Video && !selected.Contains(track.Id));

        if (invalid)
        {
            return default;
        }

        return inspection.Tracks.Where(track => selected.Contains(track.Id)).Select(track => track.Id).ToImmutableArray();
    }

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
