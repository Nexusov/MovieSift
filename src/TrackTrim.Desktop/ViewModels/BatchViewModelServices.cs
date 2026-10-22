using Serilog;
using TrackTrim.Application.Batches;
using TrackTrim.Application.Ports;
using TrackTrim.Application.Presets;
using TrackTrim.Desktop.Interaction;
using TrackTrim.Desktop.Localization;

namespace TrackTrim.Desktop.ViewModels;

public sealed record BatchViewModelServices(
    BatchSession Session,
    BatchCoordinator Coordinator,
    IBatchInputDiscovery Discovery,
    IFileDialogs Dialogs,
    LocalizationService Localization,
    bool DataWritable,
    ILogger Log,
    RulePresetService Presets,
    IQueueListStore QueueLists);
