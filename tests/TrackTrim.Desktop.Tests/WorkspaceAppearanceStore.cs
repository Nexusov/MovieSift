using TrackTrim.Application.Appearance;
using TrackTrim.Application.Ports;

namespace TrackTrim.Desktop.Tests;

/// <summary>Provides only cosmetic preferences for controlled headless workspace compositions.</summary>
internal sealed class WorkspaceAppearanceStore : IAppearanceStore
{
    public AppearanceSettings Settings { get; private set; } = AppearanceSettings.Default with { Motion = AppearanceMotion.Reduced };

    /// <summary>Returns explicit test preferences without reading a user's files.</summary>
    public Task<AppearanceLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new AppearanceLoadResult(Settings));

    /// <summary>Captures cosmetic writes independently from media and language ports.</summary>
    public Task<AppearanceSaveResult> SaveAsync(AppearanceSaveRequest request)
    {
        Settings = request.Settings;
        return Task.FromResult(new AppearanceSaveResult(true));
    }
}
