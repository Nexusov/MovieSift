using MovieSift.Application.Batches;
using MovieSift.Application.Ports;
using MovieSift.Desktop.Localization;
using Serilog;

namespace MovieSift.Desktop.ViewModels;

public sealed record QueueRecoveryOptions(
    BatchSession Session,
    BatchCoordinator Coordinator,
    IQueueListStore Store,
    LocalizationService Localization,
    Func<bool> CanManage,
    bool DataWritable,
    Action Changed,
    ILogger Log);
