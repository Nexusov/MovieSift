using MovieSift.Application.Localization;

namespace MovieSift.Desktop.Localization;

/// <summary>Connects translation-only storage to the shell's existing preference writer.</summary>
public sealed record LanguageSettingsOptions(ITranslationPackStore? Store, LocalizationService Localization, Func<string, Task> ApplyLanguage);
