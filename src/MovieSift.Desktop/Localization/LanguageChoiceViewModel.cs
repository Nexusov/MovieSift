using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MovieSift.Desktop.Localization;

/// <summary>Presents a fixed locale and its actual local availability without storing another language choice.</summary>
public sealed class LanguageChoiceViewModel : ObservableObject
{
    private readonly LanguageSettingsViewModel owner;

    /// <summary>Connects one catalog entry to the shared asynchronous selection command.</summary>
    public LanguageChoiceViewModel(LanguageSettingsViewModel owner, string code, string nativeName)
    {
        this.owner = owner;
        Code = code;
        NativeName = nativeName;
        SelectCommand = new AsyncRelayCommand(() => owner.SelectAsync(code), () => owner.CanChoose);
    }

    public string Code { get; }
    public string NativeName { get; }
    public bool IsCurrent => owner.Localization.Language == Code;
    public string StateText => owner.Localization[IsCurrent ? "LanguageCurrent" : Code == "en" ? "LanguageIncluded" : owner.IsInstalled(Code) ? "LanguageDownloaded" : "LanguageDownload"];
    public IAsyncRelayCommand SelectCommand { get; }

    /// <summary>Refreshes localized state and command availability without rebuilding catalog identities.</summary>
    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
        SelectCommand.NotifyCanExecuteChanged();
    }
}
