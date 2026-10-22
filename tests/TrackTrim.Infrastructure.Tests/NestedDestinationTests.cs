using System.Collections.Immutable;
using System.Runtime.InteropServices;
using TrackTrim.Application;
using TrackTrim.Application.Jobs;
using TrackTrim.Application.Ports;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Media;
using TrackTrim.Core.Planning;
using TrackTrim.Infrastructure.Files;
using Xunit;

namespace TrackTrim.Infrastructure.Tests;

/// <summary>Checks real directory preparation separately from controlled media lifecycle failures.</summary>
public sealed class NestedDestinationTests
{
    private const uint OwnerReadWritePermissions = 0x180;
    private static readonly TimeSpan FileTimeout = TimeSpan.FromSeconds(5);
    public static bool IsLinux => OperatingSystem.IsLinux();

    /// <summary>Review validates absent nested parents without writing; preparation creates only those parents.</summary>
    [Fact]
    public async Task ValidationDoesNotCreateDirectoriesButPreparationDoes()
    {
        using var workspace = new TestWorkspace();
        var access = new FileSourceAccess();
        var source = await CreateSourceAsync(new(workspace.Path, access));
        var root = Path.Combine(workspace.Path, "Output Кириллица");
        Directory.CreateDirectory(root);
        var parent = Path.Combine(root, "Season 1", "Part 1");
        var destination = Path.Combine(parent, "episode.trimmed.mkv");
        var request = new ValidateDestinationRequest(source, destination, source.Length,
            TestContext.Current.CancellationToken, root);
        await access.ValidateDestinationAsync(request);
        Assert.Empty(Directory.GetFileSystemEntries(root));
        await access.PrepareDestinationAsync(request);
        Assert.True(Directory.Exists(parent));
        Assert.Empty(Directory.GetFileSystemEntries(parent));
        Assert.False(File.Exists(destination));
        Assert.Equal("original source", await File.ReadAllTextAsync(source.FullPath, TestContext.Current.CancellationToken));
    }

    /// <summary>Single-file callers retain the existing contract that their chosen parent already exists.</summary>
    [Fact]
    public async Task MissingParentWithoutExplicitRootIsStillRejected()
    {
        using var workspace = new TestWorkspace();
        var access = new FileSourceAccess();
        var source = await CreateSourceAsync(new(workspace.Path, access));
        var parent = Path.Combine(workspace.Path, "missing");
        var failure = await Assert.ThrowsAsync<OperationFailureException>(() => access.ValidateDestinationAsync(new(
            source, Path.Combine(parent, "output.mkv"), source.Length, TestContext.Current.CancellationToken)));
        Assert.Equal(ErrorCode.InvalidDestination, failure.Failure.Code);
        Assert.False(Directory.Exists(parent));
    }

    /// <summary>A missing root, a sibling-prefix escape or a regular-file ancestor cannot authorize new parents.</summary>
    [Theory]
    [InlineData("missing-root")]
    [InlineData("sibling")]
    [InlineData("file-parent")]
    public async Task InvalidOutputRootOrParentCannotCreateDirectories(string scenario)
    {
        using var workspace = new TestWorkspace();
        var access = new FileSourceAccess();
        var source = await CreateSourceAsync(new(workspace.Path, access));
        var root = Path.Combine(workspace.Path, "Output");
        var parent = Path.Combine(root, "Season 1");

        if (scenario != "missing-root")
        {
            Directory.CreateDirectory(root);
        }

        if (scenario == "sibling")
        {
            parent = root + " sibling";
        }

        if (scenario == "file-parent")
        {
            await File.WriteAllTextAsync(parent, "preserved ancestor", TestContext.Current.CancellationToken);
            parent = Path.Combine(parent, "child");
        }

        var failure = await Assert.ThrowsAsync<OperationFailureException>(() => access.PrepareDestinationAsync(new(
            source, Path.Combine(parent, "output.mkv"), source.Length, TestContext.Current.CancellationToken, root)));
        Assert.Equal(ErrorCode.InvalidDestination, failure.Failure.Code);
        Assert.False(Directory.Exists(parent));
        Assert.False(Directory.Exists(root + " sibling"));
    }

