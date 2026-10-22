using TrackTrim.Core.Media;
using TrackTrim.Desktop.Localization;

namespace TrackTrim.Desktop.ViewModels;

public sealed record TrackViewModelOptions(MediaTrack Track, LocalizationService Localization, Action SelectionChanged);
