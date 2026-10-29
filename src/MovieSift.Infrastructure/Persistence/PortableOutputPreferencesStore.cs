using System.Text.Json;
using System.Text.Json.Serialization;
using MovieSift.Application.Ports;
using MovieSift.Core.Planning;
using MovieSift.Infrastructure.Files;

namespace MovieSift.Infrastructure.Persistence;

/// <summary>Persists one bounded output preference under the existing portable record protections.</summary>
public sealed class PortableOutputPreferencesStore(string dataDirectory) : IOutputPreferencesStore
{
    private const string FileName = "output-preferences.json";
    private readonly string directory = Path.GetFullPath(dataDirectory);

    /// <summary>Uses Copy only for a missing record; incompatible or malformed preferences remain visible and untouched.</summary>
    public async Task<OutputPreference> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var record = await BoundedJson.ReadAsync<StoredOutputPreference>(Path.Combine(directory, FileName));

            if (record is not { Version: 1 } || !Enum.IsDefined(record.Mode))
            {
                throw new JsonException("Invalid output preference record.");
            }

            return new(record.Remember, record.Mode);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(false, OutputMode.Copy);
        }
    }

    /// <summary>Serializes writers and atomically replaces only this preference, leaving all media state independent.</summary>
    public async Task SaveAsync(OutputPreference preference, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(preference.Mode))
        {
            throw new ArgumentOutOfRangeException(nameof(preference));
        }

        cancellationToken.ThrowIfCancellationRequested();
        // Live portable composition has already prepared this existing app-local data directory.
        PathSafety.RejectReparsePoints(directory);
        try
        {
            using var recordLock = PortableRecordFileLock.Acquire(Path.Combine(directory, ".output-preferences.lock"));
            _ = await LoadAsync(cancellationToken);
            await BoundedJson.WriteAsync(new JsonWriteRequest<StoredOutputPreference>(Path.Combine(directory, FileName),
                new(1, preference.Remember, preference.Mode), cancellationToken));
        }
        catch (PortableRecordLockException exception)
        {
            throw new IOException("The output preference is busy or unavailable.", exception);
        }
    }

    private sealed record StoredOutputPreference([property: JsonRequired] int Version,
        [property: JsonRequired] bool Remember, [property: JsonRequired] OutputMode Mode);
}
