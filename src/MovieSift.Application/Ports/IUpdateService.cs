using MovieSift.Application.Updates;

namespace MovieSift.Application.Ports;

/// <summary>Checks and stages updates separately from the desktop's explicit safe-close decision.</summary>
public interface IUpdateService
{
    Task<UpdateRelease?> CheckAsync(CancellationToken cancellationToken = default);
    Task<PreparedUpdate> StageAsync(UpdateRelease release, IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task LaunchInstallerAsync(PreparedUpdate update, CancellationToken cancellationToken = default);
    Task DiscardAsync(PreparedUpdate update, CancellationToken cancellationToken = default);
}

/// <summary>Persists only the optional startup-check preference independently of media and appearance records.</summary>
public interface IUpdatePreferencesStore
{
    Task<bool> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(bool checkOnStartup, CancellationToken cancellationToken = default);
}
