using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using System.Resources;
using CommunityToolkit.Mvvm.ComponentModel;
using MovieSift.Application.Localization;

namespace MovieSift.Desktop.Localization;

/// <summary>Notifies visible resource bindings when the selected UI language changes.</summary>
public sealed class LocalizationService : ObservableObject
{
    private static readonly ResourceManager Resources = new("MovieSift.Desktop.Localization.Strings", typeof(LocalizationService).Assembly);
    private CultureInfo culture = CultureInfo.GetCultureInfo("en");
    private readonly Dictionary<string, ImmutableDictionary<string, string>> downloaded = new(StringComparer.Ordinal);

    public string Language => culture.Name;

    public string this[string key] => downloaded.TryGetValue(Language, out var pack) && pack.TryGetValue(key, out var translated)
        ? translated : Resources.GetString(key, culture) ?? Resources.GetString(key, CultureInfo.GetCultureInfo("en")) ?? key;

    /// <summary>Supplies the neutral string contract to the data-only pack validator.</summary>
    public static IReadOnlyDictionary<string, string> EnglishMessages => Resources.GetResourceSet(CultureInfo.GetCultureInfo("en"), true, false)!
        .Cast<DictionaryEntry>().ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!, StringComparer.Ordinal);

    /// <summary>Registers an already validated immutable pack without changing the active language.</summary>
    public void InstallPack(TranslationPack pack) => downloaded[pack.Locale] = pack.Messages;

    /// <summary>Selects an explicit supported language and otherwise uses English.</summary>
    public void SetLanguage(string? language)
    {
        var bundledRussian = language == "ru" && Resources.GetResourceSet(CultureInfo.GetCultureInfo("ru"), true, false) is not null;
        var available = language == "en" || bundledRussian || language is not null && downloaded.ContainsKey(language);
        var selectedLanguage = TranslationCatalog.IsSupported(language) && available ? language! : "en";

        if (Language == selectedLanguage)
        {
            return;
        }

        culture = CultureInfo.GetCultureInfo(selectedLanguage);
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Formats application text without changing protocol or media cultures.</summary>
    public string Format(LocalizedTextRequest request)
    {
        return string.Format(culture, this[request.Key], request.Arguments);
    }
}
