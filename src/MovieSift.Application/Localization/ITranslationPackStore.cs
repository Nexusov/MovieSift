namespace MovieSift.Application.Localization;

/// <summary>Loads validated data-only translations without ever inspecting or modifying media.</summary>
public interface ITranslationPackStore
{
    /// <summary>Refreshes previously installed locales after an application change without applying a language.</summary>
    Task<IReadOnlyList<TranslationPack>> SynchronizeCachedAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<TranslationPack>>([]);

    Task<TranslationPack?> LoadCachedAsync(string locale, CancellationToken cancellationToken = default);
    Task<TranslationPack> DownloadAsync(string locale, IProgress<TranslationDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
