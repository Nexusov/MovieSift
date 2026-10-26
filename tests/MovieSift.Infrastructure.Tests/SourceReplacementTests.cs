using System.Collections.Immutable;
using System.Runtime.InteropServices;
using MovieSift.Application.Batches;
using MovieSift.Application.Jobs;
using MovieSift.Application.Ports;
using MovieSift.Core.Failures;
using MovieSift.Core.Media;
using MovieSift.Core.Planning;
using MovieSift.Infrastructure.Files;
using Xunit;

namespace MovieSift.Infrastructure.Tests;

/// <summary>Exercises actual replacement, ownership and failure storage with a controlled media backend.</summary>
public sealed class SourceReplacementTests
{
    /// <summary>A successful verified replacement changes only its original path and removes its own transient backup.</summary>
    [Fact]
    public async Task VerifiedReplacementPublishesAtTheOriginalName()
    {
        using var workspace = new TestWorkspace();
        var fixture = await CreateAsync(workspace);
        var result = await fixture.Jobs.RunAsync(new(fixture.Plan));
        Assert.Equal(JobPhase.Completed, result.Phase);
        Assert.Equal(fixture.Plan.DestinationPath, result.PublishedPath);
        Assert.Equal(ReplacementBackend.Result.Length, result.OutputLength);
        Assert.Empty(result.CleanupIssues);
        Assert.Equal(ReplacementBackend.Result, await File.ReadAllTextAsync(fixture.Plan.DestinationPath, TestContext.Current.CancellationToken));
        AssertClean(workspace);
    }

    /// <summary>Remux, verification and cancellation failures cannot change the source or leave an owned candidate.</summary>
    [Theory]
    [InlineData("tool")]
    [InlineData("verification")]
    [InlineData("cancel")]
    public async Task FailureBeforeCommitPreservesTheOriginal(string failure)
    {
        using var workspace = new TestWorkspace();
        var fixture = await CreateAsync(workspace);
        fixture.Backend.ExitCode = failure == "tool" ? 2 : 0;
        fixture.Backend.Verifies = failure != "verification";
        fixture.Backend.Cancel = failure == "cancel";
        var result = await fixture.Jobs.RunAsync(new(fixture.Plan));
        Assert.Equal(failure == "cancel" ? JobPhase.Cancelled : JobPhase.Failed, result.Phase);
        Assert.Null(result.PublishedPath);
        Assert.Equal(ReplacementBackend.Original, await File.ReadAllTextAsync(fixture.Plan.DestinationPath, TestContext.Current.CancellationToken));
        AssertClean(workspace);
    }

    /// <summary>NoChange never reserves storage or creates another media file, even in replacement mode.</summary>
    [Fact]
    public async Task NoChangeLeavesTheOriginalUntouchedWithoutReservation()
    {
        using var workspace = new TestWorkspace();
        var fixture = await CreateAsync(workspace);
        var result = await fixture.Jobs.RunAsync(new(fixture.Plan with { RetainedTracks = fixture.Plan.Inspection.Tracks }));
        Assert.Equal(JobPhase.NoChange, result.Phase);
        Assert.Equal(0, fixture.Backend.RemuxCalls);
        Assert.False(Directory.Exists(Path.Combine(workspace.Path, "data")));
        Assert.Equal(ReplacementBackend.Original, await File.ReadAllTextAsync(fixture.Plan.DestinationPath, TestContext.Current.CancellationToken));
    }

    /// <summary>A warning candidate cannot replace an original before the existing explicit warning approval.</summary>
    [Fact]
    public async Task WarningApprovalReusesTheCandidateAndThenReplaces()
    {
        using var workspace = new TestWorkspace();
        var fixture = await CreateAsync(workspace);
        fixture.Backend.ExitCode = 1;
        var pending = await fixture.Jobs.RunAsync(new(fixture.Plan));
        Assert.Equal(JobPhase.AwaitingWarningReview, pending.Phase);
        Assert.Equal(ReplacementBackend.Original, await File.ReadAllTextAsync(fixture.Plan.DestinationPath, TestContext.Current.CancellationToken));
        var completed = await fixture.Jobs.PublishWarningsAsync(new(fixture.Plan.Id));
        Assert.Equal(JobPhase.Completed, completed.Phase);
        Assert.Equal(1, fixture.Backend.RemuxCalls);
        Assert.Equal(ReplacementBackend.Result, await File.ReadAllTextAsync(fixture.Plan.DestinationPath, TestContext.Current.CancellationToken));
        AssertClean(workspace);
    }

