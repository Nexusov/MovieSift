using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MovieSift.Core.Media;
using MovieSift.Desktop.Localization;

namespace MovieSift.Desktop.ViewModels;

/// <summary>Exposes one file-local track with explicit Keep semantics.</summary>
public sealed class TrackViewModel : ObservableObject
{
    private readonly TrackViewModelOptions options;
    private bool keep = true;

    /// <summary>Retains the immutable inspected metadata and selection callback.</summary>
    public TrackViewModel(TrackViewModelOptions options)
    {
        this.options = options;
        ToggleSelectionCommand = new RelayCommand(ToggleSelection, () => CanEditSelection);
    }

    public MediaTrack Track => options.Track;
    public bool IsSelectable => Track.Kind != TrackKind.Video;
    public bool IsAudio => Track.Kind == TrackKind.Audio;
    public bool IsRequired => !IsSelectable;
    public bool IsExcluded => !Keep;
    public bool CanEditSelection => IsSelectable && (options.CanEdit?.Invoke() ?? true);
    public string SelectionActionText => Localization[IsExcluded ? "TrackRestore" : "TrackExclude"];
    public string SelectionStateText => Localization[IsRequired ? "AlwaysKept" : IsExcluded ? "TrackExcluded" : "AggregateAllKept"];
    public string SelectionActionAccessibleName => $"{SelectionActionText}: {KindText} {IdentifierText}, {TitleText}";
    public IRelayCommand ToggleSelectionCommand { get; }
    public LocalizationService Localization => options.Localization;
    public string KindText => Localization[Track.Kind.ToString()];
    public string IdentifierText => Track.Id.ToString(CultureInfo.InvariantCulture);
    public string TitleText => string.IsNullOrWhiteSpace(Track.Title) ? Localization["Untitled"] : Track.Title;
    public string LanguageText => Track.LanguageIetf ?? Track.Language;
    public string ChannelsText => Track.Channels?.ToString(CultureInfo.InvariantCulture) ?? Localization["Unknown"];
    public string KeepAccessibleName => $"{Localization["Keep"]}: {KindText} {IdentifierText}, {TitleText}";
    public string FlagsText => TrackPresentation.DescribeFlags(Track, Localization);

    public bool Keep
    {
        get => keep;
        set
        {
            var removesRequiredVideo = !IsSelectable && !value;

            if (removesRequiredVideo)
            {
                return;
            }

            if (SetProperty(ref keep, value))
            {
                options.SelectionChanged();
                RefreshLocalization();
            }
        }
    }

    /// <summary>Checks the current editor gate even when a caller directly invokes a disabled command.</summary>
    private void ToggleSelection()
    {
        if (!CanEditSelection)
        {
            return;
        }

        Keep = !Keep;
    }

    /// <summary>Refreshes current localized metadata labels without retaining old inspected rows.</summary>
    public void RefreshLocalization()
    {
        OnPropertyChanged(string.Empty);
        ToggleSelectionCommand.NotifyCanExecuteChanged();
    }
}
