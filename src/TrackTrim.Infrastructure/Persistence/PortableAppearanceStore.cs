using System.Text.Json;
using TrackTrim.Application;
using TrackTrim.Application.Appearance;
using TrackTrim.Application.Ports;
using TrackTrim.Infrastructure.Files;

namespace TrackTrim.Infrastructure.Persistence;

/// <summary>Atomically stores portable cosmetic preferences without changing media availability or language settings.</summary>
public sealed class PortableAppearanceStore : IAppearanceStore
{
    internal const string FileName = "appearance.json";
    internal const string LockFileName = "appearance.lock";
    private readonly string dataPath;
    private readonly AppearanceFileActions fileActions;

    /// <summary>Uses fixed same-directory replacement and cleanup of operation-owned temporary files only.</summary>
    public PortableAppearanceStore(string dataDirectory) : this(dataDirectory,
        new((temporary, destination) => File.Move(temporary, destination, overwrite: true), File.Delete))
    {
    }

    /// <summary>Provides a narrow failure seam for atomic replacement and secondary-cleanup regression tests.</summary>
    internal PortableAppearanceStore(string dataDirectory, AppearanceFileActions fileActions)
    {
        dataPath = Path.GetFullPath(dataDirectory);
        this.fileActions = fileActions;
    }

    /// <summary>A missing record uses built-ins; malformed and unsafe records remain untouched and explicit.</summary>
    public async Task<AppearanceLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await ReadAsync(cancellationToken);
        }
        catch (Exception exception) when (ExpectedFailure(exception))
        {
            return new(null, DescribeFailure(exception));
        }
    }

    /// <summary>Validates, flushes and replaces a complete record under fail-fast interprocess exclusion.</summary>
    public async Task<AppearanceSaveResult> SaveAsync(AppearanceSaveRequest request)
    {
        if (!AppearanceSettingsPolicy.IsValid(request.Settings))
        {
            return new(false, new(AppearanceErrorCode.InvalidData, "The appearance configuration is invalid."));
        }

        string? ownedTemporary = null;
        AppearanceSaveResult result;

        try
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            ValidateDataAncestors();
            Directory.CreateDirectory(dataPath);
            PathSafety.RejectReparsePoints(dataPath);
            using var ownedLock = PortableRecordFileLock.Acquire(Path.Combine(dataPath, LockFileName));
            var current = await LoadAsync(request.CancellationToken);
            var canReplaceInvalid = request.ReplaceInvalidData && current.Failure?.Code is
                AppearanceErrorCode.InvalidData or AppearanceErrorCode.UnsupportedVersion;

            if (current.Failure is not null && !canReplaceInvalid)
            {
                return new(false, current.Failure);
            }

            var bytes = AppearanceJson.Write(request.Settings);
            var temporaryPath = Path.Combine(dataPath, $".appearance.{Guid.NewGuid():N}.tmp");

            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownedTemporary = temporaryPath;
                await stream.WriteAsync(bytes, request.CancellationToken);
                await stream.FlushAsync(request.CancellationToken);
                stream.Flush(flushToDisk: true);
            }

            request.CancellationToken.ThrowIfCancellationRequested();
            PathSafety.RejectReparsePoints(dataPath);
            ValidateDestinationIfPresent();
            fileActions.Publish(temporaryPath, Path.Combine(dataPath, FileName));
            ownedTemporary = null;
            result = new(true);
        }
        catch (Exception exception) when (ExpectedFailure(exception))
        {
            result = new(false, DescribeFailure(exception));
        }

        if (ownedTemporary is null)
        {
            return result;
        }

        try
        {
            fileActions.DeleteTemporary(ownedTemporary);
        }
        catch (Exception exception) when (ExpectedFailure(exception))
        {
            return result with { CleanupFailure = DescribeFailure(exception) };
        }

        return result;
    }

    /// <summary>Checks regular-file identity before reading length and bounds bytes even if the file grows.</summary>
    private async Task<AppearanceLoadResult> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateDataAncestors();
        var path = Path.Combine(dataPath, FileName);

        if (!Exists(path))
        {
            return new(AppearanceSettings.Default, IsMissing: true);
        }

        await using var stream = FileSourceAccess.OpenRegularReadStream(path);

        if (stream.Length > AppearanceJson.MaximumBytes)
        {
            throw new JsonException("Appearance settings exceed 65536 UTF-8 bytes.");
        }

        var bytes = new byte[AppearanceJson.MaximumBytes + 1];
        var count = 0;

        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken);

            if (read == 0)
            {
                break;
            }

            count += read;
        }

        if (count > AppearanceJson.MaximumBytes)
        {
            throw new JsonException("Appearance settings grew beyond their byte limit.");
        }

        return AppearanceJson.Read(bytes.AsMemory(0, count));
    }

    /// <summary>Refuses redirected/nonregular existing destinations immediately before atomic replacement.</summary>
    private void ValidateDestinationIfPresent()
    {
        var path = Path.Combine(dataPath, FileName);

        if (!Exists(path))
        {
            return;
        }

        using var stream = FileSourceAccess.OpenRegularReadStream(path);
    }

    /// <summary>Validates the nearest existing portable ancestor before creating directories or assuming absence.</summary>
    private void ValidateDataAncestors()
    {
        string? current = dataPath;

        while (current is not null)
        {
            try
            {
                var attributes = File.GetAttributes(current);
                PathSafety.RejectReparsePoints(current);

                if (!attributes.HasFlag(FileAttributes.Directory))
                {
                    throw new IOException("The portable data path must be a directory.");
                }

                return;
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                current = Path.GetDirectoryName(current);
            }
        }

        throw new IOException("No accessible portable data ancestor was found.");
    }

    /// <summary>Distinguishes absence from access failures and unsafe existing records.</summary>
    private static bool Exists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Returns cosmetic outcomes without converting them into media-ownership failures.</summary>
    private static AppearanceFailure DescribeFailure(Exception exception) => exception switch
    {
        JsonException => new(AppearanceErrorCode.InvalidData, exception.Message),
        OperationCanceledException => new(AppearanceErrorCode.Cancelled),
        PortableRecordLockException { IsBusy: true } => new(AppearanceErrorCode.Busy, exception.Message),
        _ => new(AppearanceErrorCode.PersistenceUnavailable, exception.Message)
    };

    /// <summary>Handles expected bounded-storage errors while allowing programming failures to remain visible.</summary>
    private static bool ExpectedFailure(Exception exception) => exception is JsonException or IOException or
        UnauthorizedAccessException or OperationFailureException or PortableRecordLockException or OperationCanceledException;
}
