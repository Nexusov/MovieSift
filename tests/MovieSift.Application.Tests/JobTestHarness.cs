using System.Collections.Immutable;
using MovieSift.Application;
using MovieSift.Application.Jobs;
using MovieSift.Application.Ports;
using MovieSift.Core.Failures;
using MovieSift.Core.Media;
using MovieSift.Core.Planning;

namespace MovieSift.Application.Tests;

/// <summary>Provides deterministic failure boundaries without simulating real media preservation.</summary>
internal sealed class JobTestHarness : IMediaBackend, ISourceAccess, IOutputStore
{
    public MediaInspection Inspection { get; } = new(
        new("source.mkv", "identity", 100, 200), new("mkvmerge", "102.0", 20),
        [new(0, 10, TrackKind.Video, "video", "und", null, "", null, true, false, true, ImmutableDictionary<string, string>.Empty),
            new(1, 11, TrackKind.Audio, "audio", "eng", "en", "", 2, true, false, true, ImmutableDictionary<string, string>.Empty),
            new(2, 12, TrackKind.Subtitles, "subtitles", "eng", "en", "", null, false, false, true, ImmutableDictionary<string, string>.Empty)],
        [], "", false, false);

    public int RemuxCalls { get; private set; }
    public int VerifyCalls { get; private set; }
    public int PublishCalls { get; private set; }
    public int CleanupCalls { get; private set; }
    public int ReserveCalls { get; private set; }
    public int RevisionCalls { get; private set; }
    public long AvailableFreeSpace { get; set; } = long.MaxValue;
    public int ExitCode { get; set; }
    public ToolDisposition? Disposition { get; set; }
    public bool VerificationPasses { get; set; } = true;
    public bool FailCleanup { get; set; }
    public bool SourceChanges { get; set; }
    public bool LateCollision { get; set; }
    public bool TerminationFails { get; set; }
    public bool CandidateChanges { get; set; }
    public Action? OnRemux { get; set; }
    public Action? OnVerify { get; set; }
    public Action? OnPublish { get; set; }
    public Action? OnCleanup { get; set; }

    public JobCoordinator Coordinator => new(this, this, this);

    /// <summary>Creates a valid plan that removes one subtitle track.</summary>
    public RemuxPlan CreatePlan() => PlanBuilder.Build(new(Inspection, [0, 1], "output.mkv", false)).Plan!;

    /// <summary>Returns the fixed inspected description.</summary>
    public Task<MediaInspection> InspectAsync(InspectRequest request) => Task.FromResult(Inspection);

    /// <summary>Invokes a deterministic hook at the simulated worker boundary.</summary>
    public Task<ToolOutcome> RemuxAsync(RemuxRequest request)
    {
        RemuxCalls++;
        OnRemux?.Invoke();

        if (TerminationFails)
        {
            throw new OperationFailureException(new(ErrorCode.ProcessTerminationFailed));
        }

        request.CancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ToolOutcome(ExitCode, ExitCode == 1 ? "simulated warning" : "", Disposition));
    }

    /// <summary>Exposes required verification success or failure independently of tool exit status.</summary>
    public Task<VerificationOutcome> VerifyAsync(VerifyRequest request)
    {
        VerifyCalls++;
        request.CancellationToken.ThrowIfCancellationRequested();
        OnVerify?.Invoke();
        return Task.FromResult(new VerificationOutcome(VerificationPasses, "simulated structural check"));
    }

    /// <summary>Can invalidate source identity after worker completion.</summary>
    public Task<SourceRevision> ReadRevisionAsync(SourceRequest request)
    {
        RevisionCalls++;

        if (request.Path == "candidate.mkv")
        {
            return Task.FromResult(Inspection.Source with { FullPath = request.Path, Length = CandidateChanges ? 99 : 80 });
        }

        return Task.FromResult(SourceChanges ? Inspection.Source with { Length = 101 } : Inspection.Source);
    }

    /// <summary>Implements the explicit preparation boundary without adding filesystem effects to this controlled port.</summary>
    public Task PrepareDestinationAsync(ValidateDestinationRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <summary>Models changing capacity without allocating large files; publication races remain separate.</summary>
    public Task ValidateDestinationAsync(ValidateDestinationRequest request)
    {
        if (AvailableFreeSpace < request.AdditionalAllocationBytes)
        {
            throw new OperationFailureException(new(ErrorCode.InsufficientSpace));
        }

        return Task.CompletedTask;
    }

    /// <summary>Creates an application-owned candidate record without filesystem effects.</summary>
    public Task<OwnedOutput> ReserveAsync(ReserveOutputRequest request)
    {
        ReserveCalls++;
        return Task.FromResult(new OwnedOutput(request.Plan.Id, "owned", "candidate.mkv", "output.mkv", "token"));
    }

    /// <summary>Accepts only this harness's candidate ownership.</summary>
    public Task ValidateOwnershipAsync(OutputRequest request) => Task.CompletedTask;

    /// <summary>Simulates a successful persisted publication stage.</summary>
    public Task MarkPublishingAsync(OutputRequest request) => Task.CompletedTask;

    /// <summary>Can create a collision at the exact irreversible publication boundary.</summary>
    public Task<PublishedOutput> PublishAsync(OutputRequest request)
    {
        PublishCalls++;

        if (LateCollision)
        {
            throw new OperationFailureException(new(ErrorCode.DestinationExists));
        }

        OnPublish?.Invoke();
        return Task.FromResult(new PublishedOutput("output.mkv", 80));
    }

    /// <summary>Reports a secondary cleanup issue independently of successful publication.</summary>
    public Task<ImmutableArray<CleanupIssue>> CleanupAsync(OutputRequest request)
    {
        CleanupCalls++;
        OnCleanup?.Invoke();
        ImmutableArray<CleanupIssue> issues = FailCleanup ? [new("owned", "simulated cleanup failure")] : [];
        return Task.FromResult(issues);
    }

    /// <summary>Has no interrupted filesystem records.</summary>
    public Task<ImmutableArray<InterruptedOutput>> FindInterruptedAsync(CancellationToken cancellationToken = default) => Task.FromResult(ImmutableArray<InterruptedOutput>.Empty);
}
