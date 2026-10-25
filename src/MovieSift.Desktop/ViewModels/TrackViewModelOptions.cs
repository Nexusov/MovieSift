using MovieSift.Core.Media;
using MovieSift.Desktop.Localization;

namespace MovieSift.Desktop.ViewModels;

public sealed record TrackViewModelOptions(MediaTrack Track, LocalizationService Localization, Action SelectionChanged);
