namespace MovieSift.Application.Localization;

/// <summary>Loads validated data-only translations without ever inspecting or modifying media.</summary>
public interface ITranslationPackStore
{
    Task<TranslationPack?> LoadCachedAsync(string locale, CancellationToken cancellationToken = default);
    Task<TranslationPack> DownloadAsync(string locale, IProgress<TranslationDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