    /// <summary>A destination created after review remains untouched and blocks preparation.</summary>
    [Fact]
    public async Task DestinationAppearingAfterReviewRemainsUnchanged()
    {
        using var workspace = new TestWorkspace();
        var access = new FileSourceAccess();
        var source = await CreateSourceAsync(new(workspace.Path, access));
        var root = Path.Combine(workspace.Path, "Output");
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, "episode.trimmed.mkv");
        var request = new ValidateDestinationRequest(source, destination, source.Length,
            TestContext.Current.CancellationToken, root);
        await access.ValidateDestinationAsync(request);
        await File.WriteAllTextAsync(destination, "collision sentinel", TestContext.Current.CancellationToken);
        var failure = await Assert.ThrowsAsync<OperationFailureException>(() => access.PrepareDestinationAsync(request));
        Assert.Equal(ErrorCode.DestinationExists, failure.Failure.Code);
        Assert.Equal("collision sentinel", await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken));
        Assert.Equal([destination], Directory.GetFileSystemEntries(root));
    }

    /// <summary>Rejects redirected roots and intermediate ancestors before creating anything through them.</summary>
    [Theory(SkipUnless = nameof(IsLinux), Skip = "The directory-link regression requires Linux; Windows uses the same reparse policy.")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RedirectedRootOrParentDoesNotCreateOutputDirectories(bool redirectRoot)
    {
        using var workspace = new TestWorkspace();
        var access = new FileSourceAccess();
        var source = await CreateSourceAsync(new(workspace.Path, access));
        var root = Path.Combine(workspace.Path, "Output");
        var unrelated = Path.Combine(workspace.Path, "unrelated");
        Directory.CreateDirectory(unrelated);
        var redirected = redirectRoot ? root : Path.Combine(root, "Season 1");

        if (!redirectRoot)
        {
            Directory.CreateDirectory(root);
        }

        Directory.CreateSymbolicLink(redirected, unrelated);
        var destination = Path.Combine(root, "Season 1", "new child", "episode.mkv");
        var failure = await Assert.ThrowsAsync<OperationFailureException>(() => access.PrepareDestinationAsync(new(
            source, destination, source.Length, TestContext.Current.CancellationToken, root)));
        Assert.Equal(ErrorReason.RedirectedPath, failure.Failure.Reason);
        Assert.Empty(Directory.GetFileSystemEntries(unrelated));
    }

    /// <summary>A non-directory special-file ancestor is rejected without opening or waiting on it.</summary>
    [Fact(SkipUnless = nameof(IsLinux), Skip = "The output-parent FIFO regression requires Linux.")]
    public async Task FifoOutputParentDoesNotBlockValidationOrPreparation()
    {
        using var workspace = new TestWorkspace();
        var access = new FileSourceAccess();
        var source = await CreateSourceAsync(new(workspace.Path, access));
        var root = Path.Combine(workspace.Path, "Output");
        Directory.CreateDirectory(root);
        var fifo = Path.Combine(root, "Season 1");
        Assert.Equal(0, MakeFifo(fifo, OwnerReadWritePermissions));
        var request = new ValidateDestinationRequest(source, Path.Combine(fifo, "episode.mkv"), source.Length,
            TestContext.Current.CancellationToken, root);
        var execution = Task.Run(() => access.PrepareDestinationAsync(request), CancellationToken.None);
        var failure = await Assert.ThrowsAsync<OperationFailureException>(() => execution.WaitAsync(FileTimeout, TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCode.InvalidDestination, failure.Failure.Code);
        Assert.True(File.Exists(fifo));
    }

    /// <summary>Cancellation or tool failure removes owned media artifacts but intentionally leaves empty output parents.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledJobLeavesOnlyPreparedEmptyDirectories(bool cancel)
    {
        using var workspace = new TestWorkspace();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var access = new FileSourceAccess();
        var source = await CreateSourceAsync(new(workspace.Path, access));
        var root = Path.Combine(workspace.Path, "Output");
        Directory.CreateDirectory(root);
        var parent = Path.Combine(root, "Season 1", "Part 1");
        var destination = Path.Combine(parent, "episode.trimmed.mkv");
        MediaInspection inspection = new(source, new("controlled", "test", 20),
            [Track(new(0, TrackKind.Video)), Track(new(1, TrackKind.Audio)), Track(new(2, TrackKind.Subtitles))],
            [], "", false, false);
        var backend = new FailingBackend(inspection, cancel ? cancellation : null);
        var data = Path.Combine(workspace.Path, "data");
        var store = new OwnedOutputStore(data);
        var plan = PlanBuilder.Build(new(inspection, [0, 1], destination, false, root)).Plan!;
        var result = await new JobCoordinator(backend, access, store).RunAsync(new(plan, CancellationToken: cancellation.Token));
        Assert.Equal(cancel ? JobPhase.Cancelled : JobPhase.Failed, result.Phase);
        Assert.Equal(cancel ? ErrorCode.Cancelled : ErrorCode.ToolFailed, result.Failure?.Code);
        Assert.Equal(1, backend.RemuxCalls);
        Assert.Empty(result.CleanupIssues);
        Assert.True(Directory.Exists(parent));
        Assert.Empty(Directory.GetFileSystemEntries(parent));
        Assert.Empty(await store.FindInterruptedAsync(TestContext.Current.CancellationToken));
        Assert.False(File.Exists(destination));
        Assert.Equal("original source", await File.ReadAllTextAsync(source.FullPath, TestContext.Current.CancellationToken));
    }

    /// <summary>Creates a small ordinary file for filesystem checks, without claiming it is valid media.</summary>
    private static async Task<SourceRevision> CreateSourceAsync(SourceCreation request)
    {
        var source = Path.Combine(request.Workspace, "source.mkv");
        await File.WriteAllTextAsync(source, "original source", TestContext.Current.CancellationToken);
        return await request.Access.ReadRevisionAsync(new(source, TestContext.Current.CancellationToken));
    }

    /// <summary>Builds controlled track descriptions only for deterministic ownership cleanup tests.</summary>
    private static MediaTrack Track(TrackCreation request) => new(request.Id, (ulong)request.Id + 1,
        request.Kind, "controlled", "eng", "en", "", null, false, false, true, ImmutableDictionary<string, string>.Empty);

    /// <summary>Writes one owned candidate and deterministically fails or cancels without launching a media process.</summary>
    private sealed class FailingBackend(MediaInspection inspection, CancellationTokenSource? cancellation) : IMediaBackend
    {
        public int RemuxCalls { get; private set; }

        /// <summary>Returns the supplied controlled inspection.</summary>
        public Task<MediaInspection> InspectAsync(InspectRequest request) => Task.FromResult(inspection);

        /// <summary>Exercises actual owned-file cleanup after a deterministic worker outcome.</summary>
        public async Task<ToolOutcome> RemuxAsync(RemuxRequest request)
        {
            RemuxCalls++;
            await File.WriteAllTextAsync(request.CandidatePath, "owned candidate", request.CancellationToken);

            if (cancellation is not null)
            {
                await cancellation.CancelAsync();
                request.CancellationToken.ThrowIfCancellationRequested();
            }

            return new(2, "controlled worker failure");
        }

        /// <summary>Fails if a failed or cancelled job incorrectly reaches verification.</summary>
        public Task<VerificationOutcome> VerifyAsync(VerifyRequest request) => throw new InvalidOperationException("Verification must not run.");
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    private sealed record SourceCreation(string Workspace, FileSourceAccess Access);
    private sealed record TrackCreation(int Id, TrackKind Kind);
}
