using System.Collections.Immutable;

namespace MovieSift.Application.Localization;

public sealed record TranslationLanguage(string Code, string NativeName);
public sealed record TranslationPack(string Locale, ImmutableDictionary<string, string> Messages);
public sealed record TranslationDownloadProgress(long BytesReceived, long? TotalBytes);
public enum TranslationErrorCode { InvalidPack, Unavailable, Busy, Incompatible }

/// <summary>Reports translation-only failures without granting or blocking media authority.</summary>
public sealed class TranslationPackException(TranslationErrorCode code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public TranslationErrorCode Code { get; } = code;
}

/// <summary>Defines explicit supported UI locales independently from media-language matching.</summary>
public static class TranslationCatalog
{
    public static ImmutableArray<TranslationLanguage> Languages { get; } =
    [new("en", "English"), new("ru", "Русский"), new("de", "Deutsch"), new("es", "Español"),
        new("fr", "Français"), new("pt-BR", "Português (Brasil)"), new("uk", "Українська"),
        new("pl", "Polski"), new("tr", "Türkçe"), new("ja", "日本語"), new("ko", "한국어"), new("zh-Hans", "简体中文")];

    /// <summary>Preserves script/region identities and refuses unfamiliar filenames or locale codes.</summary>
    public static bool IsSupported(string? locale) => Languages.Any(language => language.Code == locale);
}
