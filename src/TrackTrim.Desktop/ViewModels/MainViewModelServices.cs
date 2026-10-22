using Serilog;
using TrackTrim.Application.Inspection;
using TrackTrim.Application.Jobs;
using TrackTrim.Application.Ports;
using TrackTrim.Desktop.Interaction;
using TrackTrim.Desktop.Localization;

namespace TrackTrim.Desktop.ViewModels;

public sealed record MainViewModelServices(
    InspectionService Inspection,
    JobCoordinator Jobs,
    ISettingsStore Settings,
    IOutputStore Outputs,
    IFileDialogs Dialogs,
    LocalizationService Localization,
    bool DataWritable,
    ILogger Log,
    BatchViewModel? Batch = null);