    /// <summary>The final identity check rejects a source changed after media verification.</summary>
    [Fact]
    public async Task ChangedSourceAtTheCommitBoundaryIsNotReplaced()
    {
        using var workspace = new TestWorkspace();
        var fixture = await CreateAsync(workspace, (boundary, output) =>
        {
            if (boundary == ReplacementBoundary.BeforeReplace)
            {
                File.WriteAllText(output.DestinationPath, "changed by another process");
            }
        });
        var result = await fixture.Jobs.RunAsync(new(fixture.Plan));
        Assert.Equal(ErrorCode.SourceChanged, result.Failure?.Code);
        Assert.Equal("changed by another process", await File.ReadAllTextAsync(fixture.Plan.DestinationPath, TestContext.Current.CancellationToken));
        AssertClean(workspace);
    }

    /// <summary>Known publication survives a late failure while the durable record controls safe backup cleanup.</summary>
    [Theory]
    [InlineData("Replaced", false)]
    [InlineData("BeforePublishedRecord", true)]
    public async Task FaultAfterReplacementPreservesPublicationFacts(string boundaryName, bool retainsBackup)
    {
        using var workspace = new TestWorkspace();
        var boundaryToFail = Enum.Parse<ReplacementBoundary>(boundaryName);
        var fixture = await CreateAsync(workspace, (boundary, _) =>
        {
            if (boundary == boundaryToFail)
            {
                throw new IOException("controlled post-replacement failure");
            }
        });
        var result = await fixture.Jobs.RunAsync(new(fixture.Plan));
        Assert.Equal(JobPhase.Completed, result.Phase);
        Assert.Equal(fixture.Plan.DestinationPath, result.PublishedPath);
        Assert.Equal(ReplacementBackend.Result, await File.ReadAllTextAsync(fixture.Plan.DestinationPath, TestContext.Current.CancellationToken));

        if (retainsBackup)
        {
            Assert.NotEmpty(result.CleanupIssues);
            Assert.Equal(ReplacementBackend.Original, await File.ReadAllTextAsync(BackupPath(fixture.Plan), TestContext.Current.CancellationToken));
            Assert.False(Assert.Single(await fixture.Store.FindInterruptedAsync(TestContext.Current.CancellationToken)).CanClean);
        }
        else
        {
            Assert.Empty(result.CleanupIssues);
            AssertClean(workspace);
        }
    }

    /// <summary>External destination changes after the swap never trigger rollback over that new file or deletion of the original backup.</summary>
    [Fact]
    public async Task UncertainReplacementPreservesBackupJournalAndForeignDestination()
    {
        using var workspace = new TestWorkspace();
        var fixture = await CreateAsync(workspace, (boundary, output) =>
        {
            if (boundary == ReplacementBoundary.Replaced)
            {
                File.Delete(output.DestinationPath);
                File.WriteAllText(output.DestinationPath, "foreign destination");
            }
        });
        var result = await fixture.Jobs.RunAsync(new(fixture.Plan));
        Assert.Equal(ErrorCode.PublicationUncertain, result.Failure?.Code);
        Assert.Null(result.PublishedPath);
        Assert.Equal("foreign destination", await File.ReadAllTextAsync(fixture.Plan.DestinationPath, TestContext.Current.CancellationToken));
        Assert.Equal(ReplacementBackend.Original, await File.ReadAllTextAsync(BackupPath(fixture.Plan), TestContext.Current.CancellationToken));
        var interrupted = Assert.Single(await fixture.Store.FindInterruptedAsync(TestContext.Current.CancellationToken));
        Assert.False(interrupted.CanClean);
        Assert.NotEmpty(await fixture.Store.CleanupAsync(new(interrupted.Output!)));
        Assert.True(File.Exists(BackupPath(fixture.Plan)));
    }

