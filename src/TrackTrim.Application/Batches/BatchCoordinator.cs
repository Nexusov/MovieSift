using System.Collections.Immutable;
using TrackTrim.Application.Inspection;
using TrackTrim.Application.Jobs;
using TrackTrim.Application.Ports;
using TrackTrim.Application.QueueLists;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Rules;

namespace TrackTrim.Application.Batches;

/// <summary>Dispatches reviewed files sequentially through the existing single-job owner.</summary>
public sealed partial class BatchCoordinator(
    BatchSession session,
    InspectionService inspectionService,
    JobCoordinator jobs,
    ISourceAccess sourceAccess)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object cancellationSynchronization = new();
    private CancellationTokenSource? activeCancellation;
    private BatchLanguagePreview? issuedLanguagePreview;

    /// <summary>Starts an empty in-memory batch only when no operation or unresolved ownership is present.</summary>
    public BatchEditResult StartNewBatch(long? expectedRevision = null)
    {
        if (!gate.Wait(0))
        {
            return new(false, new(ErrorCode.Busy));
        }

        try
        {
            var result = session.Reset(expectedRevision);

            if (result.Applied)
            {
                issuedLanguagePreview = null;
            }

            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Accepts explicit recovery of listing intentions without inspecting, approving or dispatching media.</summary>
    public BatchEditResult RestoreQueueList(QueueList list, long? expectedRevision = null)
    {
        if (!gate.Wait(0))
        {
            return new(false, new(ErrorCode.Busy));
        }

        try
        {
            var result = session.RestoreQueueList(list, expectedRevision);

            if (result.Applied)
            {
                issuedLanguagePreview = null;
            }

            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Previews independent language selections without changing any queue or approval state.</summary>
    public BatchLanguagePreviewResult PreviewLanguageRules(LanguageRules rules)
    {
        if (!gate.Wait(0))
        {
            return new(null, new(ErrorCode.Busy));
        }

        try
        {
            var result = session.PreviewLanguageRules(rules);

            if (result.Preview is not null)
            {
                issuedLanguagePreview = result.Preview;
            }

            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Applies only this coordinator's unchanged issued preview at one atomic selection boundary.</summary>
    public BatchLanguageApplyResult ApplyLanguageRules(BatchLanguagePreview preview)
    {
        if (!gate.Wait(0))
        {
            return new(false, [], new(ErrorCode.Busy));
        }

        try
        {
            var isIssued = preview is not null && ReferenceEquals(preview, issuedLanguagePreview);
            return session.ApplyLanguageRules(new(preview!, isIssued));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Inspects every included pending source independently, with at most one active probe.</summary>
    public async Task<BatchSnapshot> InspectPendingAsync(CancellationToken cancellationToken = default)
    {
        if (!await gate.WaitAsync(0))
        {
            return BusySnapshot();
        }

        Guid? activeItemId = null;

        try
        {
            if (!session.TryBegin(BatchPhase.Inspecting, out var failure))
            {
                return session.Snapshot with { Failure = failure };
            }

            var pending = session.Snapshot.Items.Where(item => item.Included && item.State == BatchItemState.PendingInspection).ToArray();

            foreach (var item in pending)
            {
                if (cancellationToken.IsCancellationRequested || session.Snapshot.HasSafetyBlock)
                {
                    break;
                }

                activeItemId = item.Id;
                await InspectAsync(new(item.Id, cancellationToken));
                activeItemId = null;
            }

            FinishPreparation(cancellationToken.IsCancellationRequested);
            return session.Snapshot;
        }
        catch (Exception exception)
        {
            return StopUnexpectedFailure(new(activeItemId, exception));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Reinspects one explicitly corrected item without reusing its old approval.</summary>
    public async Task<BatchSnapshot> InspectItemAsync(BatchItemRequest request)
    {
        if (!await gate.WaitAsync(0))
        {
            return BusySnapshot();
        }

        try
        {
            var item = session.Snapshot.Items.FirstOrDefault(item => item.Id == request.ItemId);

            if (item is null || item.State is BatchItemState.Completed or BatchItemState.NoChange)
            {
                return session.Snapshot with { Failure = new(ErrorCode.StalePlan) };
            }

            if (!session.TryBegin(BatchPhase.Inspecting, out var failure))
            {
                return session.Snapshot with { Failure = failure };
            }

            await InspectAsync(request);
            FinishPreparation(request.CancellationToken.IsCancellationRequested);
            return session.Snapshot;
        }
        catch (Exception exception)
        {
            return StopUnexpectedFailure(new(request.ItemId, exception));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Validates all included remaining approvals and freezes the next explicit run.</summary>
    public async Task<BatchReviewResult> ReviewAsync(CancellationToken cancellationToken = default)
    {
        if (!await gate.WaitAsync(0))
        {
            return new(null, [], new(ErrorCode.Busy));
        }

        try
        {
            if (!session.TryBegin(BatchPhase.Reviewing, out var failure))
            {
                return new(null, [], failure);
            }

            var current = session.Snapshot;
            var validation = await BatchReviewValidator.ValidateAsync(new(current, sourceAccess, cancellationToken));

            if (validation.Failure is not null || !validation.ItemFailures.IsEmpty)
            {
                session.Update(snapshot => snapshot with { Phase = BatchPhase.Preparing, Failure = validation.Failure });
                return validation;
            }

            session.Update(snapshot => snapshot with
            {
                Phase = BatchPhase.Ready,
                Review = validation.Review,
                Failure = null,
                PauseReason = BatchPauseReason.None,
                PauseRequested = false
            });
            return validation;
        }
        catch (OperationCanceledException)
        {
            FinishPreparation(true);
            return new(null, [], new(ErrorCode.Cancelled));
        }
        catch (Exception exception)
        {
            var failure = new Failure(ErrorCode.IoFailure, exception.ToString());
            session.Update(snapshot => snapshot with
            {
                Phase = BatchPhase.Preparing,
                Review = null,
                Failure = failure,
                PauseReason = BatchPauseReason.None,
                PauseRequested = false
            });
            return new(null, [], failure);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Runs the latest immutable batch review until completion or the first pause boundary.</summary>
    public async Task<BatchSnapshot> RunAsync(BatchRunRequest request)
    {
        if (!await gate.WaitAsync(0))
        {
            return BusySnapshot();
        }

        CancellationTokenSource? cancellation = null;
        Guid? activeItemId = null;

        try
        {
            var review = session.TakeReview(request.ReviewId);

            if (review is null)
            {
                return session.Snapshot with { Failure = new(ErrorCode.StalePlan) };
            }

            cancellation = CancellationTokenSource.CreateLinkedTokenSource(request.CancellationToken);

            lock (cancellationSynchronization)
            {
                activeCancellation = cancellation;
            }

            foreach (var reviewed in review.Items)
            {
                if (cancellation.IsCancellationRequested || session.Snapshot.PauseRequested)
                {
                    PauseAtBoundary(cancellation.IsCancellationRequested ? BatchPauseReason.Cancelled : BatchPauseReason.Requested);
                    return session.Snapshot;
                }

                activeItemId = reviewed.ItemId;
                SetItem(reviewed.ItemId, item => item with { State = BatchItemState.Running, Result = null, Failure = null });
                var progress = new ItemProgress(reviewed, request.Progress);
                var result = await jobs.RunAsync(new(reviewed.Plan, progress, cancellation.Token));
                ApplyResult(reviewed.ItemId, result);
                var reason = StopReason(result);

                if (reason != BatchPauseReason.None)
                {
                    PauseAtBoundary(reason);
                    return session.Snapshot;
                }

                activeItemId = null;
            }

            session.Update(snapshot => snapshot with
            {
                Phase = BatchPhase.Completed,
                PauseReason = BatchPauseReason.None,
                PauseRequested = false
            });
            return session.Snapshot;
        }
        catch (Exception exception)
        {
            return StopUnexpectedFailure(new(activeItemId, exception));
        }
        finally
        {
            lock (cancellationSynchronization)
            {
                activeCancellation = null;
                cancellation?.Dispose();
            }

            gate.Release();
        }
    }

    /// <summary>Requests a pause after the active job has safely completed its lifecycle.</summary>
    public void Pause() => session.RequestPause();

    /// <summary>Cancels the owned active job and prevents the next queued job from starting.</summary>
    public void Cancel()
    {
        session.RequestPause();

        lock (cancellationSynchronization)
        {
            activeCancellation?.Cancel();
        }
    }

    /// <summary>Publishes only the selected warning owner's existing candidate and remains paused.</summary>
    public Task<BatchSnapshot> PublishWarningsAsync(BatchWarningRequest request) => ResolveWarningAsync(request, publish: true);

    /// <summary>Awaits owned warning cleanup without using a previously cancelled run token.</summary>
    public Task<BatchSnapshot> DiscardWarningsAsync(BatchWarningRequest request) => ResolveWarningAsync(request, publish: false);

    /// <summary>Retains each file's own inspection and resets only its superseded approval.</summary>
    private async Task InspectAsync(BatchItemRequest request)
    {
        var item = session.Snapshot.Items.Single(item => item.Id == request.ItemId);
        SetItem(item.Id, current => current with
        {
            State = BatchItemState.Inspecting,
            Inspection = null,
            ApprovedPlan = null,
            Result = null,
            Failure = null,
            RetainedTrackIds = default
        });
        var result = await inspectionService.InspectAsync(new(item.SourcePath, request.CancellationToken));
        var failure = result.IsStale ? new Failure(ErrorCode.StalePlan) : result.Failure;
        var state = result.Inspection is not null ? BatchItemState.NeedsReview :
            failure?.Code == ErrorCode.Cancelled ? BatchItemState.Cancelled : BatchItemState.Failed;
        SetItem(item.Id, current => current with
        {
            Inspection = result.Inspection,
            DiscoveredSource = result.Inspection?.Source ?? current.DiscoveredSource,
            State = state,
            Failure = failure,
            RetainedTrackIds = result.Inspection?.Tracks.Select(track => track.Id).ToImmutableArray() ?? []
        });
    }

    /// <summary>Routes review actions by frozen item and job identity rather than the current editor.</summary>
    private async Task<BatchSnapshot> ResolveWarningAsync(BatchWarningRequest request, bool publish)
    {
        if (!await gate.WaitAsync(0))
        {
            return BusySnapshot();
        }

        CancellationTokenSource? cancellation = null;

        try
        {
            var item = session.Snapshot.Items.FirstOrDefault(item => item.Id == request.ItemId);
            var hasWarning = item?.State == BatchItemState.AwaitingWarningReview && item.Result?.Phase == JobPhase.AwaitingWarningReview;

            if (!hasWarning || session.Snapshot.HasSafetyBlock)
            {
                return session.Snapshot with { Failure = new(ErrorCode.StalePlan) };
            }

            cancellation = CancellationTokenSource.CreateLinkedTokenSource(request.CancellationToken);

            lock (cancellationSynchronization)
            {
                activeCancellation = cancellation;
            }

            session.Update(snapshot => snapshot with { Phase = BatchPhase.Running, Review = null, Failure = null });
            var warning = new WarningRequest(item!.Result!.JobId, cancellation.Token);
            var result = publish ? await jobs.PublishWarningsAsync(warning) : await jobs.DiscardWarningsAsync(warning);
            ApplyResult(item.Id, result);
            var reason = StopReason(result);
            PauseAtBoundary(reason == BatchPauseReason.None ? BatchPauseReason.Requested : reason);
            return session.Snapshot;
        }
        catch (OperationCanceledException)
        {
            // A cancelled gate wait leaves the existing coordinator's warning ownership intact.
            session.Update(snapshot => snapshot with
            {
                Phase = BatchPhase.Paused,
                PauseReason = BatchPauseReason.WarningReview,
                Failure = new(ErrorCode.Cancelled)
            });
            return session.Snapshot;
        }
        catch (Exception exception)
        {
            return StopUnexpectedFailure(new(request.ItemId, exception));
        }
        finally
        {
            lock (cancellationSynchronization)
            {
                activeCancellation = null;
                cancellation?.Dispose();
            }

            gate.Release();
        }
    }

    /// <summary>Preserves the complete existing job result independently of dispatch decisions.</summary>
    private void ApplyResult(Guid itemId, JobResult result) => SetItem(itemId, item => item with
    {
        Result = result,
        Failure = result.Failure,
        State = result.Phase switch
        {
            JobPhase.Completed => BatchItemState.Completed,
            JobPhase.NoChange => BatchItemState.NoChange,
            JobPhase.AwaitingWarningReview => BatchItemState.AwaitingWarningReview,
            JobPhase.Cancelled => BatchItemState.Cancelled,
            _ => BatchItemState.Failed
        }
    });

    /// <summary>Stops on unsafe cleanup, warning review, failures, cancellation, or explicit pause.</summary>
    private BatchPauseReason StopReason(JobResult result)
    {
        if (result.Failure?.Code is ErrorCode.ProcessTerminationFailed or ErrorCode.PublicationUncertain)
        {
            return BatchPauseReason.UnsafeOwnership;
        }

        if (!result.CleanupIssues.IsDefaultOrEmpty)
        {
            return BatchPauseReason.CleanupRequired;
        }

        if (result.Phase == JobPhase.AwaitingWarningReview)
        {
            return BatchPauseReason.WarningReview;
        }

        if (result.CancellationRequested || result.Phase == JobPhase.Cancelled)
        {
            return BatchPauseReason.Cancelled;
        }

        if (result.Phase == JobPhase.Failed)
        {
            return BatchPauseReason.ItemFailed;
        }

        return session.Snapshot.PauseRequested ? BatchPauseReason.Requested : BatchPauseReason.None;
    }

    /// <summary>Publishes a pause only after the existing job owner returns.</summary>
    private void PauseAtBoundary(BatchPauseReason reason) => session.Update(snapshot => snapshot with
    {
        Phase = BatchPhase.Paused,
        PauseReason = reason,
        PauseRequested = false,
        Review = null
    });

    /// <summary>Retains an escaped lifecycle diagnostic and prevents any second worker or cleanup attempt.</summary>
    private BatchSnapshot StopUnexpectedFailure(UnexpectedBatchFailureRequest request)
    {
        var failure = new Failure(ErrorCode.IoFailure, request.Exception.ToString());
        session.Update(snapshot => snapshot with
        {
            Revision = snapshot.Revision + 1,
            Phase = BatchPhase.Paused,
            PauseReason = BatchPauseReason.UnsafeOwnership,
            PauseRequested = false,
            Review = null,
            Failure = failure,
            Items = snapshot.Items.Select(item => item.Id == request.ItemId
                ? item with { State = BatchItemState.Failed, Failure = failure } : item).ToImmutableArray()
        });
        return session.Snapshot;
    }

    /// <summary>Ends preparation without silently starting any approved item.</summary>
    private void FinishPreparation(bool cancelled) => session.Update(snapshot => snapshot with
    {
        Phase = cancelled || snapshot.HasSafetyBlock ? BatchPhase.Paused : BatchPhase.Preparing,
        PauseReason = snapshot.HasSafetyBlock ? BatchPauseReason.UnsafeOwnership :
            cancelled ? BatchPauseReason.Cancelled : BatchPauseReason.None,
        Failure = cancelled ? new(ErrorCode.Cancelled) : null
    });

    /// <summary>Replaces one row and invalidates any previous aggregate review.</summary>
    private void SetItem(Guid itemId, Func<BatchItem, BatchItem> change) => session.Update(snapshot => snapshot with
    {
        Revision = snapshot.Revision + 1,
        Review = null,
        Items = snapshot.Items.Select(item => item.Id == itemId ? change(item) : item).ToImmutableArray()
    });

    /// <summary>Returns a rejected concurrent operation without altering active state.</summary>
    private BatchSnapshot BusySnapshot() => session.Snapshot with { Failure = new(ErrorCode.Busy) };

    /// <summary>Preserves item and immutable plan identity on every reported job transition.</summary>
    private sealed class ItemProgress(BatchReviewItem item, IProgress<BatchProgress>? progress) : IProgress<JobProgress>
    {
        /// <summary>Forwards measured progress without capturing a synchronization context.</summary>
        public void Report(JobProgress value) => progress?.Report(new(item.ItemId, item.Plan.Id, value));
    }
}
