using MovieSift.Application.Batches;
using MovieSift.Core.Failures;
using MovieSift.Desktop.Localization;

namespace MovieSift.Desktop.ViewModels;

public sealed record AggregateTracksViewModelOptions(
    BatchSession Session,
    LocalizationService Localization,
    Func<bool> CanEdit,
    Action<Failure?> SelectionEdited);
