using TrackTrim.Application.Batches;
using TrackTrim.Application.QueueLists;
using TrackTrim.Core.Failures;
using TrackTrim.Infrastructure.Files;
using TrackTrim.Infrastructure.Persistence;
using Xunit;

namespace TrackTrim.Infrastructure.Tests;

/// <summary>Exercises mixed MKV/MP4 discovery on real small files without claiming media inspection.</summary>
public sealed class Mp4DiscoveryTests
{
    /// <summary>Both filters preserve deterministic top-level and recursive paths while excluding the output subtree.</summary>
    [Fact]
    public async Task MixedFolderDiscoveryRetainsTheSameBoundsOrderingAndProvenance()
    {
        using var workspace = new TestWorkspace();
        var root = Directory.CreateDirectory(Path.Combine(workspace.Path, "Сериал с пробелами")).FullName;
        var season = Directory.CreateDirectory(Path.Combine(root, "Season 1")).FullName;
        var output = Directory.CreateDirectory(Path.Combine(root, "Output excluded")).FullName;
        var topMkv = await CreateCandidateAsync(Path.Combine(root, "a series.mkv"));
        var topMp4 = await CreateCandidateAsync(Path.Combine(root, "b серия.MP4"));
        var nestedMp4 = await CreateCandidateAsync(Path.Combine(season, "episode.mp4"));
        var nestedMkv = await CreateCandidateAsync(Path.Combine(season, "episode.mkv"));
        var excluded = await CreateCandidateAsync(Path.Combine(output, "previous.mp4"));
        await File.WriteAllTextAsync(Path.Combine(root, "ignored.mov"), "unsupported filter", TestContext.Current.CancellationToken);
        var discovery = new FileBatchInputDiscovery(new FileSourceAccess());
        var immediate = await discovery.DiscoverAsync(new(BatchInputKind.TopLevelFolder, [root], TestContext.Current.CancellationToken));
        var recursiveRequest = new BatchInputDiscoveryRequest(BatchInputKind.RecursiveFolder, [root], TestContext.Current.CancellationToken, output);
        var recursive = await discovery.DiscoverAsync(recursiveRequest);
        var repeated = await discovery.DiscoverAsync(recursiveRequest);

        Assert.Null(immediate.Rejection);
        Assert.Empty(immediate.Issues);
        Assert.Equal([topMkv, topMp4], immediate.Sources.Select(source => source.FullPath));
        Assert.Null(recursive.Rejection);
        Assert.Empty(recursive.Issues);
        Assert.Equal(new[] { topMkv, topMp4, nestedMp4, nestedMkv }.Order(StringComparer.Ordinal),
            recursive.Sources.Select(source => source.FullPath));
        Assert.Equal(recursive.Sources, repeated.Sources);
        Assert.DoesNotContain(recursive.Sources, source => source.FullPath == excluded);
        Assert.Equal(new(root, Path.Combine("Season 1", "episode.mp4")), recursive.FolderOrigins![nestedMp4]);
        Assert.Equal(new(root, Path.Combine("Season 1", "episode.mkv")), recursive.FolderOrigins[nestedMkv]);
        Assert.Equal("unprobed candidate", await File.ReadAllTextAsync(excluded, TestContext.Current.CancellationToken));
    }

    /// <summary>Explicit discovery accepts both suffixes but does not pretend that extension-filtered bytes are valid media.</summary>
    [Fact]
    public async Task ExplicitMixedCandidatesDeduplicatePathsAndReportUnsupportedSiblings()
    {
        using var workspace = new TestWorkspace();
        var first = await CreateCandidateAsync(Path.Combine(workspace.Path, "a original.mkv"));
        var second = await CreateCandidateAsync(Path.Combine(workspace.Path, "b оригинал.mp4"));
        var unsupported = await CreateCandidateAsync(Path.Combine(workspace.Path, "other.m4v"));
        var missing = Path.Combine(workspace.Path, "missing.mp4");
        var result = await new FileBatchInputDiscovery(new FileSourceAccess()).DiscoverAsync(new(BatchInputKind.ExplicitFiles,
            [second, first, second, missing, unsupported], TestContext.Current.CancellationToken));

        Assert.Null(result.Rejection);
        Assert.Equal([first, second], result.Sources.Select(source => source.FullPath));
        Assert.Equal(2, result.Issues.Length);
        Assert.Contains(result.Issues, issue => issue.Path == unsupported && issue.Failure.Code == ErrorCode.UnsupportedContainer);
        Assert.Contains(result.Issues, issue => issue.Path == missing && issue.Failure.Code == ErrorCode.InvalidSource);
        Assert.All(result.Sources, source => Assert.NotEmpty(source.FileIdentity));
    }

