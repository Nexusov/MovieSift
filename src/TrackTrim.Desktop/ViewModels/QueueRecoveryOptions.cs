using Serilog;
using TrackTrim.Application.Batches;
using TrackTrim.Application.Ports;
using TrackTrim.Desktop.Localization;

namespace TrackTrim.Desktop.ViewModels;

public sealed record QueueRecoveryOptions(
    BatchSession Session,
    BatchCoordinator Coordinator,
    IQueueListStore Store,
    LocalizationService Localization,
    Func<bool> CanManage,
    bool DataWritable,
    Action Changed,
    ILogger Log);
