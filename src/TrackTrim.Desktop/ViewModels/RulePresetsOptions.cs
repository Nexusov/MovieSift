using TrackTrim.Application.Batches;
using TrackTrim.Application.Presets;
using TrackTrim.Desktop.Localization;

namespace TrackTrim.Desktop.ViewModels;

public sealed record RulePresetsOptions(
    RulePresetService Service,
    BatchLanguageRulesViewModel Editor,
    BatchSession Session,
    LocalizationService Localization,
    Func<bool> CanEdit,
    Func<bool> CanWrite,
    Action ActivityChanged);