    /// <summary>Replacement cannot silently break an existing hard-link relationship.</summary>
    [Fact]
    public async Task HardLinkedSourceRequiresCopyMode()
    {
        using var workspace = new TestWorkspace();
        var fixture = await CreateAsync(workspace);
        var source = fixture.Plan.DestinationPath;
        var alias = Path.Combine(workspace.Path, "alias.mkv");

        if (OperatingSystem.IsWindows())
        {
            Assert.True(CreateHardLink(alias, source, IntPtr.Zero));
        }
        else
        {
            Assert.Equal(0, Link(source, alias));
        }

        var result = await fixture.Jobs.RunAsync(new(fixture.Plan));
        Assert.Equal(ErrorCode.InvalidDestination, result.Failure?.Code);
        Assert.Equal(0, fixture.Backend.RemuxCalls);
        Assert.Equal(ReplacementBackend.Original, await File.ReadAllTextAsync(alias, TestContext.Current.CancellationToken));
    }

    /// <summary>A read-only original is rejected before remux rather than silently changing permissions to replace it.</summary>
    [Fact]
    public async Task ReadOnlySourceIsPreserved()
    {
        using var workspace = new TestWorkspace();
        var fixture = await CreateAsync(workspace);
        var source = fixture.Plan.DestinationPath;
        var attributes = File.GetAttributes(source);
        File.SetAttributes(source, attributes | FileAttributes.ReadOnly);

        try
        {
            var result = await fixture.Jobs.RunAsync(new(fixture.Plan));
            Assert.Equal(ErrorCode.InvalidDestination, result.Failure?.Code);
            Assert.Equal(0, fixture.Backend.RemuxCalls);
            Assert.Equal(ReplacementBackend.Original, await File.ReadAllTextAsync(source, TestContext.Current.CancellationToken));
            Assert.True(File.GetAttributes(source).HasFlag(FileAttributes.ReadOnly));
        }
        finally
        {
            File.SetAttributes(source, attributes);
        }
    }

