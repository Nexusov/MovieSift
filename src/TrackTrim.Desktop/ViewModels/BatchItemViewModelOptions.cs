using TrackTrim.Application.Batches;
using TrackTrim.Desktop.Localization;

namespace TrackTrim.Desktop.ViewModels;

public sealed record BatchItemViewModelOptions(
    BatchItem Item,
    BatchSession Session,
    LocalizationService Localization,
    string OutputFolder,
    Func<bool> CanEdit,
    Action Changed);
