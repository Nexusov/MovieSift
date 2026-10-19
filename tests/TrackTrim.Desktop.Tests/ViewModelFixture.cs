using System.Collections.Immutable;
using Serilog;
using TrackTrim.Application;
using TrackTrim.Application.Inspection;
using TrackTrim.Application.Jobs;
using TrackTrim.Application.Ports;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Media;
using TrackTrim.Desktop.Interaction;
using TrackTrim.Desktop.Localization;
using TrackTrim.Desktop.ViewModels;

namespace TrackTrim.Desktop.Tests;

/// <summary>Creates deterministic application ports without substituting them for media integration.</summary>
internal sealed class ViewModelFixture
{
    private static readonly string SourcePath = Path.Combine(Path.GetTempPath(), "TrackTrim UI source.mkv");
    public static readonly string OutputPath = Path.Combine(Path.GetTempPath(), "TrackTrim UI output.mkv");

    public ViewModelFixture()
    {
        var source = new SourceRevision(SourcePath, "fixture-identity", 1000, 1);
        var tracks = ImmutableArray.Create(
            CreateTrack(new(0, TrackKind.Video, "Video")),
            CreateTrack(new(1, TrackKind.Audio, "Audio A")),
            CreateTrack(new(2, TrackKind.Audio, "Audio B")),
            CreateTrack(new(3, TrackKind.Subtitles, "Subtitle")));
        Backend = new FakeMediaBackend(new(source, new("MKVToolNix", "102.0", 20), tracks, [], "Fixture", false, false));
        var sourceAccess = new FakeSourceAccess(source);
        var outputs = Outputs;
        ViewModel = new(new(new InspectionService(Backend, sourceAccess), new JobCoordinator(Backend, sourceAccess, outputs),
            Settings, outputs, new FakeDialogs(SourcePath), new LocalizationService(), true, new LoggerConfiguration().CreateLogger()));
    }

    public MainViewModel ViewModel { get; }
    public FakeMediaBackend Backend { get; }
    public FakeSettingsStore Settings { get; } = new();
    public FakeOutputStore Outputs { get; } = new();

    /// <summary>Loads the deterministic source through the same commands used by the UI.</summary>
    public async Task LoadAsync()
    {
        await ViewModel.OpenSourceCommand.ExecuteAsync(null);
        ViewModel.OutputPath = OutputPath;
    }

    /// <summary>Creates distinguishable file-local track identities for selection tests.</summary>
    private static MediaTrack CreateTrack(FixtureTrack request)
    {
        return new(request.Identifier, (ulong)request.Identifier + 100, request.Kind, "fixture-codec", "eng", "en",
            request.Title, request.Kind == TrackKind.Audio ? 2 : null, request.Identifier == 1, false, true,
            ImmutableDictionary<string, string>.Empty);
    }
}

internal sealed record FixtureTrack(int Identifier, TrackKind Kind, string Title);

/// <summary>Provides explicit language persistence for UI state tests.</summary>
internal sealed class FakeSettingsStore : ISettingsStore
{
    public string Language { get; private set; } = "en";
    public Exception? LoadException { get; set; }

    /// <summary>Returns the explicit in-memory language preference.</summary>
    public Task<string> LoadLanguageAsync(CancellationToken cancellationToken = default)
    {
        if (LoadException is { } exception)
        {
            return Task.FromException<string>(exception);
        }

        return Task.FromResult(Language);
    }

    /// <summary>Records the code supplied by the real language command.</summary>
    public Task SaveLanguageAsync(SaveLanguageRequest request)
    {
        Language = request.Language;
        return Task.CompletedTask;
    }
}

/// <summary>Returns preselected local paths without native dialogs in headless tests.</summary>
internal sealed class FakeDialogs(string sourcePath) : IFileDialogs
{
    /// <summary>Returns the test's selected local source without opening native UI.</summary>
    public Task<string?> SelectSourceAsync() => Task.FromResult<string?>(sourcePath);

    /// <summary>Returns selected paths for controlled batch presentation tests.</summary>
    public Task<IReadOnlyList<string>> SelectSourcesAsync() => Task.FromResult<IReadOnlyList<string>>([sourcePath]);

    /// <summary>Returns a folder without claiming native dialog acceptance.</summary>
    public Task<string?> SelectInputFolderAsync() => Task.FromResult<string?>(Path.GetTempPath());

