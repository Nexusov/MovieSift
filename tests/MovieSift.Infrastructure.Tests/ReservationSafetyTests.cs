using System.Collections.Immutable;
using MovieSift.Application.Jobs;
using MovieSift.Application.Ports;
using MovieSift.Core.Failures;
using MovieSift.Core.Media;
using MovieSift.Core.Planning;
using MovieSift.Infrastructure.Files;
using MovieSift.Infrastructure.Persistence;
using Xunit;

namespace MovieSift.Infrastructure.Tests;

/// <summary>Exercises real partial reservation resources with a controlled backend that must never start.</summary>
public sealed class ReservationSafetyTests
{
    public static bool IsLinux => OperatingSystem.IsLinux();

    /// <summary>A journal publication failure rolls back only the adjacent directory created by this reservation.</summary>
    [Fact]
    public async Task JournalDirectoryCollisionDoesNotLeakUnreportedPrivateStorage()
    {
        using var workspace = new TestWorkspace();
        var fixture = await CreateFixtureAsync(workspace.Path);
        var journalPath = Path.Combine(fixture.Data, "ownership", $"{fixture.Plan.Id:N}.json");
        Directory.CreateDirectory(journalPath);
        var sentinel = Path.Combine(journalPath, "foreign.txt");
        await File.WriteAllTextAsync(sentinel, "foreign journal directory", TestContext.Current.CancellationToken);
        var store = new OwnedOutputStore(fixture.Data);
        var result = await new JobCoordinator(fixture.Backend, fixture.Access, store).RunAsync(new(fixture.Plan));

        Assert.Equal(JobPhase.Failed, result.Phase);
        Assert.Equal(ErrorCode.PersistenceUnavailable, result.Failure?.Code);
        Assert.Equal(0, fixture.Backend.RemuxCalls);
        Assert.Empty(result.CleanupIssues);
        Assert.False(Directory.Exists(fixture.PrivateDirectory));
        Assert.False(File.Exists(fixture.Plan.DestinationPath));
        Assert.Equal("original source", await File.ReadAllTextAsync(fixture.Plan.Inspection.Source.FullPath,
            TestContext.Current.CancellationToken));
        Assert.Equal("foreign journal directory", await File.ReadAllTextAsync(sentinel, TestContext.Current.CancellationToken));
    }

    /// <summary>Every pre-handoff boundary rolls back on failure or cancellation without launching a worker.</summary>
    [Theory]
    [InlineData(nameof(ReservationBoundary.DirectoryCreated), false)]
    [InlineData(nameof(ReservationBoundary.MarkerCreated), false)]
    [InlineData(nameof(ReservationBoundary.MarkerWritten), false)]
    [InlineData(nameof(ReservationBoundary.JournalPublished), false)]
    [InlineData(nameof(ReservationBoundary.DirectoryCreated), true)]
    [InlineData(nameof(ReservationBoundary.MarkerCreated), true)]
    [InlineData(nameof(ReservationBoundary.MarkerWritten), true)]
    [InlineData(nameof(ReservationBoundary.JournalPublished), true)]
    public async Task FailureOrCancellationBeforeHandoffRollsBackOwnedResources(string boundaryName, bool cancel)
    {
        using var workspace = new TestWorkspace();
        using var cancellation = new CancellationTokenSource();
        var fixture = await CreateFixtureAsync(workspace.Path);
        var boundary = Enum.Parse<ReservationBoundary>(boundaryName);
        var store = new OwnedOutputStore(fixture.Data, (current, _) =>
        {
            if (current != boundary)
            {
                return;
            }

            if (cancel)
            {
                cancellation.Cancel();
                return;
            }

            throw new IOException("controlled reservation failure");
        });
        var result = await new JobCoordinator(fixture.Backend, fixture.Access, store)
            .RunAsync(new(fixture.Plan, CancellationToken: cancellation.Token));

        Assert.Equal(cancel ? JobPhase.Cancelled : JobPhase.Failed, result.Phase);
        Assert.Equal(cancel ? ErrorCode.Cancelled : ErrorCode.PersistenceUnavailable, result.Failure?.Code);
        Assert.Equal(cancel, result.CancellationRequested);
        Assert.Empty(result.CleanupIssues);
        Assert.False(Directory.Exists(fixture.PrivateDirectory));
        Assert.Empty(await store.FindInterruptedAsync(TestContext.Current.CancellationToken));
        await AssertNoMediaEffectsAsync(fixture);
    }

