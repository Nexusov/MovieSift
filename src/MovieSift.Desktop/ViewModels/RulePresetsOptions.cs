using MovieSift.Application.Batches;
using MovieSift.Application.Presets;
using MovieSift.Desktop.Localization;

namespace MovieSift.Desktop.ViewModels;

public sealed record RulePresetsOptions(
    RulePresetService Service,
    BatchLanguageRulesViewModel Editor,
    BatchSession Session,
    LocalizationService Localization,
    Func<bool> CanEdit,
    Func<bool> CanWrite,
    Action ActivityChanged);
