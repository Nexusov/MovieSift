using TrackTrim.Core.Failures;

namespace TrackTrim.Application.Batches;

/// <summary>Coordinates the single explicit Review action through existing source and destination validation.</summary>
public sealed partial class BatchCoordinator
{
    /// <summary>Builds separate immutable plans, validates the complete set and never dispatches a job.</summary>
    public async Task<BatchReviewResult> ReviewSelectionsAsync(BatchSelectionReviewRequest request)
    {
        if (!await gate.WaitAsync(0))
        {
            return new(null, [], new(ErrorCode.Busy));
        }

        BatchSelectionReviewPreparation? preparation = null;

        try
        {
            preparation = session.BeginSelectionReview(request, out var failure);

            if (preparation is null)
            {
                return new(null, [], failure);
            }

            if (!preparation.Failures.IsEmpty)
            {
                return session.CompleteSelectionReview(preparation, new(null, preparation.Failures));
            }

            var validation = await BatchReviewValidator.ValidateAsync(new(preparation.Snapshot, sourceAccess, request.CancellationToken));
            return session.CompleteSelectionReview(preparation, validation);
        }
        catch (OperationCanceledException)
        {
            var result = new BatchReviewResult(null, [], new(ErrorCode.Cancelled));
            return preparation is null ? result : session.CompleteSelectionReview(preparation, result);
        }
        catch (Exception exception)
        {
            var result = new BatchReviewResult(null, [], new(ErrorCode.IoFailure, exception.ToString()));
            return preparation is null ? result : session.CompleteSelectionReview(preparation, result);
        }
        finally
        {
            gate.Release();
        }
    }
}
