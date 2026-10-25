using TrackTrim.Application.Ports;
using TrackTrim.Desktop.Localization;

namespace TrackTrim.Desktop.Appearance;

/// <summary>Connects cosmetic persistence and resource updates without any media-workspace dependency.</summary>
public sealed record AppearanceViewModelOptions(IAppearanceStore Store, LocalizationService Localization, AppearanceApplier Applier);
