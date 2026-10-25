using System.Text.Json;
using MovieSift.Application;
using MovieSift.Application.Ports;
using MovieSift.Core.Failures;
using MovieSift.Core.Media;
using MovieSift.Infrastructure.Files;

namespace MovieSift.Infrastructure.Persistence;

/// <summary>Reads small versioned local records and replaces them through a sibling temporary file.</summary>
internal static class BoundedJson
{
    private const int MaximumRecordBytes = 65_536;
    private static readonly JsonSerializerOptions Options = new() { MaxDepth = 16, WriteIndented = true };

    /// <summary>Requires a regular file and rejects oversized or redirected records before deserialization.</summary>
    internal static async Task<T?> ReadAsync<T>(string path)
    {
        await using var stream = Files.FileSourceAccess.OpenRegularReadStream(path);

        if (stream.Length > MaximumRecordBytes)
        {
            throw new OperationFailureException(new(ErrorCode.OwnershipInvalid, "The local record exceeds its size limit."));
        }

        var bytes = new byte[MaximumRecordBytes + 1];
        var count = 0;

        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count));

            if (read == 0)
            {
                break;
            }

            count += read;
        }

        if (count > MaximumRecordBytes)
        {
            throw new OperationFailureException(new(ErrorCode.OwnershipInvalid, "The local record changed beyond its size limit."));
        }

        return JsonSerializer.Deserialize<T>(bytes.AsSpan(0, count), Options);
    }

    /// <summary>Flushes a unique sibling record before a replace operation.</summary>
    internal static async Task<SourceRevision> WriteAsync<T>(JsonWriteRequest<T> request)
    {
        var parent = Path.GetDirectoryName(request.Path)!;
        Files.PathSafety.RejectReparsePoints(parent);
        var temporaryPath = Path.Combine(parent, $".{Guid.NewGuid():N}.tmp");
        var fileActions = request.FileActions ?? new JsonFileActions(
            (source, destination, overwrite) => File.Move(source, destination, overwrite), File.Delete);
        var captureRevision = fileActions.CaptureTemporaryRevision ?? FileSourceAccess.ReadRegularRevision;
        SourceRevision? temporaryRevision = null;
        var temporaryCreated = false;
        var temporaryComplete = false;
        Exception? primaryFailure = null;

        try
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                temporaryCreated = true;
                temporaryRevision = captureRevision(new(temporaryPath, stream.SafeFileHandle));
                await JsonSerializer.SerializeAsync(stream, request.Value, Options, request.CancellationToken);
                await stream.FlushAsync(request.CancellationToken);
                stream.Flush(flushToDisk: true);
            }

            var closedRevision = await new FileSourceAccess().ReadRevisionAsync(new(temporaryPath));

            if (closedRevision.FileIdentity != temporaryRevision.FileIdentity)
            {
                throw new IOException("The temporary record changed identity before publication.");
            }

            temporaryRevision = closedRevision;
            temporaryComplete = true;
            request.CancellationToken.ThrowIfCancellationRequested();
            var committedRevision = temporaryRevision with { FullPath = request.Path };
            fileActions.MoveTemporary(temporaryPath, request.Path, request.Overwrite);
            temporaryCreated = false;
            temporaryRevision = null;
            return committedRevision;
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            if (temporaryCreated)
            {
                try
                {
                    if (temporaryRevision is null)
                    {
                        throw new IOException("The created temporary record's identity could not be captured; the path was preserved.");
                    }

                    if (File.Exists(temporaryPath) || Directory.Exists(temporaryPath))
                    {
                        var current = await new FileSourceAccess().ReadRevisionAsync(new(temporaryPath));

                        var changed = current.FileIdentity != temporaryRevision.FileIdentity ||
                            temporaryComplete && current != temporaryRevision;

                        if (changed)
                        {
                            throw new IOException("The temporary record no longer identifies this operation's file; it was preserved.");
                        }

                        fileActions.DeleteTemporary(temporaryPath);
                    }
                }
                catch (Exception exception)
                {
                    var failure = primaryFailure switch
                    {
                        OperationFailureException operation => operation.Failure,
                        OperationCanceledException => new(ErrorCode.Cancelled),
                        _ => new Failure(ErrorCode.PersistenceUnavailable, (primaryFailure ?? exception).Message)
                    };
                    var issues = primaryFailure is OperationFailureException operationFailure ? operationFailure.CleanupIssues : [];
                    throw new OperationFailureException(failure, issues.Add(new CleanupIssue(temporaryPath, exception.Message)),
                        primaryFailure ?? exception);
                }
            }
        }
    }
}