    /// <summary>Copy mode cannot overwrite either its own source or another existing destination.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OrdinaryCopyRetainsNoOverwritePolicy(bool ownSource)
    {
        using var workspace = new TestWorkspace();
        var fixture = await CreateAsync(workspace);
        var destination = ownSource ? fixture.Plan.DestinationPath : Path.Combine(workspace.Path, "already exists.mkv");

        if (!ownSource)
        {
            await File.WriteAllTextAsync(destination, "unrelated existing file", TestContext.Current.CancellationToken);
        }

        var result = await fixture.Jobs.RunAsync(new(fixture.Plan with { OutputMode = OutputMode.Copy, DestinationPath = destination }));
        Assert.Equal(ownSource ? ErrorCode.InvalidDestination : ErrorCode.DestinationExists, result.Failure?.Code);
        Assert.Equal(0, fixture.Backend.RemuxCalls);
        Assert.Equal(ownSource ? ReplacementBackend.Original : "unrelated existing file",
            await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken));
    }

    /// <summary>Adjacent recursive outputs do not need an unrelated output root and do not guess input provenance from a suffix.</summary>
    [Fact]
    public async Task RecursiveSourceDirectoriesIncludeLegitimateTrimmedInputs()
    {
        using var workspace = new TestWorkspace();
        var season = Directory.CreateDirectory(Path.Combine(workspace.Path, "Season 1")).FullName;
        var source = Path.Combine(season, "episode.trimmed.mkv");
        await File.WriteAllTextAsync(source, ReplacementBackend.Original, TestContext.Current.CancellationToken);
        var discovery = new FileBatchInputDiscovery(new FileSourceAccess());
        var result = await discovery.DiscoverAsync(new(BatchInputKind.RecursiveFolder, [workspace.Path],
            TestContext.Current.CancellationToken, UseSourceDirectories: true));
        Assert.Null(result.Rejection);
        Assert.Empty(result.Issues);
        Assert.Equal(source, Assert.Single(result.Sources).FullPath);
    }

    /// <summary>Creates a real local source and production coordinator around a deliberately non-media test backend.</summary>
    internal static async Task<ReplacementFixture> CreateAsync(TestWorkspace workspace,
        Action<ReplacementBoundary, OwnedOutput>? boundary = null)
    {
        var source = Path.Combine(workspace.Path, "source Кириллица.mkv");
        await File.WriteAllTextAsync(source, ReplacementBackend.Original, TestContext.Current.CancellationToken);
        var access = new FileSourceAccess();
        var revision = await access.ReadRevisionAsync(new(source));
        var tracks = ImmutableArray.Create(Track(0, TrackKind.Video), Track(1, TrackKind.Audio), Track(2, TrackKind.Subtitles));
        var inspection = new MediaInspection(revision, new("controlled", "1", 1), tracks, [], "", false, false);
        var plan = PlanBuilder.Build(new(inspection, [0, 1], source, false, OutputMode: OutputMode.ReplaceSource)).Plan!;
        var store = new OwnedOutputStore(Path.Combine(workspace.Path, "data"), null, boundary);
        var backend = new ReplacementBackend();
        return new(plan, backend, store, new(backend, access, store));
    }

    /// <summary>Provides independent track identities without claiming encoded-media verification.</summary>
    private static MediaTrack Track(int id, TrackKind kind) => new(id, (ulong)id + 1, kind, "test", "eng", "en", "", null,
        false, false, true, ImmutableDictionary<string, string>.Empty);

    /// <summary>Asserts that successful or pre-commit-failed operations removed only their temporary ownership storage.</summary>
    private static void AssertClean(TestWorkspace workspace)
    {
        Assert.Empty(Directory.EnumerateDirectories(workspace.Path, ".moviesift-*"));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(workspace.Path, "data", "ownership")));
    }

    /// <summary>Uses the defined private backup layout to inspect recovery evidence in this test-owned directory.</summary>
    private static string BackupPath(RemuxPlan plan) => Path.Combine(Path.GetDirectoryName(plan.DestinationPath)!,
        $".moviesift-{plan.Id:N}", "original.mkv");

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string existingPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);
}

internal sealed record ReplacementFixture(RemuxPlan Plan, ReplacementBackend Backend, OwnedOutputStore Store, JobCoordinator Jobs);

/// <summary>Writes recognizable test bytes through the real candidate path and exposes controlled verification outcomes.</summary>
internal sealed class ReplacementBackend : IMediaBackend
{
    internal const string Original = "original unmodified payload";
    internal const string Result = "verified result";
    internal int ExitCode { get; set; }
    internal bool Verifies { get; set; } = true;
    internal bool Cancel { get; set; }
    internal int RemuxCalls { get; private set; }

    /// <summary>Inspection is supplied directly so these tests exercise storage, not media formats.</summary>
    public Task<MediaInspection> InspectAsync(InspectRequest request) => throw new NotSupportedException();

    /// <summary>Writes only to the private candidate and optionally stops before the publication boundary.</summary>
    public async Task<ToolOutcome> RemuxAsync(RemuxRequest request)
    {
        RemuxCalls++;
        Assert.NotEqual(request.Plan.Inspection.Source.FullPath, request.CandidatePath);
        await File.WriteAllTextAsync(request.CandidatePath, Result, request.CancellationToken);

        if (Cancel)
        {
            throw new OperationCanceledException();
        }

        return new(ExitCode, "controlled backend");
    }

    /// <summary>Reports the chosen controlled verification outcome without asserting actual encoded packet integrity.</summary>
    public Task<VerificationOutcome> VerifyAsync(VerifyRequest request) => Task.FromResult(new VerificationOutcome(Verifies, "controlled verification"));
}
