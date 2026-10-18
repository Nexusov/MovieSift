using System.Text.Json;
using TrackTrim.Application;
using TrackTrim.Core.Failures;

namespace TrackTrim.Infrastructure.Persistence;

/// <summary>Reads small versioned local records and replaces them through a sibling temporary file.</summary>
internal static class BoundedJson
{
    private const int MaximumRecordBytes = 65_536;
    private static readonly JsonSerializerOptions Options = new() { MaxDepth = 16, WriteIndented = true };

    /// <summary>Rejects oversized or redirected records before deserialization.</summary>
    internal static async Task<T?> ReadAsync<T>(string path)
    {
        Files.PathSafety.RejectReparsePoints(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

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
    internal static async Task WriteAsync<T>(JsonWriteRequest<T> request)
    {
        var parent = Path.GetDirectoryName(request.Path)!;
        Files.PathSafety.RejectReparsePoints(parent);
        var temporaryPath = Path.Combine(parent, $".{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, request.Value, Options);
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, request.Path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
