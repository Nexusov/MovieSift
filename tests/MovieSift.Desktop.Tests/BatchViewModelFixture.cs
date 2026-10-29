using System.Collections.Immutable;
using MovieSift.Application;
using MovieSift.Application.Batches;
using MovieSift.Application.Inspection;
using MovieSift.Application.Jobs;
using MovieSift.Application.Localization;
using MovieSift.Application.Ports;
using MovieSift.Application.Presets;
using MovieSift.Core.Failures;
using MovieSift.Core.Media;
using MovieSift.Desktop.Appearance;
using MovieSift.Desktop.Interaction;
using MovieSift.Desktop.Localization;
using MovieSift.Desktop.ViewModels;
using Serilog;
using Xunit;

namespace MovieSift.Desktop.Tests;

/// <summary>Composes the actual unified workspace owner with file-specific deterministic UI ports.</summary>
internal sealed class BatchViewModelFixture
{
    public static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    public BatchSession Session { get; } = new();
    public FakeOutputStore Outputs { get; } = new();
    public FakeSettingsStore Settings { get; } = new();
    public BatchUiPresetStore PresetStore { get; } = new();
    public BatchUiQueueListStore QueueStore { get; } = new();
    public LocalizationService Localization { get; } = new();
    public BatchUiBackend Backend { get; } = new();
    public BatchUiDialogs Dialogs { get; }
    public BatchUiDiscovery Discovery { get; }
    public BatchCoordinator Coordinator { get; }
    public BatchUiSourceAccess SourceAccess { get; }
    public BatchViewModel Batch { get; }
    public MainViewModel Main { get; }
    public AppearanceViewModel Appearance { get; }
    public Task QueueInitialization { get; }

    /// <summary>Shares one job coordinator for every file in the unified workspace.</summary>
    public BatchViewModelFixture(IRulePresetStore? presetStore = null, IQueueListStore? queueStore = null, bool initializeQueue = true,
        ITranslationPackStore? translationPacks = null, IOutputPreferencesStore? outputPreferences = null)
    {
        Dialogs = new(Backend.Inspections.Keys.ToArray());
        Discovery = new(Backend.Inspections.Values.Select(inspection => inspection.Source).ToImmutableArray());
        SourceAccess = new(Backend);
        var jobs = new JobCoordinator(Backend, SourceAccess, Outputs);
        Coordinator = new(Session, new InspectionService(Backend, SourceAccess), jobs, SourceAccess);
        var logger = new LoggerConfiguration().CreateLogger();
        Batch = new(new(Session, Coordinator, Discovery, Dialogs, Localization, true, logger, new(presetStore ?? PresetStore), queueStore ?? QueueStore, outputPreferences));
        Appearance = new(new(new WorkspaceAppearanceStore(), Localization, new AppearanceApplier(Avalonia.Application.Current)));
        Main = new(new(Settings, Outputs, Localization, true, logger, Batch, Appearance, TranslationPacks: translationPacks));

        QueueInitialization = initializeQueue ? Batch.Recovery.InitializeAsync() : Task.CompletedTask;
    }

    /// <summary>Uses the real add-files and output-folder commands without invoking native pickers.</summary>
    public async Task LoadAsync()
    {
        await QueueInitialization;
        await Batch.AddFilesCommand.ExecuteAsync(null);
        await Batch.ChooseOutputCommand.ExecuteAsync(null);
    }

    /// <summary>Approves a distinct subtitle-removal plan for each independently inspected valid file.</summary>
    public async Task PrepareAsync()
    {
        await LoadAsync();

        foreach (var item in Batch.Items.Where(item => item.Snapshot.Inspection is not null))
        {
            Batch.SelectedItem = item;

            foreach (var track in item.Tracks.Where(track => track.Track.Kind == TrackKind.Subtitles))
            {
                track.Keep = false;
            }
        }

        await Batch.ReviewCommand.ExecuteAsync(null);
    }

    /// <summary>Creates a deterministic continuation gate with no synchronous callback recursion.</summary>
    public static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Bounds a test synchronization failure without using delays as lifecycle evidence.</summary>
    public static Task Await(Task task) => task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
}

/// <summary>Returns test-only picker selections, preserving null and empty cancellation outcomes.</summary>
internal sealed class BatchUiDialogs(IReadOnlyList<string> sources) : IFileDialogs
{
    public IReadOnlyList<string> Sources { get; set; } = sources;
    public string? OutputFolder { get; set; } = Path.Combine(Path.GetTempPath(), "MovieSift batch UI output");
    public int InputFolderRequests { get; private set; }

    /// <summary>Returns the controlled explicit batch addition.</summary>
    public Task<IReadOnlyList<string>> SelectSourcesAsync() => Task.FromResult(Sources);

    /// <summary>Returns a synthetic folder without claiming native dialog coverage.</summary>
    public Task<string?> SelectInputFolderAsync()
    {
        InputFolderRequests++;
        return Task.FromResult<string?>(Path.GetTempPath());
    }

    /// <summary>Returns the explicitly chosen shared destination folder.</summary>
    public Task<string?> SelectOutputFolderAsync() => Task.FromResult(OutputFolder);
}

