using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using MovieSift.Application;
using MovieSift.Application.Ports;
using MovieSift.Core.Failures;
using MovieSift.Core.Media;
using MovieSift.Core.Planning;
using MovieSift.Infrastructure.Files;
using Xunit;

namespace MovieSift.Infrastructure.Tests;

/// <summary>Checks the one app-local input resource without granting it independent publication or process authority.</summary>
public sealed class InputProjectionOwnershipTests
{
    public static bool IsLinux => OperatingSystem.IsLinux();

    /// <summary>Content may grow while its original created-file identity and original-media bytes remain unchanged.</summary>
    [Fact]
    public async Task ReservedProjectionUsesOneJournalAndCleansAfterWriting()
    {
        using var workspace = new TestWorkspace();
        var fixture = await CreateAsync(workspace);
        var output = await fixture.Store.ReserveAsync(new(fixture.Plan, RequiresInputProjection: true));
        var projection = Assert.IsType<OwnedInputProjection>(output.InputProjection);
        Assert.Equal(Path.Combine(fixture.Data, "ownership", $"{output.JobId:N}.input.mkv"), projection.CreatedFile.FullPath);
        Assert.Equal(fixture.Plan.Inspection.Source, projection.OriginalSource);
        Assert.NotEqual(projection.OriginalSource.FileIdentity, projection.CreatedFile.FileIdentity);

        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(projection.CreatedFile.FullPath));
        }

        Assert.Equal(4, (await ReadJournalAsync(fixture, output))["Version"]!.GetValue<int>());
        await using (var stream = OwnedInputProjectionAccess.OpenReadWrite(projection))
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes("owned projected input"), TestContext.Current.CancellationToken);
        }
        await fixture.Store.ValidateOwnershipAsync(new(output));
        Assert.Empty(await fixture.Store.CleanupAsync(new(output)));
        Assert.False(File.Exists(projection.CreatedFile.FullPath));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(fixture.Data, "ownership")));
        Assert.False(Directory.Exists(output.DirectoryPath));
        Assert.Equal("original media", await File.ReadAllTextAsync(fixture.Plan.Inspection.Source.FullPath, TestContext.Current.CancellationToken));
    }

    /// <summary>Projection creation participates in the existing pre-handoff rollback for faults and cancellation.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreHandoffFailureRollsBackProjection(bool cancel)
    {
        using var workspace = new TestWorkspace();
        using var cancellation = new CancellationTokenSource();
        var fixture = await CreateAsync(workspace);
        var store = new OwnedOutputStore(fixture.Data, (boundary, _) =>
        {
            if (boundary != ReservationBoundary.ProjectionCreated)
            {
                return;
            }

            if (cancel)
            {
                cancellation.Cancel();
                return;
            }

            throw new IOException("controlled projection reservation failure");
        });
        var exception = await Record.ExceptionAsync(() => store.ReserveAsync(new(fixture.Plan, cancellation.Token, true)));
        Assert.NotNull(exception);
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(fixture.Data, "ownership")));
        Assert.False(Directory.Exists(Path.Combine(workspace.Path, $".moviesift-{fixture.Plan.Id:N}")));
        Assert.False(File.Exists(fixture.Plan.DestinationPath));
    }

    /// <summary>A substituted file never inherits the created projection's deletion authority.</summary>
    [Fact]
    public async Task ReplacedProjectionIsPreserved()
    {
        using var workspace = new TestWorkspace();
        var fixture = await CreateAsync(workspace);
        var output = await fixture.Store.ReserveAsync(new(fixture.Plan, RequiresInputProjection: true));
        var path = output.InputProjection!.CreatedFile.FullPath;
        File.Move(path, path + ".retained");
        await File.WriteAllTextAsync(path, "foreign replacement", TestContext.Current.CancellationToken);
        Assert.NotEmpty(await fixture.Store.CleanupAsync(new(output)));
        Assert.Equal("foreign replacement", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(JournalPath(fixture, output)));
    }

    /// <summary>An existing app-local filename is not adopted, overwritten or deleted during failed reservation.</summary>
    [Fact]
    public async Task ExistingProjectionFilenameRemainsUntouched()
    {
        using var workspace = new TestWorkspace();
        var fixture = await CreateAsync(workspace);
        var records = Path.Combine(fixture.Data, "ownership");
        Directory.CreateDirectory(records);
        var path = Path.Combine(records, $"{fixture.Plan.Id:N}.input.mkv");
        await File.WriteAllTextAsync(path, "foreign sentinel", TestContext.Current.CancellationToken);
        var failure = await Assert.ThrowsAsync<OperationFailureException>(() => fixture.Store.ReserveAsync(new(fixture.Plan, RequiresInputProjection: true)));
        Assert.Empty(failure.CleanupIssues);
        Assert.Equal("foreign sentinel", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(workspace.Path, $".moviesift-{fixture.Plan.Id:N}")));
    }

    /// <summary>A projection redirected to the original can authorize neither writes nor cleanup.</summary>
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Unprivileged file symlinks are available on Linux.")]
    public async Task RedirectedProjectionNeverTouchesTheOriginal()
    {
        using var workspace = new TestWorkspace();
        var fixture = await CreateAsync(workspace);
        var output = await fixture.Store.ReserveAsync(new(fixture.Plan, RequiresInputProjection: true));
        var projection = output.InputProjection!;
        File.Move(projection.CreatedFile.FullPath, projection.CreatedFile.FullPath + ".retained");
        File.CreateSymbolicLink(projection.CreatedFile.FullPath, projection.OriginalSource.FullPath);
        Assert.Throws<OperationFailureException>(() => OwnedInputProjectionAccess.OpenReadWrite(projection));
        Assert.NotEmpty(await fixture.Store.CleanupAsync(new(output)));
        Assert.Equal("original media", await File.ReadAllTextAsync(projection.OriginalSource.FullPath, TestContext.Current.CancellationToken));
    }

    /// <summary>Additional hardlinks cannot redirect input writes or be removed as a uniquely owned resource.</summary>
    [Fact]
    public async Task HardlinkedProjectionCannotBeWrittenOrCleaned()
    {
        using var workspace = new TestWorkspace();
        var fixture = await CreateAsync(workspace);
        var output = await fixture.Store.ReserveAsync(new(fixture.Plan, RequiresInputProjection: true));
        var projection = output.InputProjection!;
        var alias = Path.Combine(workspace.Path, "foreign-alias.mkv");

        if (OperatingSystem.IsWindows())
        {
            Assert.True(CreateHardLink(alias, projection.CreatedFile.FullPath, IntPtr.Zero));
        }

        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(0, Link(projection.CreatedFile.FullPath, alias));
        }

        Assert.Throws<OperationFailureException>(() => OwnedInputProjectionAccess.OpenReadWrite(projection));
        Assert.NotEmpty(await fixture.Store.CleanupAsync(new(output)));
        Assert.True(File.Exists(alias));
        Assert.True(File.Exists(projection.CreatedFile.FullPath));
    }

    /// <summary>An old GUI's death does not prove its native projection worker has stopped.</summary>
    [Fact]
    public async Task InterruptedProjectionBlocksAutomaticCleanup()
    {
        using var workspace = new TestWorkspace();
        var fixture = await CreateAsync(workspace);
        var output = await fixture.Store.ReserveAsync(new(fixture.Plan, RequiresInputProjection: true));
        var journal = await ReadJournalAsync(fixture, output);
        journal["OwnerProcessId"] = int.MaxValue;
        await File.WriteAllTextAsync(JournalPath(fixture, output), journal.ToJsonString(), TestContext.Current.CancellationToken);
        var interrupted = Assert.Single(await fixture.Store.FindInterruptedAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(interrupted.Output);
        Assert.False(interrupted.CanClean);
        Assert.NotEmpty(await fixture.Store.CleanupAsync(new(output)));
        Assert.True(File.Exists(output.InputProjection!.CreatedFile.FullPath));
        Assert.True(File.Exists(JournalPath(fixture, output)));
    }

    /// <summary>Projection authority is version-specific and cannot be attached to a legacy or unknown record.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(99)]
    public async Task WrongRecordVersionCannotAuthorizeProjection(int version)
    {
        using var workspace = new TestWorkspace();
        var fixture = await CreateAsync(workspace);
        var output = await fixture.Store.ReserveAsync(new(fixture.Plan, RequiresInputProjection: true));
        var journal = await ReadJournalAsync(fixture, output);
        journal["Version"] = version;
        await File.WriteAllTextAsync(JournalPath(fixture, output), journal.ToJsonString(), TestContext.Current.CancellationToken);
        Assert.Null(Assert.Single(await fixture.Store.FindInterruptedAsync(TestContext.Current.CancellationToken)).Output);
        Assert.NotEmpty(await fixture.Store.CleanupAsync(new(output)));
        Assert.True(File.Exists(output.InputProjection!.CreatedFile.FullPath));
    }

    /// <summary>Ordinary operations retain version three and do not allocate a projected media file.</summary>
    [Fact]
    public async Task OrdinaryReservationRemainsVersionThree()
    {
        using var workspace = new TestWorkspace();
        var fixture = await CreateAsync(workspace);
        var output = await fixture.Store.ReserveAsync(new(fixture.Plan));
        Assert.Null(output.InputProjection);
        Assert.Equal(3, (await ReadJournalAsync(fixture, output))["Version"]!.GetValue<int>());
        Assert.Empty(await fixture.Store.CleanupAsync(new(output)));
    }

    /// <summary>Impossible additional capacity is rejected with rollback rather than overflowing its allocation budget.</summary>
    [Fact]
    public async Task ProjectionAllocationOverflowFailsWithoutLeakingResources()
    {
        using var workspace = new TestWorkspace();
        var fixture = await CreateAsync(workspace);
        var plan = fixture.Plan with { Inspection = fixture.Plan.Inspection with { Source = fixture.Plan.Inspection.Source with { Length = long.MaxValue } } };
        var failure = await Assert.ThrowsAsync<OperationFailureException>(() => fixture.Store.ReserveAsync(new(plan, RequiresInputProjection: true)));
        Assert.Equal(ErrorCode.InsufficientSpace, failure.Failure.Code);
        Assert.Empty(failure.CleanupIssues);
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(fixture.Data, "ownership")));
    }

    /// <summary>Builds actual local files with controlled inspection data; no backend processes are started.</summary>
    private static async Task<ProjectionFixture> CreateAsync(TestWorkspace workspace)
    {
        var source = Path.Combine(workspace.Path, "original.mkv");
        await File.WriteAllTextAsync(source, "original media", TestContext.Current.CancellationToken);
        var revision = await new FileSourceAccess().ReadRevisionAsync(new(source));
        MediaInspection inspection = new(revision, new("controlled", "test", 20),
            [Track(0, TrackKind.Video), Track(1, TrackKind.Audio), Track(2, TrackKind.Subtitles)], [], "", false, false);
        var plan = PlanBuilder.Build(new(inspection, [0, 1], Path.Combine(workspace.Path, "copy.trimmed.mkv"), false)).Plan!;
        var data = Path.Combine(workspace.Path, "data");
        return new(plan, data, new(data));
    }

    /// <summary>Supplies only the immutable track metadata required to create an approved controlled plan.</summary>
    private static MediaTrack Track(int identifier, TrackKind kind) => new(identifier, (ulong)identifier + 1,
        kind, "controlled", "eng", "en", "", null, false, false, true, ImmutableDictionary<string, string>.Empty);

    /// <summary>Reads the actual bounded record representation for version and previous-owner controls.</summary>
    private static async Task<JsonObject> ReadJournalAsync(ProjectionFixture fixture, OwnedOutput output) =>
        JsonNode.Parse(await File.ReadAllTextAsync(JournalPath(fixture, output), TestContext.Current.CancellationToken))!.AsObject();

    /// <summary>Derives the existing journal filename without searching unrelated storage.</summary>
    private static string JournalPath(ProjectionFixture fixture, OwnedOutput output) => Path.Combine(fixture.Data, "ownership", $"{output.JobId:N}.json");

    private sealed record ProjectionFixture(RemuxPlan Plan, string Data, OwnedOutputStore Store);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string existingPath, string newPath);
}