    /// <summary>An unexpected pre-handoff exception still rolls back owned storage and preserves its primary diagnostic.</summary>
    [Fact]
    public async Task UnexpectedFailureBeforeHandoffStillRollsBack()
    {
        using var workspace = new TestWorkspace();
        var fixture = await CreateFixtureAsync(workspace.Path);
        var store = new OwnedOutputStore(fixture.Data, (boundary, _) =>
        {
            if (boundary == ReservationBoundary.MarkerWritten)
            {
                throw new InvalidOperationException("unexpected primary failure");
            }
        });
        var result = await new JobCoordinator(fixture.Backend, fixture.Access, store).RunAsync(new(fixture.Plan));

        Assert.Equal(ErrorCode.PersistenceUnavailable, result.Failure?.Code);
        Assert.Equal("unexpected primary failure", result.Failure?.Detail);
        Assert.Empty(result.CleanupIssues);
        Assert.False(Directory.Exists(fixture.PrivateDirectory));
        await AssertNoMediaEffectsAsync(fixture);
    }

    /// <summary>An existing journal is never overwritten by an initial reservation, even when its filename matches.</summary>
    [Fact]
    public async Task PreexistingJournalFileIsPreserved()
    {
        using var workspace = new TestWorkspace();
        var fixture = await CreateFixtureAsync(workspace.Path);
        var records = Path.Combine(fixture.Data, "ownership");
        Directory.CreateDirectory(records);
        var journal = Path.Combine(records, $"{fixture.Plan.Id:N}.json");
        await File.WriteAllTextAsync(journal, "foreign journal", TestContext.Current.CancellationToken);
        var store = new OwnedOutputStore(fixture.Data);
        var result = await new JobCoordinator(fixture.Backend, fixture.Access, store).RunAsync(new(fixture.Plan));

        Assert.Equal(ErrorCode.PersistenceUnavailable, result.Failure?.Code);
        Assert.Empty(result.CleanupIssues);
        Assert.False(Directory.Exists(fixture.PrivateDirectory));
        Assert.Equal("foreign journal", await File.ReadAllTextAsync(journal, TestContext.Current.CancellationToken));
        await AssertNoMediaEffectsAsync(fixture);
    }

    /// <summary>Pre-existing private directories cannot be adopted or cleaned as this operation's output.</summary>
    [Fact]
    public async Task PreexistingPrivateDirectoryIsPreserved()
    {
        using var workspace = new TestWorkspace();
        var fixture = await CreateFixtureAsync(workspace.Path);
        Directory.CreateDirectory(fixture.PrivateDirectory);
        var foreign = Path.Combine(fixture.PrivateDirectory, "foreign.txt");
        await File.WriteAllTextAsync(foreign, "foreign directory", TestContext.Current.CancellationToken);
        var result = await new JobCoordinator(fixture.Backend, fixture.Access, new OwnedOutputStore(fixture.Data))
            .RunAsync(new(fixture.Plan));

        Assert.Equal(ErrorCode.OwnershipInvalid, result.Failure?.Code);
        Assert.Empty(result.CleanupIssues);
        Assert.Equal("foreign directory", await File.ReadAllTextAsync(foreign, TestContext.Current.CancellationToken));
        await AssertNoMediaEffectsAsync(fixture);
    }

