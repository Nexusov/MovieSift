using CommunityToolkit.Mvvm.ComponentModel;
using TrackTrim.Desktop.Localization;

namespace TrackTrim.Desktop.Appearance;

/// <summary>Keeps an appearance option identity stable while its application-owned label is translated.</summary>
public sealed class AppearanceOptionViewModel(LocalizationService localization, string key) : ObservableObject
{
    public string Text => localization[key];

    /// <summary>Changes only presentation without replacing the ComboBox item or selected index.</summary>
    public void Refresh() => OnPropertyChanged(nameof(Text));
}
