using System.Text.Json;
using System.Text.Json.Serialization;
using MovieSift.Application.Ports;
using MovieSift.Application.Updates;
using MovieSift.Infrastructure.Persistence;

namespace MovieSift.Infrastructure.Updates;

/// <summary>Stores the optional update preference independently of the required portable media state.</summary>
public sealed class PortableUpdatePreferencesStore(string dataDirectory) : IUpdatePreferencesStore
{
    private readonly string directory = Path.GetFullPath(dataDirectory);

    /// <summary>Defaults to enabled only when the record is absent; malformed records stay untouched.</summary>
    public async Task<bool> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.Combine(directory, "updates.json");
        try
        {
            var settings = await BoundedJson.ReadAsync<UpdatePreferences>(path);
            if (settings is not { Version: 1 })
            {
                throw new JsonException("Unsupported update preference record.");
            }
            return settings.CheckOnStartup;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or Application.OperationFailureException)
        {
            throw new UpdateException(UpdateErrorCode.Storage, "The update preference could not be read.", exception);
        }
    }

    /// <summary>Replaces one complete preference after validating the current record under interprocess exclusion.</summary>
    public async Task SaveAsync(bool checkOnStartup, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            using var ownedLock = PortableRecordFileLock.Acquire(Path.Combine(directory, ".updates.lock"));
            _ = await LoadAsync(cancellationToken);
            await BoundedJson.WriteAsync(new JsonWriteRequest<UpdatePreferences>(
                Path.Combine(directory, "updates.json"), new(1, checkOnStartup), cancellationToken));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PortableRecordLockException or Application.OperationFailureException)
        {
            throw new UpdateException(UpdateErrorCode.Storage, "The update preference could not be saved.", exception);
        }
    }

    private sealed record UpdatePreferences([property: JsonRequired] int Version, [property: JsonRequired] bool CheckOnStartup);
}