    /// <summary>A foreign marker arriving after directory creation remains untouched and makes rollback uncertainty visible.</summary>
    [Fact]
    public async Task MarkerCreationCollisionPreservesForeignMarkerAndReportsCleanup()
    {
        using var workspace = new TestWorkspace();
        var fixture = await CreateFixtureAsync(workspace.Path);
        var store = new OwnedOutputStore(fixture.Data, (boundary, output) =>
        {
            if (boundary == ReservationBoundary.DirectoryCreated)
            {
                File.WriteAllText(Path.Combine(output.DirectoryPath, "owner.json"), "foreign marker");
            }
        });
        var result = await new JobCoordinator(fixture.Backend, fixture.Access, store).RunAsync(new(fixture.Plan));

        Assert.Equal(ErrorCode.PersistenceUnavailable, result.Failure?.Code);
        Assert.Single(result.CleanupIssues);
        Assert.Equal("foreign marker", await File.ReadAllTextAsync(Path.Combine(fixture.PrivateDirectory, "owner.json"),
            TestContext.Current.CancellationToken));
        await AssertNoMediaEffectsAsync(fixture);
    }

    /// <summary>Directory substitution cannot redirect rollback or erase the directory whose identity was retained.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementDirectoryIsPreservedAndPrimaryFailureSurvives(bool cancel)
    {
        using var workspace = new TestWorkspace();
        using var cancellation = new CancellationTokenSource();
        var fixture = await CreateFixtureAsync(workspace.Path);
        var retained = fixture.PrivateDirectory + ".retained";
        var store = new OwnedOutputStore(fixture.Data, (boundary, output) =>
        {
            if (boundary != ReservationBoundary.MarkerWritten)
            {
                return;
            }

            Directory.Move(output.DirectoryPath, retained);
            Directory.CreateDirectory(output.DirectoryPath);
            File.WriteAllText(Path.Combine(output.DirectoryPath, "foreign.txt"), "foreign replacement");

            if (cancel)
            {
                cancellation.Cancel();
                return;
            }

            throw new IOException("primary reservation failure");
        });
        var result = await new JobCoordinator(fixture.Backend, fixture.Access, store)
            .RunAsync(new(fixture.Plan, CancellationToken: cancellation.Token));

        Assert.Equal(cancel ? ErrorCode.Cancelled : ErrorCode.PersistenceUnavailable, result.Failure?.Code);
        Assert.Equal(cancel ? JobPhase.Cancelled : JobPhase.Failed, result.Phase);
        Assert.Equal(cancel, result.CancellationRequested);
        Assert.Single(result.CleanupIssues);
        Assert.True(File.Exists(Path.Combine(retained, "owner.json")));
        Assert.Equal("foreign replacement", await File.ReadAllTextAsync(Path.Combine(fixture.PrivateDirectory, "foreign.txt"),
            TestContext.Current.CancellationToken));
        await AssertNoMediaEffectsAsync(fixture);
    }

