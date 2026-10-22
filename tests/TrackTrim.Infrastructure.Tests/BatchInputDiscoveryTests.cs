using System.Runtime.InteropServices;
using TrackTrim.Application.Batches;
using TrackTrim.Application.Ports;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Media;
using TrackTrim.Infrastructure.Files;
using Xunit;

namespace TrackTrim.Infrastructure.Tests;

/// <summary>Exercises bounded candidate discovery against actual files, aliases, redirected paths and cancellation.</summary>
public sealed class BatchInputDiscoveryTests
{
    private const uint FifoOwnerReadWritePermissions = 0x180;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);
    public static bool IsLinux => OperatingSystem.IsLinux();

    /// <summary>Includes only top-level MKV extensions in stable order and leaves real media identification for later.</summary>
    [Fact]
    public async Task FolderFindsOnlyImmediateMkvCandidatesInStableOrder()
    {
        using var workspace = new TestWorkspace();
        var first = Path.Combine(workspace.Path, "a Кириллица.mkv");
        var last = Path.Combine(workspace.Path, "z.MKV");
        var nested = Path.Combine(workspace.Path, "nested");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(first, "This is a candidate, not a probed Matroska file.", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(last, "candidate two", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(nested, "inside.mkv"), "must not be traversed", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "ignored.mp4"), "not included", TestContext.Current.CancellationToken);
        var discovery = new FileBatchInputDiscovery(new FileSourceAccess());
        var request = new BatchInputDiscoveryRequest(BatchInputKind.TopLevelFolder, [workspace.Path], TestContext.Current.CancellationToken);
        var initial = await discovery.DiscoverAsync(request);
        var repeated = await discovery.DiscoverAsync(request);
        Assert.Null(initial.Rejection);
        Assert.Empty(initial.Issues);
        Assert.Equal([first, last], initial.Sources.Select(source => source.FullPath));
        Assert.Equal(initial.Sources, repeated.Sources);
        Assert.All(initial.Sources, source => Assert.NotEmpty(source.FileIdentity));
        Assert.Equal("must not be traversed", await File.ReadAllTextAsync(Path.Combine(nested, "inside.mkv"), TestContext.Current.CancellationToken));
    }

    /// <summary>Deduplicates canonical aliases and actual hard-link identities without relying on names or metadata.</summary>
    [Fact]
    public async Task ExplicitFilesDeduplicateCanonicalPathsAndHardLinkIdentities()
    {
        using var workspace = new TestWorkspace();
        var original = Path.Combine(workspace.Path, "z-original.mkv");
        var hardLink = Path.Combine(workspace.Path, "a-hard-link.mkv");
        var distinct = Path.Combine(workspace.Path, "b-distinct.mkv");
        Directory.CreateDirectory(Path.Combine(workspace.Path, "child"));
        await File.WriteAllTextAsync(original, "unchanged original", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(distinct, "distinct identity", TestContext.Current.CancellationToken);

        if (OperatingSystem.IsWindows())
        {
            Assert.True(CreateHardLink(hardLink, original, IntPtr.Zero));
        }

        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(0, Link(original, hardLink));
        }

        var canonicalAlias = Path.Combine(workspace.Path, "child", "..", "z-original.mkv");
        var result = await new FileBatchInputDiscovery(new FileSourceAccess()).DiscoverAsync(new(
            BatchInputKind.ExplicitFiles, [original, canonicalAlias, hardLink, distinct, distinct], TestContext.Current.CancellationToken));
        Assert.Null(result.Rejection);
        Assert.Empty(result.Issues);
        Assert.Equal([hardLink, distinct], result.Sources.Select(source => source.FullPath));
        Assert.Equal(2, result.Sources.Select(source => source.FileIdentity).Distinct().Count());
        Assert.Equal("unchanged original", await File.ReadAllTextAsync(original, TestContext.Current.CancellationToken));
    }

    /// <summary>Returns invalid explicit entries independently while preserving eligible sibling candidates.</summary>
    [Fact]
    public async Task InvalidEntriesDoNotHideValidSiblingsOrBecomeMediaInspection()
    {
        using var workspace = new TestWorkspace();
        var valid = Path.Combine(workspace.Path, "valid.mkv");
        var missing = Path.Combine(workspace.Path, "missing.mkv");
        var otherExtension = Path.Combine(workspace.Path, "not-supported.mp4");
        var directory = Path.Combine(workspace.Path, "folder.mkv");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(valid, "unprobed candidate", TestContext.Current.CancellationToken);
        var result = await new FileBatchInputDiscovery(new FileSourceAccess()).DiscoverAsync(new(
            BatchInputKind.ExplicitFiles, [missing, otherExtension, directory, valid, otherExtension], TestContext.Current.CancellationToken));
        Assert.Null(result.Rejection);
        Assert.Equal(valid, Assert.Single(result.Sources).FullPath);
        Assert.Equal(3, result.Issues.Length);
        Assert.Equal(result.Issues.Select(issue => issue.Path).Order(StringComparer.Ordinal), result.Issues.Select(issue => issue.Path));
        Assert.Contains(result.Issues, issue => issue.Path == otherExtension && issue.Failure.Code == ErrorCode.UnsupportedContainer);
        Assert.Contains(result.Issues, issue => issue.Path == missing && issue.Failure.Code == ErrorCode.InvalidSource);
        Assert.Contains(result.Issues, issue => issue.Path == directory && issue.Failure.Code == ErrorCode.InvalidSource);
    }

    /// <summary>Rejects an oversized addition completely instead of selecting an arbitrary successful prefix.</summary>
    [Theory]
    [InlineData(BatchInputKind.ExplicitFiles, false)]
    [InlineData(BatchInputKind.ExplicitFiles, true)]
    [InlineData(BatchInputKind.TopLevelFolder, false)]
    [InlineData(BatchInputKind.TopLevelFolder, true)]
    public async Task FileLimitAcceptsExactCapacityAndRejectsOverflow(BatchInputKind kind, bool overflow)
    {
        using var workspace = new TestWorkspace();
        var count = BatchDiscoveryLimits.MaximumFiles + (overflow ? 1 : 0);
        var paths = Enumerable.Range(0, count).Select(index => Path.Combine(workspace.Path, $"candidate-{index:D3}.mkv")).ToArray();

        foreach (var path in paths)
        {
            await File.WriteAllTextAsync(path, "candidate", TestContext.Current.CancellationToken);
        }

        var selection = kind == BatchInputKind.TopLevelFolder ? [workspace.Path] : paths;
        var result = await new FileBatchInputDiscovery(new FileSourceAccess()).DiscoverAsync(new(
            kind, selection, TestContext.Current.CancellationToken));

        if (overflow)
        {
            Assert.Equal(ErrorReason.BatchFileLimit, result.Rejection?.Reason);
            Assert.Empty(result.Sources);
            Assert.Empty(result.Issues);
            return;
        }

        Assert.Null(result.Rejection);
        Assert.Equal(BatchDiscoveryLimits.MaximumFiles, result.Sources.Length);
        Assert.Empty(result.Issues);
    }

    /// <summary>Counts invalid rows toward capacity so an invalid-path list cannot bypass the queue bound.</summary>
    [Fact]
    public async Task InvalidEntriesAlsoCountTowardTheAdditionLimit()
    {
        using var workspace = new TestWorkspace();
        var paths = Enumerable.Range(0, BatchDiscoveryLimits.MaximumFiles + 1)
            .Select(index => Path.Combine(workspace.Path, $"missing-{index:D3}.mkv")).ToArray();
        var result = await new FileBatchInputDiscovery(new FileSourceAccess()).DiscoverAsync(new(
            BatchInputKind.ExplicitFiles, paths, TestContext.Current.CancellationToken));
        Assert.Equal(ErrorReason.BatchFileLimit, result.Rejection?.Reason);
        Assert.Empty(result.Sources);
        Assert.Empty(result.Issues);
    }

    /// <summary>Applies the independent entry scan bound before accepting any candidates from a large folder.</summary>
    [Fact]
    public async Task FolderEntryLimitIncludesUnrelatedEntriesAndAcceptsNoPartialSubset()
    {
        using var workspace = new TestWorkspace();

        for (var index = 0; index <= BatchDiscoveryLimits.MaximumExaminedEntries; index++)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using var stream = File.Create(Path.Combine(workspace.Path, $"unrelated-{index:D5}.txt"));
        }

        var access = new RecordingSourceAccess();
        var result = await new FileBatchInputDiscovery(access).DiscoverAsync(new(
            BatchInputKind.TopLevelFolder, [workspace.Path], TestContext.Current.CancellationToken));
        Assert.Equal(ErrorReason.BatchEntryLimit, result.Rejection?.Reason);
        Assert.Empty(result.Sources);
        Assert.Empty(result.Issues);
        Assert.Equal(0, access.ReadCalls);
    }

    /// <summary>Rejects malformed input modes and network folder syntax before touching any candidate source.</summary>
    [Fact]
    public async Task InvalidShapesAndNetworkFoldersAreExplicitRejections()
    {
        var access = new RecordingSourceAccess();
        var discovery = new FileBatchInputDiscovery(access);
        BatchInputDiscoveryRequest[] requests =
        [
            new(BatchInputKind.ExplicitFiles, []),
            new(BatchInputKind.TopLevelFolder, ["one", "two"]),
            new((BatchInputKind)int.MaxValue, ["file.mkv"]),
            new(BatchInputKind.TopLevelFolder, ["relative-folder"]),
        ];

        foreach (var request in requests)
        {
            var result = await discovery.DiscoverAsync(request with { CancellationToken = TestContext.Current.CancellationToken });
            Assert.Equal(ErrorReason.InvalidBatchInput, result.Rejection?.Reason);
            Assert.Empty(result.Sources);
        }

        if (OperatingSystem.IsWindows())
        {
            var network = await discovery.DiscoverAsync(new(BatchInputKind.TopLevelFolder, [@"\\server\share"], TestContext.Current.CancellationToken));
            Assert.Equal(ErrorReason.NetworkPath, network.Rejection?.Reason);
        }

        Assert.Equal(0, access.ReadCalls);
    }

    /// <summary>Rejects linked folders before traversal and rejects linked candidates independently.</summary>
    [Fact(SkipUnless = nameof(IsLinux), Skip = "This symbolic-link regression requires Linux; Windows uses the same existing reparse policy.")]
    public async Task SymbolicLinksAreNotTraversedOrAcceptedAsRegularCandidates()
    {
        using var workspace = new TestWorkspace();
        var folder = Path.Combine(workspace.Path, "folder");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(workspace.Path, "outside.mkv");
        await File.WriteAllTextAsync(target, "outside folder", TestContext.Current.CancellationToken);
        File.CreateSymbolicLink(Path.Combine(folder, "linked.mkv"), target);
        Directory.CreateSymbolicLink(Path.Combine(folder, "linked-child"), workspace.Path);
        var linkedRoot = Path.Combine(workspace.Path, "linked-root");
        Directory.CreateSymbolicLink(linkedRoot, folder);
        var discovery = new FileBatchInputDiscovery(new FileSourceAccess());
        var entries = await discovery.DiscoverAsync(new(BatchInputKind.TopLevelFolder, [folder], TestContext.Current.CancellationToken));
        Assert.Null(entries.Rejection);
        Assert.Empty(entries.Sources);
        Assert.Equal(ErrorReason.RedirectedPath, Assert.Single(entries.Issues).Failure.Reason);
        var root = await discovery.DiscoverAsync(new(BatchInputKind.TopLevelFolder, [linkedRoot], TestContext.Current.CancellationToken));
        Assert.Equal(ErrorReason.RedirectedPath, root.Rejection?.Reason);
        Assert.Empty(root.Sources);
        Assert.Equal("outside folder", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
    }

    /// <summary>Rejects a named pipe through the reused regular-file policy without waiting for a writer.</summary>
    [Fact(SkipUnless = nameof(IsLinux), Skip = "The FIFO discovery regression requires Linux.")]
    public async Task FifoCandidateCannotBlockDiscovery()
    {
        using var workspace = new TestWorkspace();
        var fifo = Path.Combine(workspace.Path, "not-regular.mkv");
        Assert.Equal(0, MakeFifo(fifo, FifoOwnerReadWritePermissions));
        var result = await new FileBatchInputDiscovery(new FileSourceAccess()).DiscoverAsync(new(
            BatchInputKind.TopLevelFolder, [workspace.Path], TestContext.Current.CancellationToken))
            .WaitAsync(OperationTimeout, TestContext.Current.CancellationToken);
        Assert.Null(result.Rejection);
        Assert.Empty(result.Sources);
        Assert.Equal(ErrorCode.InvalidSource, Assert.Single(result.Issues).Failure.Code);
    }

    /// <summary>Returns from discovery while synchronous I/O is blocked elsewhere, then propagates cancellation.</summary>
    [Fact]
    public async Task SynchronousSourceIoRunsOffTheCallerAndRemainsCancellable()
    {
        using var workspace = new TestWorkspace();
        using var access = new BlockingSourceAccess();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var discovery = new FileBatchInputDiscovery(access);
        var request = new BatchInputDiscoveryRequest(BatchInputKind.ExplicitFiles,
            [Path.Combine(workspace.Path, "blocked.mkv")], cancellation.Token);
        var invocationReturned = new TaskCompletionSource<Task<BatchInputDiscoveryResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = new Thread(() =>
        {
            try
            {
                invocationReturned.TrySetResult(discovery.DiscoverAsync(request));
            }
            catch (Exception exception)
            {
                invocationReturned.TrySetException(exception);
            }
        })
        { IsBackground = true };
        caller.Start();

        try
        {
            var execution = await invocationReturned.Task.WaitAsync(OperationTimeout, TestContext.Current.CancellationToken);
            await access.Entered.Task.WaitAsync(OperationTimeout, TestContext.Current.CancellationToken);
            Assert.False(execution.IsCompleted);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(OperationTimeout, TestContext.Current.CancellationToken));
        }
        finally
        {
            access.Release.Set();
            Assert.True(caller.Join(OperationTimeout));
        }
    }

    /// <summary>Checks pre-cancellation before any filesystem request is dispatched.</summary>
    [Fact]
    public async Task PreCancelledDiscoveryDoesNotReadAnySource()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var access = new RecordingSourceAccess();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FileBatchInputDiscovery(access).DiscoverAsync(new(
            BatchInputKind.ExplicitFiles, [Path.Combine(Path.GetTempPath(), "not-read.mkv")], cancellation.Token)));
        Assert.Equal(0, access.ReadCalls);
    }

    /// <summary>Counts source calls without exposing a media backend to discovery.</summary>
    private sealed class RecordingSourceAccess : ISourceAccess
    {
        public int ReadCalls { get; private set; }

        /// <summary>Returns a distinct synthetic identity to isolate scan and request-shape behavior.</summary>
        public Task<SourceRevision> ReadRevisionAsync(SourceRequest request)
        {
            ReadCalls++;
            return Task.FromResult(new SourceRevision(request.Path, request.Path, 1, 1));
        }

        /// <summary>Implements the explicit preparation boundary without adding filesystem effects to this controlled port.</summary>
        public Task PrepareDestinationAsync(ValidateDestinationRequest request) => throw new InvalidOperationException("Discovery must not prepare outputs.");

        /// <summary>Fails if discovery crosses into destination validation.</summary>
        public Task ValidateDestinationAsync(ValidateDestinationRequest request) => throw new InvalidOperationException("Discovery must not validate outputs.");
    }

    /// <summary>Provides a synchronously blocked I/O port to verify the asynchronous caller boundary.</summary>
    private sealed class BlockingSourceAccess : ISourceAccess, IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();

        /// <summary>Blocks until cancellation or explicit test cleanup without sleeping or opening a media writer.</summary>
        public Task<SourceRevision> ReadRevisionAsync(SourceRequest request)
        {
            Entered.TrySetResult();
            Release.Wait(request.CancellationToken);
            return Task.FromResult(new SourceRevision(request.Path, request.Path, 1, 1));
        }

        /// <summary>Implements the explicit preparation boundary without adding filesystem effects to this controlled port.</summary>
        public Task PrepareDestinationAsync(ValidateDestinationRequest request) => throw new InvalidOperationException("Discovery must not prepare outputs.");

        /// <summary>Fails if discovery crosses into destination validation.</summary>
        public Task ValidateDestinationAsync(ValidateDestinationRequest request) => throw new InvalidOperationException("Discovery must not validate outputs.");

        /// <summary>Disposes the isolated wait resource after its worker has returned.</summary>
        public void Dispose() => Release.Dispose();
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string sourcePath, string linkPath);

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
}
