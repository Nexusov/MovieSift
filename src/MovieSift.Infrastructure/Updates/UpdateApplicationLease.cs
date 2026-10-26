using System.Security.Cryptography;
using System.Text;
using MovieSift.Application.Updates;
using MovieSift.Infrastructure.Persistence;

namespace MovieSift.Infrastructure.Updates;

/// <summary>Coordinates cooperating app instances and the installer using an OS lock outside the replaced tree.</summary>
public sealed class UpdateApplicationLease : IDisposable
{
    private readonly IDisposable? ownedLock;

    private UpdateApplicationLease(IDisposable? ownedLock) => this.ownedLock = ownedLock;

    /// <summary>False leaves ordinary portable use available but disables automatic installation.</summary>
    public bool CanInstallUpdates => ownedLock is not null;

    /// <summary>Keeps a shared lifetime lease; an active installer blocks startup instead of racing it.</summary>
    public static UpdateApplicationLease Acquire(string applicationDirectory)
    {
        try
        {
            return new(PortableRecordFileLock.Acquire(LockPath(applicationDirectory), shared: true));
        }
        catch (PortableRecordLockException exception) when (exception.IsBusy)
        {
            throw new UpdateException(UpdateErrorCode.Busy, "This installation is currently being updated. Retry after the updater finishes.", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PortableRecordLockException or Application.OperationFailureException)
        {
            // A portable app in a read-only parent must remain usable; replacement needs a writable parent.
            return new(null);
        }
    }

    /// <summary>Requires all registered instances to have exited before any installation mutation.</summary>
    internal static IDisposable AcquireInstaller(string applicationDirectory) =>
        PortableRecordFileLock.Acquire(LockPath(applicationDirectory));

    /// <summary>Leaves the permanent lock file in place so contenders always lock the same inode.</summary>
    public void Dispose() => ownedLock?.Dispose();

    /// <summary>Names the coordination file from the canonical installation location, independent of version.</summary>
    internal static string LockPath(string applicationDirectory)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory));
        var parent = Path.GetDirectoryName(path) ?? throw new IOException("An application at a filesystem root cannot self-update.");
        var key = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24].ToLowerInvariant();
        return Path.Combine(parent, $".moviesift-update-{digest}.lock");
    }
}
