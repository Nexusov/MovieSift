using System.Globalization;
using System.Resources;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MovieSift.Desktop.Localization;

/// <summary>Notifies visible resource bindings when the selected UI language changes.</summary>
public sealed class LocalizationService : ObservableObject
{
    private static readonly ResourceManager Resources = new("MovieSift.Desktop.Localization.Strings", typeof(LocalizationService).Assembly);
    private CultureInfo culture = CultureInfo.GetCultureInfo("en");

    public string Language => culture.TwoLetterISOLanguageName;

    public string this[string key] => Resources.GetString(key, culture) ?? key;

    /// <summary>Selects an explicit supported language and otherwise uses English.</summary>
    public void SetLanguage(string? language)
    {
        var selectedLanguage = language == "ru" ? "ru" : "en";

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
