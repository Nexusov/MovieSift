using Serilog;
using TrackTrim.Application.Ports;
using TrackTrim.Desktop.Appearance;
using TrackTrim.Desktop.Localization;

namespace TrackTrim.Desktop.ViewModels;

/// <summary>Supplies shell preferences, interrupted storage and the single unified media workspace.</summary>
public sealed record MainViewModelServices(
    ISettingsStore Settings,
    IOutputStore Outputs,
    LocalizationService Localization,
    bool DataWritable,
    ILogger Log,
    BatchViewModel Batch,
    AppearanceViewModel? Appearance = null);
