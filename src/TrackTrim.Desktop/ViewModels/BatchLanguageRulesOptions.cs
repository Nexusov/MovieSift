using TrackTrim.Application.Batches;
using TrackTrim.Desktop.Localization;

namespace TrackTrim.Desktop.ViewModels;

public sealed record BatchLanguageRulesOptions(
    BatchSession Session,
    BatchCoordinator Coordinator,
    LocalizationService Localization,
    Func<bool> CanEdit,
    Action ChoicesChanged);
