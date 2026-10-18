using TrackTrim.Application.Ports;
using TrackTrim.Core.Failures;

namespace TrackTrim.Application.Inspection;

/// <summary>Prevents a superseded inspection from replacing the selected source.</summary>
public sealed class InspectionService(IMediaBackend backend, ISourceAccess sourceAccess)
{
    private long revision;

    /// <summary>Inspects an unchanged source and discards late responses.</summary>
    public async Task<InspectionResult> InspectAsync(InspectRequest request)
    {
        var currentRevision = Interlocked.Increment(ref revision);

        try
        {
            var before = await sourceAccess.ReadRevisionAsync(new(request.SourcePath, request.CancellationToken));
            var inspection = await backend.InspectAsync(request);
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
}
