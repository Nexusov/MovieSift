using TrackTrim.Application.Appearance;

namespace TrackTrim.Application.Ports;

/// <summary>Persists cosmetic preferences separately from language and all media authority.</summary>
public interface IAppearanceStore
{
    Task<AppearanceLoadResult> LoadAsync(CancellationToken cancellationToken = default);
    Task<AppearanceSaveResult> SaveAsync(AppearanceSaveRequest request);
}
