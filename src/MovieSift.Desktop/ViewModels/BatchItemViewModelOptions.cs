using MovieSift.Application.Batches;
using MovieSift.Desktop.Localization;

namespace MovieSift.Desktop.ViewModels;

public sealed record BatchItemViewModelOptions(
    BatchItem Item,
    BatchSession Session,
    LocalizationService Localization,
    string OutputFolder,
    Func<bool> CanEdit,
    Action Changed);