    /// <summary>Incomplete rollback preserves the durable journal and valid marker for ordinary interruption discovery.</summary>
    [Fact]
    public async Task JournalBoundaryRollbackFailureRetainsDiscoverableOwnership()
    {
        using var workspace = new TestWorkspace();
        var fixture = await CreateFixtureAsync(workspace.Path);
        var store = new OwnedOutputStore(fixture.Data, (boundary, output) =>
        {
            if (boundary != ReservationBoundary.JournalPublished)
            {
                return;
            }

            File.WriteAllText(Path.Combine(output.DirectoryPath, "unexpected.txt"), "preserve this evidence");
            throw new IOException("primary reservation failure");
        });
        var result = await new JobCoordinator(fixture.Backend, fixture.Access, store).RunAsync(new(fixture.Plan));

        Assert.Equal("primary reservation failure", result.Failure?.Detail);
        Assert.Single(result.CleanupIssues);
        var interrupted = Assert.Single(await store.FindInterruptedAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(interrupted.Output);
        Assert.Equal(fixture.Plan.Id, interrupted.Output.JobId);
        Assert.False(interrupted.CanClean);
        Assert.True(File.Exists(Path.Combine(fixture.PrivateDirectory, "owner.json")));
        await AssertNoMediaEffectsAsync(fixture);
    }

    /// <summary>A modified marker cannot grant permission to delete a no-longer-matching evidence path.</summary>
    [Fact]
    public async Task ChangedMarkerIsPreservedWithSeparateCleanupIssue()
    {
        using var workspace = new TestWorkspace();
        var fixture = await CreateFixtureAsync(workspace.Path);
        var store = new OwnedOutputStore(fixture.Data, (boundary, output) =>
        {
            if (boundary != ReservationBoundary.MarkerWritten)
            {
                return;
            }

            File.WriteAllText(Path.Combine(output.DirectoryPath, "owner.json"), "changed foreign marker");
            throw new IOException("primary reservation failure");
        });
        var result = await new JobCoordinator(fixture.Backend, fixture.Access, store).RunAsync(new(fixture.Plan));

        Assert.Equal("primary reservation failure", result.Failure?.Detail);
        Assert.Single(result.CleanupIssues);
        Assert.Equal("changed foreign marker", await File.ReadAllTextAsync(Path.Combine(fixture.PrivateDirectory, "owner.json"),
            TestContext.Current.CancellationToken));
        await AssertNoMediaEffectsAsync(fixture);
    }

    /// <summary>A foreign journal replacement is preserved even after the operation's private directory can be removed.</summary>
    [Fact]
    public async Task ChangedJournalIsPreservedWithSeparateCleanupIssue()
    {
        using var workspace = new TestWorkspace();
        var fixture = await CreateFixtureAsync(workspace.Path);
        var store = new OwnedOutputStore(fixture.Data, (boundary, output) =>
        {
            if (boundary != ReservationBoundary.JournalPublished)
            {
                return;
            }

            File.WriteAllText(Path.Combine(fixture.Data, "ownership", $"{output.JobId:N}.json"), "changed foreign journal");
            throw new IOException("primary reservation failure");
        });
        var result = await new JobCoordinator(fixture.Backend, fixture.Access, store).RunAsync(new(fixture.Plan));

        Assert.Equal("primary reservation failure", result.Failure?.Detail);
        Assert.Single(result.CleanupIssues);
        Assert.False(Directory.Exists(fixture.PrivateDirectory));
        Assert.Equal("changed foreign journal", await File.ReadAllTextAsync(
            Path.Combine(fixture.Data, "ownership", $"{fixture.Plan.Id:N}.json"), TestContext.Current.CancellationToken));
        await AssertNoMediaEffectsAsync(fixture);
    }

    /// <summary>A redirected directory is never followed during partial rollback.</summary>
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Directory symlink creation is unprivileged on Linux.")]
    public async Task RedirectedDirectoryDuringRollbackIsPreserved()
    {
        using var workspace = new TestWorkspace();
        var fixture = await CreateFixtureAsync(workspace.Path);
        var foreign = Path.Combine(workspace.Path, "foreign target");
        Directory.CreateDirectory(foreign);
        var sentinel = Path.Combine(foreign, "foreign.txt");
        await File.WriteAllTextAsync(sentinel, "foreign target", TestContext.Current.CancellationToken);
        var store = new OwnedOutputStore(fixture.Data, (boundary, output) =>
        {
            if (boundary != ReservationBoundary.DirectoryCreated)
            {
                return;
            }

            Directory.Move(output.DirectoryPath, output.DirectoryPath + ".retained");
            Directory.CreateSymbolicLink(output.DirectoryPath, foreign);
            throw new IOException("primary reservation failure");
        });
        var result = await new JobCoordinator(fixture.Backend, fixture.Access, store).RunAsync(new(fixture.Plan));

        Assert.Equal("primary reservation failure", result.Failure?.Detail);
        Assert.Single(result.CleanupIssues);
        Assert.Equal("foreign target", await File.ReadAllTextAsync(sentinel, TestContext.Current.CancellationToken));
        Assert.True(File.GetAttributes(fixture.PrivateDirectory).HasFlag(FileAttributes.ReparsePoint));
        await AssertNoMediaEffectsAsync(fixture);
    }

    /// <summary>Failed initial identity capture preserves the created file and reports uncertainty without masking its cause.</summary>
    [Fact]
    public async Task FailedTemporaryIdentityCaptureReportsPreservedFile()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Path, "record.json");
        await File.WriteAllTextAsync(path, "previous record", TestContext.Current.CancellationToken);
        var primary = new IOException("primary identity capture failure");
        var moveCalls = 0;
        var deleteCalls = 0;
        JsonFileActions actions = new((_, _, _) => moveCalls++, _ => deleteCalls++, _ => throw primary);
        var failure = await Assert.ThrowsAsync<MovieSift.Application.OperationFailureException>(() =>
            BoundedJson.WriteAsync(new JsonWriteRequest<object>(path, new { Value = "new record" }, FileActions: actions)));

