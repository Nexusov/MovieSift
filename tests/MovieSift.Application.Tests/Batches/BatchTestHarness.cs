using System.Collections.Immutable;
using MovieSift.Application.Batches;
using MovieSift.Application.Inspection;
using MovieSift.Application.Jobs;
using MovieSift.Application.Ports;
using MovieSift.Core.Failures;
using MovieSift.Core.Media;
using MovieSift.Core.Planning;
using Xunit;

namespace MovieSift.Application.Tests.Batches;

/// <summary>Gates the real job coordinator's ports without claiming real media preservation.</summary>
internal sealed class BatchTestHarness : IMediaBackend, ISourceAccess, IOutputStore
{
    public const int TestTimeoutSeconds = 10;
    private int activeRemuxes;
    private int activeInspections;
    public Dictionary<string, MediaInspection> Inspections { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, SourceRevision> Revisions { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ExitCodes { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Failure> DestinationFailures { get; } = new(StringComparer.Ordinal);
    public List<string> InspectedPaths { get; } = [];
    public List<string> RemuxedPaths { get; } = [];
    public List<string> PublishedPaths { get; } = [];
    public List<ValidateDestinationRequest> DestinationValidations { get; } = [];
    public List<OwnedOutput> Reservations { get; } = [];
    public int MaximumActiveRemuxes { get; private set; }
    public int MaximumActiveInspections { get; private set; }
    public int VerifyCalls { get; private set; }
    public int CleanupCalls { get; private set; }
    public bool CleanupFails { get; set; }
    public Failure? RemuxFailure { get; set; }
    public Failure? PublicationFailure { get; set; }
    public Func<RemuxRequest, Task>? OnRemux { get; set; }
    public bool RequiresInputProjection { get; set; }
    public Func<PrepareInputRequest, Task>? OnPrepareInput { get; set; }
    public Func<InspectRequest, Task>? OnInspect { get; set; }
    public Func<VerifyRequest, Task>? OnVerify { get; set; }
    public Func<OutputRequest, Task>? OnCleanup { get; set; }
    public Action<OutputRequest>? OnPublish { get; set; }
    public BatchSession Session { get; }
    public BatchCoordinator Coordinator { get; }

    /// <summary>Creates distinguishable per-file identities and track identifiers.</summary>
    public BatchTestHarness(int count = 2)
    {
        for (var index = 0; index < count; index++)
        {
            var path = Path.Combine(Path.GetTempPath(), "MovieSift batch fake ports", $"episode {index}.mkv");
            var revision = new SourceRevision(path, $"identity-{index}", 100, 200);
            var trackOffset = index * 10;
            var inspection = new MediaInspection(revision, new("mkvmerge", "102.0", 20),
                [Track(trackOffset, TrackKind.Video), Track(trackOffset + 1, TrackKind.Audio), Track(trackOffset + 2, TrackKind.Subtitles)],
                [], $"Episode {index}", false, false);
            Inspections.Add(path, inspection);
            Revisions.Add(path, revision);
        }

        Session = new(Inspections.Values.Select(inspection => inspection.Source));
        Coordinator = new(Session, new InspectionService(this, this), new JobCoordinator(this, this, this), this);
    }

    /// <summary>Inspects and records independent default removals before the production Review boundary.</summary>
    public async Task<BatchReview> PrepareAsync()
    {
        await Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);

        foreach (var item in Session.Snapshot.Items)
        {
            ConfigureSelection(item);
        }

        var review = await Coordinator.ReviewSelectionsAsync(new(Session.Snapshot.Revision, [], TestContext.Current.CancellationToken));
        Assert.Null(review.Failure);
        Assert.Empty(review.ItemFailures);
        return Assert.IsType<BatchReview>(review.Review);
    }

    /// <summary>Builds selections from the item's own track identities instead of another episode's positions.</summary>
    public async Task<RemuxPlan> SelectAndReviewAsync(BatchItem item, string? destination = null, bool noChange = false)
    {
        ConfigureSelection(item, destination, noChange);
        await Coordinator.ReviewSelectionsAsync(new(Session.Snapshot.Revision, [], TestContext.Current.CancellationToken));
        return Assert.IsType<RemuxPlan>(Session.Snapshot.Items.Single(current => current.Id == item.Id).ApprovedPlan);
    }

    /// <summary>Edits only public selection and destination intent without injecting plan authority.</summary>
    public void ConfigureSelection(BatchItem item, string? destination = null, bool noChange = false)
    {
        var inspection = Assert.IsType<MediaInspection>(item.Inspection);
        var identifiers = inspection.Tracks.Where(track => noChange || track.Kind != TrackKind.Subtitles)
            .Select(track => track.Id).ToImmutableArray();
        Assert.True(Session.SetSelection(new(item.Id, identifiers)).Applied);
        Assert.True(Session.SetDestination(new(item.Id, destination ?? Path.ChangeExtension(item.SourcePath, ".trimmed.mkv"))).Applied);
    }

    /// <summary>Creates an asynchronously continuable deterministic lifecycle gate.</summary>
    public static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Bounds test failure without using timing as synchronization.</summary>
    public static Task Await(Task task) => task.WaitAsync(TimeSpan.FromSeconds(TestTimeoutSeconds), TestContext.Current.CancellationToken);

    /// <summary>Returns the file-specific immutable inspection after an optional gate.</summary>
    public async Task<MediaInspection> InspectAsync(InspectRequest request)
    {
        activeInspections++;
        MaximumActiveInspections = Math.Max(MaximumActiveInspections, activeInspections);
        InspectedPaths.Add(request.SourcePath);

        try
        {
            if (OnInspect is not null)
            {
                await OnInspect(request);
            }

            request.CancellationToken.ThrowIfCancellationRequested();
            return Inspections[request.SourcePath];
        }
        finally
        {
            activeInspections--;
        }
    }

    /// <summary>Models worker completion or cancellation while tracking concurrent remux invocations.</summary>
    public async Task<ToolOutcome> RemuxAsync(RemuxRequest request)
    {
        activeRemuxes++;
        MaximumActiveRemuxes = Math.Max(MaximumActiveRemuxes, activeRemuxes);
        RemuxedPaths.Add(request.Plan.Inspection.Source.FullPath);

        try
        {
            if (OnRemux is not null)
            {
                await OnRemux(request);
            }

            if (RemuxFailure is not null)
            {
                throw new OperationFailureException(RemuxFailure);
            }

            request.CancellationToken.ThrowIfCancellationRequested();
            var exitCode = ExitCodes.GetValueOrDefault(request.Plan.Inspection.Source.FullPath);
            return new(exitCode, exitCode == 1 ? "simulated warning" : "");
        }
        finally
        {
            activeRemuxes--;
        }
    }

    /// <summary>Counts real coordinator verification requests at an optional cancellation gate.</summary>
    public async Task<VerificationOutcome> VerifyAsync(VerifyRequest request)
    {
        VerifyCalls++;

        if (OnVerify is not null)
        {
            await OnVerify(request);
        }

        request.CancellationToken.ThrowIfCancellationRequested();
        return new(true, "simulated structural verification");
    }

    /// <summary>Returns current source or candidate identity without touching the filesystem.</summary>
    public Task<SourceRevision> ReadRevisionAsync(SourceRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Revisions[request.Path]);
    }

    /// <summary>Implements the explicit preparation boundary without adding filesystem effects to this controlled port.</summary>
    public Task PrepareDestinationAsync(ValidateDestinationRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <summary>Exposes required destination checks and controlled late conflicts.</summary>
    public Task ValidateDestinationAsync(ValidateDestinationRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        DestinationValidations.Add(request);

        if (DestinationFailures.TryGetValue(request.DestinationPath, out var failure))
        {
            throw new OperationFailureException(failure);
        }

        return Task.CompletedTask;
    }

    /// <summary>Reserves distinguishable candidates for each existing job identity.</summary>
    public Task<OwnedOutput> ReserveAsync(ReserveOutputRequest request)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MovieSift batch fake owned", request.Plan.Id.ToString("N"));
        var projection = request.RequiresInputProjection
            ? new OwnedInputProjection(new(Path.Combine(directory, "input-projection.mkv"), "owned-input", 0, 200), request.Plan.Inspection.Source) : null;
        var output = new OwnedOutput(request.Plan.Id, directory, Path.Combine(directory, "candidate.mkv"), request.Plan.DestinationPath, "token", InputProjection: projection);
        Reservations.Add(output);
        Revisions[output.CandidatePath] = new(output.CandidatePath, request.Plan.Id.ToString(), 80, 300);
        return Task.FromResult(output);
    }

    /// <summary>Opts controlled batches into the existing preparation boundary.</summary>
    public MediaWorkRequirements GetWorkRequirements(RemuxPlan plan) => new(RequiresInputProjection);

    /// <summary>Waits at the preparation gate without simulating a second worker owner.</summary>
    public async Task PrepareInputAsync(PrepareInputRequest request)
    {
        if (OnPrepareInput is not null)
        {
            await OnPrepareInput(request);
        }

        request.CancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Accepts only test-owned candidate reservations.</summary>
    public Task ValidateOwnershipAsync(OutputRequest request)
    {
        Assert.Contains(request.Output, Reservations);
        return Task.CompletedTask;
    }

    /// <summary>Models the existing coordinator's short persisted publication step.</summary>
    public Task MarkPublishingAsync(OutputRequest request) => Task.CompletedTask;

    /// <summary>Records publication facts independently from late cancellation or cleanup.</summary>
    public Task<PublishedOutput> PublishAsync(OutputRequest request)
    {
        if (PublicationFailure is not null)
        {
            throw new OperationFailureException(PublicationFailure);
        }

        PublishedPaths.Add(request.Output.DestinationPath);
        OnPublish?.Invoke(request);
        return Task.FromResult(new PublishedOutput(request.Output.DestinationPath, 80));
    }

    /// <summary>Awaits controlled cleanup before allowing dispatch to another item.</summary>
    public async Task<ImmutableArray<CleanupIssue>> CleanupAsync(OutputRequest request)
    {
        CleanupCalls++;

        if (OnCleanup is not null)
        {
            await OnCleanup(request);
        }

        return CleanupFails ? [new(request.Output.DirectoryPath, "simulated cleanup failure")] : [];
    }

    /// <summary>Has no restart records because the session is intentionally non-persistent.</summary>
    public Task<ImmutableArray<InterruptedOutput>> FindInterruptedAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ImmutableArray<InterruptedOutput>.Empty);

    /// <summary>Creates immutable track metadata with file-local identifiers.</summary>
    private static MediaTrack Track(int identifier, TrackKind kind) => new(identifier, (ulong)identifier + 1,
        kind, kind.ToString(), "eng", "en", "", kind == TrackKind.Audio ? 2 : null,
        kind != TrackKind.Subtitles, false, true, ImmutableDictionary<string, string>.Empty);
}
