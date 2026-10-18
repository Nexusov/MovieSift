using System.Collections.Immutable;
using TrackTrim.Application.Ports;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Planning;

namespace TrackTrim.Application.Jobs;

/// <summary>Owns one candidate from validation through verification, review and publication.</summary>
public sealed class JobCoordinator(IMediaBackend backend, ISourceAccess sourceAccess, IOutputStore outputStore)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private PendingWarning? pendingWarning;

    /// <summary>Runs one approved immutable plan and publishes only verified clean output.</summary>
    public async Task<JobResult> RunAsync(JobRequest request)
    {
        if (!await gate.WaitAsync(0, request.CancellationToken))
        {
            return new(request.Plan.Id, JobPhase.Failed, Failure: new(ErrorCode.Busy));
        }

        var result = new JobResult(request.Plan.Id, JobPhase.Validating, CleanupIssues: []);
        OwnedOutput? output = null;

        try
        {
            if (pendingWarning is not null)
            {
                return result with { Phase = JobPhase.Failed, Failure = new(ErrorCode.Busy) };
            }

            await ValidatePlanAsync(new(request.Plan, request.CancellationToken));
            request.Progress?.Report(new(JobPhase.Validating));

            if (request.Plan.IsNoChange)
            {
                return result with { Phase = JobPhase.NoChange };
            }

            await sourceAccess.ValidateDestinationAsync(new(request.Plan.Inspection.Source, request.Plan.DestinationPath, request.CancellationToken));
            output = await outputStore.ReserveAsync(new(request.Plan, request.CancellationToken));
            request.Progress?.Report(new(JobPhase.Remuxing));
            var progress = new InlineProgress(request.Progress);
            var tool = await backend.RemuxAsync(new(request.Plan, output.CandidatePath, progress, request.CancellationToken));
            result = result with { Phase = JobPhase.Remuxing, Tool = tool };

            if (tool.ExitCode is not (0 or 1))
            {
                throw new OperationFailureException(new(ErrorCode.ToolFailed, tool.Diagnostics));
            }

            request.CancellationToken.ThrowIfCancellationRequested();
            request.Progress?.Report(new(JobPhase.Verifying));
            var candidateRevision = await sourceAccess.ReadRevisionAsync(new(output.CandidatePath, request.CancellationToken));
            var verification = await backend.VerifyAsync(new(request.Plan, output.CandidatePath, request.CancellationToken));
            result = result with { Phase = JobPhase.Verifying, Verification = verification };

            if (!verification.Passed)
            {
                throw new OperationFailureException(new(ErrorCode.VerificationFailed, verification.Details));
            }

            await ValidatePlanAsync(new(request.Plan, request.CancellationToken));
            await ValidateCandidateAsync(new(output.CandidatePath, candidateRevision, request.CancellationToken));
            await outputStore.ValidateOwnershipAsync(new(output, request.CancellationToken));
            request.CancellationToken.ThrowIfCancellationRequested();

            if (tool.ExitCode == 1)
            {
                result = result with { Phase = JobPhase.AwaitingWarningReview };
                pendingWarning = new(request.Plan, output, result);
                request.Progress?.Report(new(JobPhase.AwaitingWarningReview));
                return result;
            }

            request.Progress?.Report(new(JobPhase.Publishing));
            result = await PublishAsync(new(request.Plan, output, result, candidateRevision, request.CancellationToken));
            request.Progress?.Report(new(JobPhase.Completed));
        }
        catch (OperationCanceledException)
        {
            result = result with { Phase = JobPhase.Cancelled, Failure = new(ErrorCode.Cancelled), CancellationRequested = true };
        }
        catch (OperationFailureException exception)
        {
            result = result with { Phase = JobPhase.Failed, Failure = exception.Failure };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            result = result with { Phase = JobPhase.Failed, Failure = new(ErrorCode.IoFailure, exception.Message) };
        }
        finally
        {
            gate.Release();
        }

        if (output is not null && result.Failure?.Code != ErrorCode.ProcessTerminationFailed)
        {
            result = result with { CleanupIssues = await CleanupSafelyAsync(output) };
        }

        return result;
    }

    /// <summary>Revalidates and publishes the existing warning candidate without remuxing.</summary>
    public async Task<JobResult> PublishWarningsAsync(WarningRequest request)
    {
        await gate.WaitAsync(request.CancellationToken);
        var pending = pendingWarning;

        if (pending is null || pending.Plan.Id != request.JobId)
        {
            gate.Release();
            return new(request.JobId, JobPhase.Failed, Failure: new(ErrorCode.StalePlan));
        }

        var result = pending.Result;

        try
        {
            await ValidatePlanAsync(new(pending.Plan, request.CancellationToken));
            await outputStore.ValidateOwnershipAsync(new(pending.Output, request.CancellationToken));
            var candidateRevision = await sourceAccess.ReadRevisionAsync(new(pending.Output.CandidatePath, request.CancellationToken));
            var verification = await backend.VerifyAsync(new(pending.Plan, pending.Output.CandidatePath, request.CancellationToken));
            result = result with { Verification = verification };

            if (!verification.Passed)
            {
                throw new OperationFailureException(new(ErrorCode.VerificationFailed, verification.Details));
            }

            result = await PublishAsync(new(pending.Plan, pending.Output, result, candidateRevision, request.CancellationToken));
            pendingWarning = null;
        }
        catch (OperationCanceledException)
        {
            result = result with { Phase = JobPhase.Cancelled, Failure = new(ErrorCode.Cancelled), CancellationRequested = true };
            pendingWarning = null;
        }
        catch (OperationFailureException exception)
        {
            result = result with { Phase = JobPhase.Failed, Failure = exception.Failure };
            pendingWarning = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            result = result with { Phase = JobPhase.Failed, Failure = new(ErrorCode.IoFailure, exception.Message) };
            pendingWarning = null;
        }
        finally
        {
            gate.Release();
        }

        if (result.Failure?.Code != ErrorCode.ProcessTerminationFailed)
        {
            result = result with { CleanupIssues = await CleanupSafelyAsync(pending.Output) };
        }

        return result;
    }

    /// <summary>Discards only this coordinator's owned warning candidate.</summary>
    public async Task<JobResult> DiscardWarningsAsync(WarningRequest request)
    {
        await gate.WaitAsync(request.CancellationToken);

        try
        {
            var pending = pendingWarning;

            if (pending is null || pending.Plan.Id != request.JobId)
            {
                return new(request.JobId, JobPhase.Failed, Failure: new(ErrorCode.StalePlan));
            }

            pendingWarning = null;
            var issues = await CleanupSafelyAsync(pending.Output);
            return pending.Result with { Phase = JobPhase.Cancelled, CancellationRequested = true, CleanupIssues = issues };
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Rejects plans whose inspected source revision no longer exists.</summary>
    private async Task ValidatePlanAsync(PlanValidationRequest request)
    {
        var current = await sourceAccess.ReadRevisionAsync(new(request.Plan.Inspection.Source.FullPath, request.CancellationToken));

        if (current != request.Plan.Inspection.Source)
        {
            throw new OperationFailureException(new(ErrorCode.SourceChanged));
        }
    }

    /// <summary>Checks cancellation before the short non-cancellable publication boundary.</summary>
    private async Task<JobResult> PublishAsync(PublicationRequest request)
    {
        await ValidatePlanAsync(new(request.Plan, request.CancellationToken));
        await sourceAccess.ValidateDestinationAsync(new(request.Plan.Inspection.Source, request.Plan.DestinationPath, request.CancellationToken));
        await outputStore.ValidateOwnershipAsync(new(request.Output, request.CancellationToken));
        await ValidateCandidateAsync(new(request.Output.CandidatePath, request.CandidateRevision, request.CancellationToken));
        request.CancellationToken.ThrowIfCancellationRequested();
        await outputStore.MarkPublishingAsync(new(request.Output));
        var publication = await outputStore.PublishAsync(new(request.Output));
        return request.Result with
        {
            Phase = JobPhase.Completed,
            PublishedPath = publication.Path,
            OutputLength = publication.Length,
            Failure = null
        };
    }

    /// <summary>Rejects a candidate replaced or modified during verification or review approval.</summary>
    private async Task ValidateCandidateAsync(CandidateValidationRequest request)
    {
        var current = await sourceAccess.ReadRevisionAsync(new(request.Path, request.CancellationToken));

        if (current != request.ExpectedRevision)
        {
            throw new OperationFailureException(new(ErrorCode.VerificationFailed, "The candidate changed after verification began."));
        }
    }

    /// <summary>Keeps cleanup failures separate from the primary job outcome.</summary>
    private async Task<ImmutableArray<CleanupIssue>> CleanupSafelyAsync(OwnedOutput output)
    {
        try
        {
            return await outputStore.CleanupAsync(new(output));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationFailureException)
        {
            return [new(output.DirectoryPath, exception.Message)];
        }
    }

    /// <summary>Reports backend progress without capturing an accidental synchronization context.</summary>
    private sealed class InlineProgress(IProgress<JobProgress>? progress) : IProgress<double>
    {
        /// <summary>Forwards measured remux progress to the caller.</summary>
        public void Report(double value) => progress?.Report(new(JobPhase.Remuxing, value));
    }
}