        Assert.Equal("primary identity capture failure", failure.Failure.Detail);
        Assert.Same(primary, failure.InnerException);
        var issue = Assert.Single(failure.CleanupIssues);
        Assert.Contains("identity", issue.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, moveCalls);
        Assert.Equal(0, deleteCalls);
        var temporary = Assert.Single(Directory.GetFiles(workspace.Path, "*.tmp"));
        Assert.Equal(temporary, issue.Path);
        Assert.Equal("previous record", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    /// <summary>A temporary-record deletion fault cannot replace the original publication failure or previous record.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TemporaryCleanupFailurePreservesPrimaryFailureAndPreviousRecord(bool unexpectedCleanupFailure)
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Path, "record.json");
        await File.WriteAllTextAsync(path, "previous record", TestContext.Current.CancellationToken);
        var primary = new IOException("primary move failure");
        Exception secondary = unexpectedCleanupFailure
            ? new InvalidOperationException("secondary cleanup failure") : new IOException("secondary cleanup failure");
        JsonFileActions actions = new((_, _, _) => throw primary, _ => throw secondary);
        var failure = await Assert.ThrowsAsync<MovieSift.Application.OperationFailureException>(() =>
            BoundedJson.WriteAsync(new JsonWriteRequest<object>(path, new { Value = "new record" }, FileActions: actions)));

