using TrackTrim.Application.Batches;
using TrackTrim.Core.Failures;
using TrackTrim.Desktop.Localization;

namespace TrackTrim.Desktop.ViewModels;

public sealed record AggregateTracksViewModelOptions(
    BatchSession Session,
    LocalizationService Localization,
    Func<bool> CanEdit,
    Action<Failure?> SelectionEdited);
