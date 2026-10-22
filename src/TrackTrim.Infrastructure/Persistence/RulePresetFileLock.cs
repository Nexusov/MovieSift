using TrackTrim.Application.Presets;

namespace TrackTrim.Infrastructure.Persistence;

/// <summary>Maps the portable record lock into the existing preset-specific outcomes.</summary>
internal sealed class RulePresetFileLock(PortableRecordFileLock ownedLock) : IDisposable
{
    /// <summary>Preserves fail-fast preset lock behavior while sharing its safe OS implementation.</summary>
    internal static RulePresetFileLock Acquire(string path)
    {
        try
        {
            return new(PortableRecordFileLock.Acquire(path));
        }
        catch (PortableRecordLockException exception)
        {
            var failure = exception.IsBusy
                ? new RulePresetFailure(RulePresetErrorCode.Busy, "Another process is updating the rule presets; retry the operation.")
                : new RulePresetFailure(RulePresetErrorCode.PersistenceUnavailable, exception.Message);
            throw new RulePresetStorageException(failure);
        }
    }

    /// <summary>Releases only this operation's owned descriptor.</summary>
    public void Dispose() => ownedLock.Dispose();
}