    /// <summary>Returns a writable test destination folder.</summary>
    public Task<string?> SelectOutputFolderAsync() => Task.FromResult<string?>(Path.GetTempPath());
}

/// <summary>Provides a stable source revision for presentation-only workflow tests.</summary>
internal sealed class FakeSourceAccess(SourceRevision source) : ISourceAccess
{
    /// <summary>Reports the unchanged synthetic file identity.</summary>
    public Task<SourceRevision> ReadRevisionAsync(SourceRequest request) => Task.FromResult(source);

    /// <summary>Allows the synthetic destination without touching disk.</summary>
    public Task ValidateDestinationAsync(ValidateDestinationRequest request) => Task.CompletedTask;
}

/// <summary>Simulates outcomes and exposes deterministic remux synchronization points.</summary>
internal sealed class FakeMediaBackend(MediaInspection inspection) : IMediaBackend
{
    public TaskCompletionSource RemuxStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource? ContinueRemux { get; set; }
    public int ExitCode { get; set; }
    public bool VerificationPassed { get; set; } = true;
    public bool TerminationFails { get; set; }
    public int RemuxCount { get; private set; }
    public Exception? InspectionException { get; set; }

    /// <summary>Returns immutable representative metadata for presentation tests.</summary>
    public Task<MediaInspection> InspectAsync(InspectRequest request)
    {
        if (InspectionException is { } exception)
        {
            return Task.FromException<MediaInspection>(exception);
        }

        return Task.FromResult(inspection);
    }

    /// <summary>Exposes deterministic completion and cancellation rather than arbitrary sleeps.</summary>
    public async Task<ToolOutcome> RemuxAsync(RemuxRequest request)
    {
        RemuxCount++;
        request.Progress?.Report(37);
        RemuxStarted.TrySetResult();

        if (TerminationFails)
        {
            throw new OperationFailureException(new(ErrorCode.ProcessTerminationFailed));
        }

        if (ContinueRemux is not null)
        {
            await ContinueRemux.Task.WaitAsync(request.CancellationToken);
        }

        return new(ExitCode, ExitCode == 1 ? "simulated tool warning" : string.Empty);
    }

    /// <summary>Supplies either accepted or rejected verification evidence.</summary>
    public Task<VerificationOutcome> VerifyAsync(VerifyRequest request) => Task.FromResult(new VerificationOutcome(VerificationPassed, "simulated verification"));
}

/// <summary>Supplies outcome evidence without creating files in presentation tests.</summary>
internal sealed class FakeOutputStore : IOutputStore
{
    public ImmutableArray<CleanupIssue> CleanupIssues { get; set; } = [];
    public ImmutableArray<InterruptedOutput> Interrupted { get; set; } = [];
    public TaskCompletionSource CleanupStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource? ContinueCleanup { get; set; }
    public int CleanupCount { get; private set; }
    public int DiscoveryCount { get; private set; }

    /// <summary>Creates an in-memory ownership record for the synthetic candidate.</summary>
    public Task<OwnedOutput> ReserveAsync(ReserveOutputRequest request)
    {
        return Task.FromResult(new OwnedOutput(request.Plan.Id, "fixture-owned", "fixture-candidate.mkv", request.Plan.DestinationPath, "fixture-token"));
    }

    /// <summary>Accepts the immutable synthetic ownership token.</summary>
    public Task ValidateOwnershipAsync(OutputRequest request) => Task.CompletedTask;

    /// <summary>Marks the synthetic publication boundary without persistence.</summary>
    public Task MarkPublishingAsync(OutputRequest request) => Task.CompletedTask;

    /// <summary>Returns publication evidence without creating a media file.</summary>
    public Task<PublishedOutput> PublishAsync(OutputRequest request) => Task.FromResult(new PublishedOutput(request.Output.DestinationPath, 700));

    /// <summary>Reports complete cleanup for presentation-only candidates.</summary>
    public async Task<ImmutableArray<CleanupIssue>> CleanupAsync(OutputRequest request)
    {
        CleanupCount++;
        CleanupStarted.TrySetResult();

        if (ContinueCleanup is not null)
        {
            await ContinueCleanup.Task;
        }

        return CleanupIssues;
    }

    /// <summary>Starts presentation tests with no prior ownership records.</summary>
    public Task<ImmutableArray<InterruptedOutput>> FindInterruptedAsync(CancellationToken cancellationToken = default)
    {
        DiscoveryCount++;
        return Task.FromResult(Interrupted);
    }
}
