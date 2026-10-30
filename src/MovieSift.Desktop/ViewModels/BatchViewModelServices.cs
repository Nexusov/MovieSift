using MovieSift.Application.Batches;
using MovieSift.Application.Ports;
using MovieSift.Application.Presets;
using MovieSift.Desktop.Interaction;
using MovieSift.Desktop.Localization;
using Serilog;

namespace MovieSift.Desktop.ViewModels;

public sealed record BatchViewModelServices(
    BatchSession Session,
    BatchCoordinator Coordinator,
    IBatchInputDiscovery Discovery,
    IFileDialogs Dialogs,
    LocalizationService Localization,
    bool DataWritable,
    ILogger Log,
    RulePresetService Presets,
    IQueueListStore QueueLists,
    IOutputPreferencesStore? OutputPreferences = null);
