using MovieSift.Application.Localization;
using MovieSift.Application.Ports;
using MovieSift.Desktop.Appearance;
using MovieSift.Desktop.Localization;
using MovieSift.Desktop.Updates;
using Serilog;

namespace MovieSift.Desktop.ViewModels;

/// <summary>Supplies shell preferences, interrupted storage and the single unified media workspace.</summary>
public sealed record MainViewModelServices(
    ISettingsStore Settings,
    IOutputStore Outputs,
    LocalizationService Localization,
    bool DataWritable,
    ILogger Log,
    BatchViewModel Batch,
    AppearanceViewModel? Appearance = null,
    UpdatesViewModel? Updates = null,
    ITranslationPackStore? TranslationPacks = null);
