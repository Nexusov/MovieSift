using MovieSift.Application.Appearance;

namespace MovieSift.Application.Ports;

/// <summary>Persists cosmetic preferences separately from language and all media authority.</summary>
public interface IAppearanceStore
{
    Task<AppearanceLoadResult> LoadAsync(CancellationToken cancellationToken = default);
    Task<AppearanceSaveResult> SaveAsync(AppearanceSaveRequest request);
}
