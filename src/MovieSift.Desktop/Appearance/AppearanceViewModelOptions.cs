using MovieSift.Application.Ports;
using MovieSift.Desktop.Localization;

namespace MovieSift.Desktop.Appearance;

/// <summary>Connects cosmetic persistence and resource updates without any media-workspace dependency.</summary>
public sealed record AppearanceViewModelOptions(IAppearanceStore Store, LocalizationService Localization, AppearanceApplier Applier);
