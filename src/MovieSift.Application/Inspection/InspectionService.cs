using MovieSift.Application.Ports;
using MovieSift.Core.Failures;

namespace MovieSift.Application.Inspection;

/// <summary>Prevents a superseded inspection from replacing the selected source.</summary>
public sealed class InspectionService(IMediaBackend backend, ISourceAccess sourceAccess)
{
    private long revision;

    /// <summary>Inspects an unchanged source and discards late responses.</summary>
    public async Task<InspectionResult> InspectAsync(InspectRequest request)
    {
        var currentRevision = Interlocked.Increment(ref revision);
        using var progress = new RevisionProgress(this, currentRevision, request);

        try
        {
            progress.Report(InspectionStage.CheckingSource);
            var before = await sourceAccess.ReadRevisionAsync(new(request.SourcePath, request.CancellationToken));
            var inspection = await backend.InspectAsync(request with { Progress = progress });
            progress.Report(InspectionStage.RecheckingSource);
            var after = await sourceAccess.ReadRevisionAsync(new(request.SourcePath, request.CancellationToken));

            if (currentRevision != Volatile.Read(ref revision))
            {
                return new(null, null, true);
            }

            if (before != after || inspection.Source != after)
            {
                return new(null, new(ErrorCode.SourceChanged));
            }

            return new(inspection, null);
        }
        catch (OperationFailureException exception)
        {
            return new(null, exception.Failure, currentRevision != Volatile.Read(ref revision));
        }
        catch (OperationCanceledException)
        {
            return new(null, new(ErrorCode.Cancelled), currentRevision != Volatile.Read(ref revision));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(null, new(ErrorCode.IoFailure, exception.Message), currentRevision != Volatile.Read(ref revision));
        }
    }

    /// <summary>Stops superseded or cancelled backend notifications before they reach presentation.</summary>
    private sealed class RevisionProgress(InspectionService owner, long expectedRevision, InspectRequest request) : IProgress<InspectionStage>, IDisposable
    {
        private int completed;

        /// <summary>Forwards only observations from the currently owned inspection.</summary>
        public void Report(InspectionStage value)
        {
            var isCurrent = Volatile.Read(ref completed) == 0 && expectedRevision == Volatile.Read(ref owner.revision)
                && !request.CancellationToken.IsCancellationRequested;

            if (isCurrent)
            {
                request.Progress?.Report(value);
            }
        }

        /// <summary>Closes the observation lifetime on success or failure without changing request cancellation.</summary>
        public void Dispose() => Volatile.Write(ref completed, 1);
    }
}