    /// <summary>The entry limit applies to the combined candidate set rather than permitting a quota per container.</summary>
    [Theory]
    [InlineData(BatchInputKind.ExplicitFiles)]
    [InlineData(BatchInputKind.TopLevelFolder)]
    [InlineData(BatchInputKind.RecursiveFolder)]
    public async Task MixedContainersShareTheExistingWholeAdditionLimit(BatchInputKind kind)
    {
        using var workspace = new TestWorkspace();
        var root = Directory.CreateDirectory(Path.Combine(workspace.Path, "sources")).FullName;
        var output = Directory.CreateDirectory(Path.Combine(workspace.Path, "outputs")).FullName;
        var paths = new List<string>();

        for (var index = 0; index <= BatchDiscoveryLimits.MaximumFiles; index++)
        {
            var extension = index % 2 == 0 ? ".mkv" : ".mp4";
            paths.Add(await CreateCandidateAsync(Path.Combine(root, $"episode-{index:D3}" + extension)));
        }

        var selection = kind == BatchInputKind.ExplicitFiles ? paths : [root];
        var result = await new FileBatchInputDiscovery(new FileSourceAccess()).DiscoverAsync(new(kind, selection,
            TestContext.Current.CancellationToken, kind == BatchInputKind.RecursiveFolder ? output : null));

        Assert.Equal(ErrorReason.BatchFileLimit, result.Rejection?.Reason);
        Assert.Empty(result.Sources);
        Assert.Empty(result.Issues);
        Assert.Null(result.FolderOrigins);
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
    }

    /// <summary>Portable intent records accept MP4 folder provenance without persisting a probe or trusted stream index.</summary>
    [Fact]
    public async Task MixedFolderListingRoundtripPreservesRelativeDestinationsOnly()
    {
        using var workspace = new TestWorkspace();
        var sourceRoot = Path.Combine(workspace.Path, "Series Кириллица");
        var outputRoot = Path.Combine(workspace.Path, "Output with spaces");
        var mp4Relative = Path.Combine("Season 1", "episode.mp4");
        var mkvRelative = Path.Combine("Season 2", "episode.mkv");
        var listing = new QueueList([
            new(Path.Combine(sourceRoot, mp4Relative), Path.Combine(outputRoot, "Season 1", "episode.trimmed.mp4"),
                true, new(sourceRoot, mp4Relative), outputRoot),
            new(Path.Combine(sourceRoot, mkvRelative), Path.Combine(outputRoot, "Season 2", "episode.trimmed.mkv"),
                false, new(sourceRoot, mkvRelative), outputRoot)
        ]);
        var data = Path.Combine(workspace.Path, "data");
        var written = await new PortableQueueListStore(data).WriteAsync(new(listing, null, TestContext.Current.CancellationToken));
        var restored = await new PortableQueueListStore(data).ReadAsync(TestContext.Current.CancellationToken);

        Assert.True(written.Committed);
        Assert.Null(written.Failure);
        Assert.Null(restored.Failure);
        Assert.Equal(written.Revision, restored.Revision);
        Assert.Equal(listing.Entries, restored.List!.Entries);
        Assert.False(Directory.Exists(sourceRoot));
        Assert.False(Directory.Exists(outputRoot));
    }

    /// <summary>Writes only small task-owned filter candidates; the media backend is intentionally not called.</summary>
    private static async Task<string> CreateCandidateAsync(string path)
    {
        await File.WriteAllTextAsync(path, "unprobed candidate", TestContext.Current.CancellationToken);
        return path;
    }
}
