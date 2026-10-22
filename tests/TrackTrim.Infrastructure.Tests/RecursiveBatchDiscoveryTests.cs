using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using TrackTrim.Application.Batches;
using TrackTrim.Application.Ports;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Media;
using TrackTrim.Core.Planning;
using TrackTrim.Infrastructure.Files;
using Xunit;

namespace TrackTrim.Infrastructure.Tests;

/// <summary>Exercises recursive discovery against bounded, isolated real directory trees.</summary>
public sealed class RecursiveBatchDiscoveryTests
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Keeps top-level behavior and records immutable root-relative identities for each recursive source.</summary>
    [Fact]
    public async Task RecursiveModeIncludesSeasonsWithTheSameBasenameInOrdinalOrder()
    {
        using var workspace = new TestWorkspace();
        var root = CreateDirectory(workspace.Path, "Series Кириллица");
        var output = CreateDirectory(workspace.Path, "Output с пробелами");
        var top = await CreateCandidateAsync(root, "top.mkv");
        var second = await CreateCandidateAsync(CreateDirectory(root, "Season 2"), "episode.mkv");
        var first = await CreateCandidateAsync(CreateDirectory(root, "Season 1"), "episode.mkv");
        await File.WriteAllTextAsync(Path.Combine(root, "not-media.txt"), "ignored", TestContext.Current.CancellationToken);
        var discovery = new FileBatchInputDiscovery(new FileSourceAccess());
        var immediate = await discovery.DiscoverAsync(new(BatchInputKind.TopLevelFolder, [root], TestContext.Current.CancellationToken));
        var recursive = await discovery.DiscoverAsync(RecursiveRequest(root, output));
        var repeated = await discovery.DiscoverAsync(RecursiveRequest(root, output));

        Assert.Equal(top, Assert.Single(immediate.Sources).FullPath);
        Assert.Null(immediate.Rejection);
        Assert.Empty(immediate.Issues);
        Assert.Equal(new(root, "top.mkv"), immediate.FolderOrigins![top]);
        Assert.Null(recursive.Rejection);
        Assert.Empty(recursive.Issues);
        Assert.Equal(new[] { first, second, top }.Order(StringComparer.Ordinal), recursive.Sources.Select(source => source.FullPath));
        Assert.Equal(recursive.Sources, repeated.Sources);
        Assert.Equal(new(root, Path.Combine("Season 1", "episode.mkv")), recursive.FolderOrigins![first]);
        Assert.Equal(new(root, Path.Combine("Season 2", "episode.mkv")), recursive.FolderOrigins[second]);
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
        Assert.All(recursive.Sources, source => Assert.Equal("unprobed candidate", File.ReadAllText(source.FullPath)));
    }

    /// <summary>Models a network mount without privileged mounts and rejects even a child containing no MKV candidates.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ControlledNetworkChildClassificationRejectsTheWholeAddition(bool containsMkv)
    {
        using var workspace = new TestWorkspace();
        var root = CreateDirectory(workspace.Path, "Series");
        var output = CreateDirectory(workspace.Path, "output");
        var networkChild = CreateDirectory(CreateDirectory(root, "Season 2"), "controlled network mount");
        await CreateCandidateAsync(CreateDirectory(root, "Season 1"), "otherwise valid.mkv");

        if (containsMkv)
        {
            await CreateCandidateAsync(networkChild, "not eligible.mkv");
        }

        var classifiedDirectories = new List<string>();
        var access = new CountingSourceAccess();
        var discovery = new FileBatchInputDiscovery(access, null, null, path =>
        {
            classifiedDirectories.Add(path);
            return path == networkChild ? DriveType.Network : DriveType.Fixed;
        });
        var result = await discovery.DiscoverAsync(RecursiveRequest(root, output));

        Assert.Equal(ErrorCode.InvalidSource, result.Rejection?.Code);
        Assert.Equal(ErrorReason.NetworkPath, result.Rejection?.Reason);
        Assert.Contains(networkChild, classifiedDirectories);
        Assert.Empty(result.Sources);
        Assert.Empty(result.Issues);
        Assert.Null(result.FolderOrigins);
        Assert.Equal(0, access.ReadCalls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
    }

    /// <summary>Allows existing local filesystem types across child mount boundaries without requiring actual test mounts.</summary>
    [Theory]
    [InlineData(DriveType.Fixed)]
    [InlineData(DriveType.Removable)]
    [InlineData(DriveType.Ram)]
    public async Task ControlledLocalChildClassificationPreservesSupportedDirectoryDiscovery(DriveType childDriveType)
    {
        using var workspace = new TestWorkspace();
        var root = CreateDirectory(workspace.Path, "Series");
        var output = CreateDirectory(workspace.Path, "output");
        var child = CreateDirectory(root, "local mounted season");
        var source = await CreateCandidateAsync(child, "episode.mkv");
        var classifiedDirectories = new List<string>();
        var discovery = new FileBatchInputDiscovery(new FileSourceAccess(), null, null, path =>
        {
            classifiedDirectories.Add(path);
            return path == child ? childDriveType : DriveType.Fixed;
        });
        var result = await discovery.DiscoverAsync(RecursiveRequest(root, output));

        Assert.Null(result.Rejection);
        Assert.Empty(result.Issues);
        Assert.Equal(source, Assert.Single(result.Sources).FullPath);
        Assert.Equal(new(root, Path.Combine("local mounted season", "episode.mkv")), result.FolderOrigins![source]);
        Assert.Contains(child, classifiedDirectories);
    }

    /// <summary>Requires an existing output root and rejects equality or ancestry without rejecting sibling prefixes.</summary>
    [Fact]
    public async Task RecursiveOutputPolicyRejectsMissingAndConflictingRoots()
    {
        using var workspace = new TestWorkspace();
        var root = CreateDirectory(workspace.Path, "Series");
        var output = CreateDirectory(workspace.Path, "Series-output");
        var access = new CountingSourceAccess();
        var discovery = new FileBatchInputDiscovery(access);
        var missing = await discovery.DiscoverAsync(new(BatchInputKind.RecursiveFolder, [root], TestContext.Current.CancellationToken));
        var nonexistent = await discovery.DiscoverAsync(RecursiveRequest(root, Path.Combine(workspace.Path, "not created")));
        var equal = await discovery.DiscoverAsync(RecursiveRequest(root, root));
        var ancestor = await discovery.DiscoverAsync(RecursiveRequest(root, workspace.Path));
        var sibling = await discovery.DiscoverAsync(RecursiveRequest(root, output));

        Assert.Equal(ErrorReason.RecursiveOutputRequired, missing.Rejection?.Reason);
        Assert.NotNull(nonexistent.Rejection);
        Assert.Equal(ErrorReason.RecursiveRootConflict, equal.Rejection?.Reason);
        Assert.Equal(ErrorReason.RecursiveRootConflict, ancestor.Rejection?.Reason);
        Assert.Null(sibling.Rejection);
        Assert.Equal(0, access.ReadCalls);
        Assert.False(Directory.Exists(Path.Combine(workspace.Path, "not created")));
    }

    /// <summary>Excludes actual output, portable-data and validated ownership before controlled mount classification.</summary>
    [Fact]
    public async Task RecursiveDiscoveryExcludesOutputAndKnownOwnedWorkingDirectoriesBeforeMountClassification()
    {
        using var workspace = new TestWorkspace();
        var root = CreateDirectory(workspace.Path, "Series");
        var season = CreateDirectory(root, "Season 1");
        var output = CreateDirectory(root, "Output");
        var data = CreateDirectory(root, "portable data");
        var source = await CreateCandidateAsync(season, "episode.mkv");
        await CreateCandidateAsync(output, "published.mkv");
        var outputChild = CreateDirectory(output, "controlled network mount");
        await CreateCandidateAsync(outputChild, "not a source.mkv");
        await CreateCandidateAsync(data, "inspection.mkv");
        var unowned = await CreateCandidateAsync(CreateDirectory(root, ".tracktrim-unrelated"), "unrelated.mkv");
        var store = new OwnedOutputStore(data);
        var revision = await new FileSourceAccess().ReadRevisionAsync(new(source, TestContext.Current.CancellationToken));
        var inspection = new MediaInspection(revision, new("discovery-test", "1", 1), [], [], "", false, false);
        var plan = new RemuxPlan(Guid.NewGuid(), inspection, [], Path.Combine(season, "reserved.trimmed.mkv"), false);
        var owned = await store.ReserveAsync(new(plan, TestContext.Current.CancellationToken));
        await File.WriteAllTextAsync(owned.CandidatePath, "owned candidate", TestContext.Current.CancellationToken);
        var classifiedDirectories = new List<string>();
        var excludedDirectories = new[] { outputChild, data, owned.DirectoryPath };
        var discovery = new FileBatchInputDiscovery(new FileSourceAccess(), store, [data], path =>
        {
            classifiedDirectories.Add(path);
            return excludedDirectories.Contains(path, StringComparer.Ordinal) ? DriveType.Network : DriveType.Fixed;
        });
        var result = await discovery.DiscoverAsync(RecursiveRequest(root, output));

        Assert.Null(result.Rejection);
        Assert.Empty(result.Issues);
        Assert.Equal(new[] { source, unowned }.Order(StringComparer.Ordinal), result.Sources.Select(entry => entry.FullPath));
        Assert.Equal("owned candidate", await File.ReadAllTextAsync(owned.CandidatePath, TestContext.Current.CancellationToken));
        Assert.Single(await store.FindInterruptedAsync(TestContext.Current.CancellationToken));
        Assert.Equal("unprobed candidate", await File.ReadAllTextAsync(Path.Combine(output, "published.mkv"), TestContext.Current.CancellationToken));
        Assert.All(excludedDirectories, excluded => Assert.DoesNotContain(excluded, classifiedDirectories));
    }

    /// <summary>Does not allow a validated application working tree to become the recursive source root.</summary>
    [Fact]
    public async Task KnownWorkingDirectoryCannotBecomeTheSourceRoot()
    {
        using var workspace = new TestWorkspace();
        var data = CreateDirectory(workspace.Path, "data");
        var output = CreateDirectory(workspace.Path, "output");
        await CreateCandidateAsync(data, "not a source.mkv");
        var result = await new FileBatchInputDiscovery(new FileSourceAccess(), workingDirectories: [data])
            .DiscoverAsync(RecursiveRequest(data, output));
        Assert.Equal(ErrorReason.InvalidBatchInput, result.Rejection?.Reason);
        Assert.Empty(result.Sources);
    }

    /// <summary>Uses the first ordinal alias's relative identity while retaining explicit-file discovery compatibility.</summary>
    [Fact]
    public async Task HardLinkAliasesRetainTheFirstDeterministicFolderOrigin()
    {
        using var workspace = new TestWorkspace();
        var root = CreateDirectory(workspace.Path, "Series");
        var output = CreateDirectory(workspace.Path, "output");
        var original = await CreateCandidateAsync(CreateDirectory(root, "Season 2"), "episode.mkv");
        var alias = Path.Combine(CreateDirectory(root, "Season 1"), "episode.mkv");
        CreateHardLink(original, alias);
        var discovery = new FileBatchInputDiscovery(new FileSourceAccess());
        var folder = await discovery.DiscoverAsync(RecursiveRequest(root, output));
        var explicitResult = await discovery.DiscoverAsync(new(BatchInputKind.ExplicitFiles, [original], TestContext.Current.CancellationToken));
        var retained = Assert.Single(folder.Sources);
        Assert.Equal(alias, retained.FullPath);
        Assert.Equal(new(root, Path.Combine("Season 1", "episode.mkv")), folder.FolderOrigins![alias]);
        Assert.Single(folder.FolderOrigins);
        Assert.Equal(retained.FileIdentity, Assert.Single(explicitResult.Sources).FileIdentity);
        Assert.Null(explicitResult.FolderOrigins);
    }

    /// <summary>Counts files, directories and unrelated entries globally instead of granting a fresh bound to each season.</summary>
    [Fact]
    public async Task GlobalEntryBudgetAcceptsExactCapacityAndRejectsOverflowBeforeReadingSources()
    {
        using var workspace = new TestWorkspace();
        var root = CreateDirectory(workspace.Path, "Series");
        var output = CreateDirectory(workspace.Path, "output");
        var first = CreateDirectory(root, "Season 1");
        var second = CreateDirectory(root, "Season 2");
        const int seasonDirectories = 2;
        var fileCount = BatchDiscoveryLimits.MaximumExaminedEntries - seasonDirectories;

        for (var index = 0; index < fileCount; index++)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var parent = index % seasonDirectories == 0 ? first : second;
            using var stream = File.Create(Path.Combine(parent, $"unrelated-{index:D5}.txt"));
        }

        var access = new CountingSourceAccess();
        var discovery = new FileBatchInputDiscovery(access);
        var exact = await discovery.DiscoverAsync(RecursiveRequest(root, output));
        Assert.Null(exact.Rejection);
        Assert.Empty(exact.Sources);
        await CreateCandidateAsync(second, "overflow.mkv");
        var overflow = await discovery.DiscoverAsync(RecursiveRequest(root, output));
        Assert.Equal(ErrorReason.BatchEntryLimit, overflow.Rejection?.Reason);
        Assert.Empty(overflow.Sources);
        Assert.Empty(overflow.Issues);
        Assert.Equal(0, access.ReadCalls);
    }

    /// <summary>Applies the existing candidate limit across all nested source directories without accepting a prefix.</summary>
    [Fact]
    public async Task NestedCandidatesCannotBypassTheHundredEntryLimit()
    {
        using var workspace = new TestWorkspace();
        var root = CreateDirectory(workspace.Path, "Series");
        var output = CreateDirectory(workspace.Path, "output");
        var first = CreateDirectory(root, "Season 1");
        var second = CreateDirectory(root, "Season 2");

        for (var index = 0; index <= BatchDiscoveryLimits.MaximumFiles; index++)
        {
            await CreateCandidateAsync(index % 2 == 0 ? first : second, $"episode-{index:D3}.mkv");
        }

        var result = await new FileBatchInputDiscovery(new FileSourceAccess()).DiscoverAsync(RecursiveRequest(root, output));
        Assert.Equal(ErrorReason.BatchFileLimit, result.Rejection?.Reason);
        Assert.Empty(result.Sources);
        Assert.Empty(result.Issues);
        Assert.Null(result.FolderOrigins);
    }

    /// <summary>Bounds depth even in a narrow directory chain, independently of the total entry count.</summary>
    [Fact]
    public async Task DepthOverflowRejectsTheWholeAddition()
    {
        using var workspace = new TestWorkspace();
        var root = CreateDirectory(workspace.Path, "Series");
        var output = CreateDirectory(workspace.Path, "output");
        await CreateCandidateAsync(root, "otherwise valid.mkv");
        var deepest = root;

        for (var depth = 0; depth <= BatchDiscoveryLimits.MaximumDirectoryDepth; depth++)
        {
            deepest = CreateDirectory(deepest, "d");
        }

        var result = await new FileBatchInputDiscovery(new FileSourceAccess()).DiscoverAsync(RecursiveRequest(root, output));
        Assert.Equal(ErrorReason.BatchDepthLimit, result.Rejection?.Reason);
        Assert.Empty(result.Sources);
        Assert.Empty(result.Issues);
    }

    /// <summary>Rejects a directory junction or symbolic link without traversing or accepting siblings from a partial scan.</summary>
    [Fact]
    public async Task RedirectedDirectoryRejectsTheAdditionWithoutFollowingIt()
    {
        using var workspace = new TestWorkspace();
        var root = CreateDirectory(workspace.Path, "Series");
        var output = CreateDirectory(workspace.Path, "output");
        var outside = CreateDirectory(workspace.Path, "outside");
        await CreateCandidateAsync(root, "otherwise valid.mkv");
        var outsideSource = await CreateCandidateAsync(outside, "outside.mkv");
        var link = Path.Combine(root, "redirected directory");
        await CreateDirectoryRedirectAsync(link, outside);

        try
        {
            var result = await new FileBatchInputDiscovery(new FileSourceAccess()).DiscoverAsync(RecursiveRequest(root, output));
            Assert.Equal(ErrorReason.RedirectedPath, result.Rejection?.Reason);
            Assert.Empty(result.Sources);
            Assert.Empty(result.Issues);
            Assert.Equal("unprobed candidate", await File.ReadAllTextAsync(outsideSource, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    /// <summary>Propagates deterministic cancellation before discovery returns any accepted candidate snapshot.</summary>
    [Fact]
    public async Task CancellationBeforeDiscoveryAcceptanceDoesNotReturnAPartialSubset()
    {
        using var workspace = new TestWorkspace();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var root = CreateDirectory(workspace.Path, "Series");
        var output = CreateDirectory(workspace.Path, "output");
        await CreateCandidateAsync(CreateDirectory(root, "Season 1"), "episode.mkv");
        await CreateCandidateAsync(CreateDirectory(root, "Season 2"), "episode.mkv");
        var access = new GatedSourceAccess();
        var execution = new FileBatchInputDiscovery(access).DiscoverAsync(RecursiveRequest(root, output) with { CancellationToken = cancellation.Token });
        await access.Entered.Task.WaitAsync(OperationTimeout, TestContext.Current.CancellationToken);
        Assert.False(execution.IsCompleted);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(OperationTimeout, TestContext.Current.CancellationToken));
        Assert.Equal(1, access.ReadCalls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
    }

    /// <summary>Keeps invalid regular-file candidates visible with folder context without probing their media extension.</summary>
    [Fact]
    public async Task InvalidNestedCandidateKeepsItsOwnRelativeContext()
    {
        using var workspace = new TestWorkspace();
        var root = CreateDirectory(workspace.Path, "Series");
        var output = CreateDirectory(workspace.Path, "output");
        var season = CreateDirectory(root, "Season 1");
        var valid = await CreateCandidateAsync(season, "episode.mkv");
        var unreadable = await CreateCandidateAsync(season, "unreadable.mkv");
        var result = await new FileBatchInputDiscovery(new DeniedSourceAccess(unreadable)).DiscoverAsync(RecursiveRequest(root, output));
        Assert.Null(result.Rejection);
        Assert.Equal(valid, Assert.Single(result.Sources).FullPath);
        Assert.Equal(unreadable, Assert.Single(result.Issues).Path);
        Assert.Equal(new(root, Path.Combine("Season 1", "unreadable.mkv")), result.FolderOrigins![unreadable]);
    }

    /// <summary>Creates one task-owned directory without deriving production output paths.</summary>
    private static string CreateDirectory(string parent, string name) => Directory.CreateDirectory(Path.Combine(parent, name)).FullName;

    /// <summary>Writes a regular candidate whose supported-container decision remains the later backend's responsibility.</summary>
    private static async Task<string> CreateCandidateAsync(string parent, string name)
    {
        var path = Path.Combine(parent, name);
        await File.WriteAllTextAsync(path, "unprobed candidate", TestContext.Current.CancellationToken);
        return path;
    }

    /// <summary>Builds a recursive request with an explicitly existing output root.</summary>
    private static BatchInputDiscoveryRequest RecursiveRequest(string root, string output) =>
        new(BatchInputKind.RecursiveFolder, [root], TestContext.Current.CancellationToken, output);

    /// <summary>Creates an actual file-identity alias using the host platform's hard-link operation.</summary>
    private static void CreateHardLink(string original, string alias)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.True(CreateHardLinkWindows(alias, original, IntPtr.Zero));
            return;
        }

        Assert.Equal(0, Link(original, alias));
    }

    /// <summary>Creates a Windows junction without elevation or a Linux symbolic link to exercise the real redirect policy.</summary>
    private static async Task CreateDirectoryRedirectAsync(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(link);
        start.ArgumentList.Add(target);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, $"Junction creation failed: {await output} {await error}");
    }

    /// <summary>Records candidate reads while forbidding discovery from performing output operations.</summary>
    private class CountingSourceAccess : ISourceAccess
    {
        public int ReadCalls { get; protected set; }

        /// <summary>Returns a distinct identity for an already enumerated candidate.</summary>
        public virtual Task<SourceRevision> ReadRevisionAsync(SourceRequest request)
        {
            ReadCalls++;
            return Task.FromResult(new SourceRevision(request.Path, request.Path, 1, 1));
        }

        /// <summary>Fails if discovery enters plan validation.</summary>
        public Task ValidateDestinationAsync(ValidateDestinationRequest request) => throw new InvalidOperationException("Discovery must not validate destinations.");

        /// <summary>Fails if discovery attempts to create output directories.</summary>
        public Task PrepareDestinationAsync(ValidateDestinationRequest request) => throw new InvalidOperationException("Discovery must not prepare destinations.");
    }

    /// <summary>Models an unreadable candidate without relying on host administrator or POSIX privilege differences.</summary>
    private sealed class DeniedSourceAccess(string deniedPath) : CountingSourceAccess
    {
        /// <summary>Rejects only the controlled unreadable candidate and captures valid sibling identities normally.</summary>
        public override Task<SourceRevision> ReadRevisionAsync(SourceRequest request)
        {
            if (request.Path == deniedPath)
            {
                throw new UnauthorizedAccessException("Controlled unreadable entry.");
            }

            return new FileSourceAccess().ReadRevisionAsync(request);
        }
    }

    /// <summary>Blocks a candidate read until cancellation so discovery acceptance has a deterministic synchronization point.</summary>
    private sealed class GatedSourceAccess : CountingSourceAccess
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Waits on cancellation rather than relying on filesystem timing or arbitrary delays.</summary>
        public override async Task<SourceRevision> ReadRevisionAsync(SourceRequest request)
        {
            ReadCalls++;
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, request.CancellationToken);
            throw new InvalidOperationException("The cancellation gate must not complete normally.");
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(string fileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string sourcePath, string linkPath);
}