        Assert.Equal("primary move failure", failure.Failure.Detail);
        Assert.Same(primary, failure.InnerException);
        Assert.Equal("secondary cleanup failure", Assert.Single(failure.CleanupIssues).Detail);
        Assert.Equal("previous record", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(workspace.Path, "*.tmp"));
    }

    /// <summary>A temporary path replaced by another file is reported and preserved rather than deleted by its old name.</summary>
    [Fact]
    public async Task ReplacedTemporaryRecordIsPreserved()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Path, "record.json");
        string? temporary = null;
        var deleteCalls = 0;
        JsonFileActions actions = new((source, _, _) =>
        {
            temporary = source;
            File.WriteAllText(source, "foreign replacement with different bytes");
            throw new IOException("primary move failure");
        }, _ => deleteCalls++);
        var failure = await Assert.ThrowsAsync<MovieSift.Application.OperationFailureException>(() =>
            BoundedJson.WriteAsync(new JsonWriteRequest<object>(path, new { Value = "new record" }, FileActions: actions)));

        Assert.Equal("primary move failure", failure.Failure.Detail);
        Assert.Single(failure.CleanupIssues);
        Assert.Equal(0, deleteCalls);
        Assert.NotNull(temporary);
        Assert.Equal("foreign replacement with different bytes", await File.ReadAllTextAsync(temporary,
            TestContext.Current.CancellationToken));
        Assert.False(File.Exists(path));
    }

    /// <summary>Successful replacement relinquishes the temporary name and never deletes a later foreign file there.</summary>
    [Fact]
    public async Task CommittedRecordDoesNotCleanAForeignFileAtTheOldTemporaryName()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Path, "record.json");
        string? temporary = null;
        var deleteCalls = 0;
        JsonFileActions actions = new((source, destination, overwrite) =>
        {
            File.Move(source, destination, overwrite);
            temporary = source;
            File.WriteAllText(source, "foreign later file");
        }, _ => deleteCalls++);
        var revision = await BoundedJson.WriteAsync(new JsonWriteRequest<object>(path, new { Value = "new record" }, FileActions: actions));

        Assert.Equal(path, revision.FullPath);
        Assert.Equal(0, deleteCalls);
        Assert.NotNull(temporary);
        Assert.Equal("foreign later file", await File.ReadAllTextAsync(temporary, TestContext.Current.CancellationToken));
        Assert.Contains("new record", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    /// <summary>Checks source/output immutability and the absence of any attempted media execution.</summary>
    private static async Task AssertNoMediaEffectsAsync(ReservationFixture fixture)
    {
        Assert.Equal(0, fixture.Backend.RemuxCalls);
        Assert.False(File.Exists(fixture.Plan.DestinationPath));
        Assert.Equal("original source", await File.ReadAllTextAsync(fixture.Plan.Inspection.Source.FullPath,
            TestContext.Current.CancellationToken));
    }

    /// <summary>Creates a changed immutable plan from small ordinary bytes, without claiming media preservation.</summary>
    private static async Task<ReservationFixture> CreateFixtureAsync(string root)
    {
        var source = Path.Combine(root, "source.mkv");
        await File.WriteAllTextAsync(source, "original source", TestContext.Current.CancellationToken);
        var access = new FileSourceAccess();
        var revision = await access.ReadRevisionAsync(new(source));
        MediaInspection inspection = new(revision, new("controlled", "test", 20),
            [Track(0, TrackKind.Video), Track(1, TrackKind.Audio), Track(2, TrackKind.Subtitles)], [], "", false, false);
        var plan = PlanBuilder.Build(new(inspection, [0, 1], Path.Combine(root, "output.trimmed.mkv"), false)).Plan!;
        return new(plan, Path.Combine(root, "data"), Path.Combine(root, $".moviesift-{plan.Id:N}"), access, new(inspection));
    }

    /// <summary>Supplies only the metadata needed for this controlled ownership test.</summary>
    private static MediaTrack Track(int identifier, TrackKind kind) => new(identifier, (ulong)identifier + 1,
        kind, "controlled", "eng", "en", "", null, false, false, true, ImmutableDictionary<string, string>.Empty);

    /// <summary>Counts any incorrect attempt to cross the undurable reservation boundary.</summary>
    private sealed class UnstartedBackend(MediaInspection inspection) : IMediaBackend
    {
        public int RemuxCalls { get; private set; }

        /// <summary>Returns the explicit controlled description.</summary>
        public Task<MediaInspection> InspectAsync(InspectRequest request) => Task.FromResult(inspection);

        /// <summary>Fails if reservation incorrectly hands an incomplete resource to the media stage.</summary>
        public Task<ToolOutcome> RemuxAsync(RemuxRequest request)
        {
            RemuxCalls++;
            throw new InvalidOperationException("Media work must not start.");
        }

        /// <summary>Disallows verification before durable reservation.</summary>
        public Task<VerificationOutcome> VerifyAsync(VerifyRequest request) =>
            throw new InvalidOperationException("Verification must not start.");
    }

    private sealed record ReservationFixture(RemuxPlan Plan, string Data, string PrivateDirectory,
        FileSourceAccess Access, UnstartedBackend Backend);
}