/// <summary>Supplies stable discovery identities or an explicit rejection/invalid-entry result.</summary>
internal sealed class BatchUiDiscovery(ImmutableArray<SourceRevision> sources) : IBatchInputDiscovery
{
    public BatchInputDiscoveryResult? Result { get; set; }
    public BatchInputDiscoveryRequest? LastRequest { get; private set; }
    public Func<BatchInputDiscoveryRequest, Task<BatchInputDiscoveryResult>>? OnDiscover { get; set; }

    /// <summary>Returns discovered rows without accessing the real filesystem.</summary>
    public Task<BatchInputDiscoveryResult> DiscoverAsync(BatchInputDiscoveryRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        LastRequest = request;

        if (OnDiscover is not null)
        {
            return OnDiscover(request);
        }

        return Task.FromResult(Result ?? new(sources, []));
    }
}

/// <summary>Keeps candidate validation stable while preserving each source's actual fake identity.</summary>
internal sealed class BatchUiSourceAccess(BatchUiBackend backend) : ISourceAccess
{
    public Func<ValidateDestinationRequest, Task>? OnValidateDestination { get; set; }
    /// <summary>Returns matching per-file revisions or the synthetic owned candidate revision.</summary>
    public Task<SourceRevision> ReadRevisionAsync(SourceRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        var revision = backend.Inspections.TryGetValue(request.Path, out var inspection)
            ? inspection.Source : new(request.Path, "candidate-identity", 700, 2);
        return Task.FromResult(revision);
    }

    /// <summary>Implements the explicit preparation boundary without adding filesystem effects to this controlled port.</summary>
    public Task PrepareDestinationAsync(ValidateDestinationRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <summary>Leaves filesystem collision behavior to separate Application and real-media suites.</summary>
    public Task ValidateDestinationAsync(ValidateDestinationRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        return OnValidateDestination?.Invoke(request) ?? Task.CompletedTask;
    }
}

/// <summary>Provides deliberately different file-local track orders and gated backend outcomes.</summary>
internal sealed class BatchUiBackend : IMediaBackend
{
    public Dictionary<string, MediaInspection> Inspections { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Failure> InspectionFailures { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ExitCodes { get; } = new(StringComparer.Ordinal);
    public List<RemuxRequest> RemuxRequests { get; } = [];
    public Func<InspectRequest, Task>? OnInspect { get; set; }
    public Func<RemuxRequest, Task>? OnRemux { get; set; }

    /// <summary>Creates two ordinary inputs whose numeric identities and track order cannot be copied blindly.</summary>
    public BatchUiBackend()
    {
        var firstPath = Path.Combine(Path.GetTempPath(), "MovieSift batch UI source", "episode A.mkv");
        var secondPath = Path.Combine(Path.GetTempPath(), "MovieSift batch UI source", "episode Б.mkv");
        Inspections.Add(firstPath, new(new(firstPath, "episode-A", 1000, 1), new("MKVToolNix", "102.0", 20),
            [Track(new(0, TrackKind.Video, "Video A")), Track(new(1, TrackKind.Audio, "Audio A default", true)),
                Track(new(2, TrackKind.Audio, "Audio A other")), Track(new(3, TrackKind.Subtitles, "Subtitle A"))], [], "First", false, false));
        Inspections.Add(secondPath, new(new(secondPath, "episode-B", 1100, 1), new("MKVToolNix", "102.0", 20),
            [Track(new(77, TrackKind.Subtitles, "Subtitle B")), Track(new(25, TrackKind.Audio, "Audio B other")),
                Track(new(8, TrackKind.Video, "Video B")), Track(new(61, TrackKind.Audio, "Audio B default", true))], [], "Second", false, false));
    }

    /// <summary>Completes only the requested file's immutable inspection after an optional gate.</summary>
    public async Task<MediaInspection> InspectAsync(InspectRequest request)
    {
        if (OnInspect is not null)
        {
            await OnInspect(request);
        }

        request.CancellationToken.ThrowIfCancellationRequested();

        if (InspectionFailures.TryGetValue(request.SourcePath, out var failure))
        {
            throw new OperationFailureException(failure);
        }

        return Inspections[request.SourcePath];
    }

    /// <summary>Retains each requested immutable plan and allows deterministic active-worker cancellation.</summary>
    public async Task<ToolOutcome> RemuxAsync(RemuxRequest request)
    {
        RemuxRequests.Add(request);
        request.Progress?.Report(37);

        if (OnRemux is not null)
        {
            await OnRemux(request);
        }

        request.CancellationToken.ThrowIfCancellationRequested();
        var exitCode = ExitCodes.GetValueOrDefault(request.Plan.Inspection.Source.FullPath);
        return new(exitCode, exitCode == 1 ? "simulated batch worker warning" : "simulated batch worker diagnostics");
    }

    /// <summary>Reports simulated verification separately from any real remux preservation claim.</summary>
    public Task<VerificationOutcome> VerifyAsync(VerifyRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new VerificationOutcome(true, "simulated batch verification"));
    }

    /// <summary>Creates immutable distinguishable track metadata without imposing row-based identity.</summary>
    private static MediaTrack Track(BatchUiTrack request) => new(request.Id, (ulong)request.Id + 100,
        request.Kind, "fixture-codec", "eng", "en", request.Title, request.Kind == TrackKind.Audio ? 2 : null,
        request.IsDefault, false, true, ImmutableDictionary<string, string>.Empty);
}

internal sealed record BatchUiTrack(int Id, TrackKind Kind, string Title, bool IsDefault = false);
