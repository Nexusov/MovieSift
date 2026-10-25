using MovieSift.Application.Batches;
using MovieSift.Desktop.Localization;

namespace MovieSift.Desktop.ViewModels;

public sealed record BatchLanguageRulesOptions(
    BatchSession Session,
    BatchCoordinator Coordinator,
    LocalizationService Localization,
    Func<bool> CanEdit,
    Action ChoicesChanged);
