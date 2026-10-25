using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using MovieSift.Application.QueueLists;
using MovieSift.Infrastructure.Persistence;
using Xunit;

namespace MovieSift.Infrastructure.Tests;

/// <summary>Exercises actual queue-list files, revision conflicts and the portable OS lock without media writes.</summary>
public sealed class PortableQueueListStoreTests
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Reading missing state neither creates data nor invents a persisted revision.</summary>
    [Fact]
    public async Task MissingRecordIsEmptyWithoutCreatingPortableData()
    {
        using var workspace = new TestWorkspace();
        var data = Path.Combine(workspace.Path, "data");
        var result = await new PortableQueueListStore(data).ReadAsync(TestContext.Current.CancellationToken);
        Assert.Null(result.Failure);
        Assert.Null(result.Revision);
        Assert.Empty(result.List!.Entries);
        Assert.False(Directory.Exists(data));
    }

    /// <summary>Fresh instances restore Unicode paths, inclusion and recursive provenance without inspection authority.</summary>
    [Fact]
    public async Task FreshStoreRoundTripsIntendedListingWithoutTouchingSourceOrOutputFiles()
    {
        using var workspace = new TestWorkspace();
        var list = Listing(workspace.Path);
        var saved = await Store(workspace).WriteAsync(new(list, null, TestContext.Current.CancellationToken));
        Assert.True(saved.Committed);
        Assert.NotEqual(Guid.Empty, saved.Revision);
        Assert.Null(saved.Failure);
        var loaded = await Store(workspace).ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(saved.Revision, loaded.Revision);
        Assert.True(QueueListPolicy.ContentEquals(list, loaded.List));
        Assert.NotSame(list, loaded.List);
        Assert.False(Directory.Exists(Path.GetDirectoryName(list.Entries[0].SourcePath)));
        Assert.False(Directory.Exists(list.Entries[0].OutputDirectoryRoot));
        var json = JsonNode.Parse(await ReadBytesAsync(workspace))!.AsObject();
        Assert.Equal(new[] { "entries", "revision", "version" }, json.Select(property => property.Key).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "destinationPath", "folderOrigin", "included", "outputDirectoryRoot", "sourcePath" },
            json["entries"]![0]!.AsObject().Select(property => property.Key).Order(StringComparer.Ordinal));
        AssertNoTemporaryFiles(workspace.Path);
    }

    /// <summary>Saving unchanged deserialized content preserves the revision and avoids a replacement.</summary>
    [Fact]
    public async Task ContentEqualWriteIsNoopAcrossDeserialization()
    {
        using var workspace = new TestWorkspace();
        var saved = await Store(workspace).WriteAsync(new(Listing(workspace.Path), null, TestContext.Current.CancellationToken));
        var loaded = await Store(workspace).ReadAsync(TestContext.Current.CancellationToken);
        var before = await ReadBytesAsync(workspace);
        var actions = new QueueListFileActions((_, _) => throw new IOException("A no-op must not publish."), File.Delete);
        var result = await new PortableQueueListStore(workspace.Path, actions).WriteAsync(new(loaded.List!, loaded.Revision,
            TestContext.Current.CancellationToken));
        Assert.False(result.Committed);
        Assert.Null(result.Failure);
        Assert.Equal(saved.Revision, result.Revision);
        Assert.Equal(before, await ReadBytesAsync(workspace));
    }

    /// <summary>A stale writer cannot silently replace another instance's list, including after explicit discard.</summary>
    [Fact]
    public async Task RevisionComparisonPreventsLostUpdatesAndDiscardDoesNotCreateAnAbaGap()
    {
        using var workspace = new TestWorkspace();
        var first = Store(workspace);
        var second = Store(workspace);
        var saved = await first.WriteAsync(new(Listing(workspace.Path), null, TestContext.Current.CancellationToken));
        var snapshot = await second.ReadAsync(TestContext.Current.CancellationToken);
        var discarded = await first.WriteAsync(new(new([]), saved.Revision, TestContext.Current.CancellationToken));
        Assert.True(discarded.Committed);
        Assert.NotNull(discarded.Revision);
        Assert.NotEqual(saved.Revision, discarded.Revision);
        var bytes = await ReadBytesAsync(workspace);
        var conflict = await second.WriteAsync(new(snapshot.List!, snapshot.Revision, TestContext.Current.CancellationToken));
        Assert.Equal(QueueListErrorCode.Conflict, conflict.Failure?.Code);
        Assert.False(conflict.Committed);
        Assert.Equal(discarded.Revision, conflict.Revision);
        Assert.Equal(bytes, await ReadBytesAsync(workspace));
        Assert.Empty((await second.ReadAsync(TestContext.Current.CancellationToken)).List!.Entries);
        Assert.True(File.Exists(RecordPath(workspace)));
    }

    /// <summary>Every invalid document is preserved byte-for-byte and cannot be overwritten by a fresh empty write.</summary>
    [Theory]
    [InlineData("malformed")]
    [InlineData("null-root")]
    [InlineData("missing-version")]
    [InlineData("missing-revision")]
    [InlineData("empty-revision")]
    [InlineData("missing-entries")]
    [InlineData("null-entries")]
    [InlineData("null-entry")]
    [InlineData("missing-source")]
    [InlineData("null-source")]
    [InlineData("relative-source")]
    [InlineData("missing-destination")]
    [InlineData("missing-included")]
    [InlineData("missing-origin")]
    [InlineData("null-origin-root")]
    [InlineData("relative-traversal")]
    [InlineData("mismatched-origin")]
    [InlineData("escaped-output")]
    [InlineData("missing-output-root")]
    [InlineData("unknown-job")]
    [InlineData("duplicate-source")]
    [InlineData("duplicate-property")]
    [InlineData("invalid-property-unicode")]
    [InlineData("invalid-path-unicode")]
    [InlineData("invalid-utf8")]
    public async Task InvalidRecordNeverLoadsOrReplacesAPartialListing(string corruption)
    {
        using var workspace = new TestWorkspace();
        var bytes = CorruptDocument(workspace.Path, corruption);
        await File.WriteAllBytesAsync(RecordPath(workspace), bytes, TestContext.Current.CancellationToken);
        var read = await Store(workspace).ReadAsync(TestContext.Current.CancellationToken);
        var write = await Store(workspace).WriteAsync(new(new([]), null, TestContext.Current.CancellationToken));
        Assert.Equal(QueueListErrorCode.InvalidRecord, read.Failure?.Code);
        Assert.Equal(QueueListErrorCode.InvalidRecord, write.Failure?.Code);
        Assert.Null(read.List);
        Assert.Null(write.List);
        Assert.False(write.Committed);
        Assert.Equal(bytes, await ReadBytesAsync(workspace));
        AssertNoTemporaryFiles(workspace.Path);
    }

    /// <summary>Unsupported version and actual-byte bounds have explicit outcomes distinct from malformed JSON.</summary>
    [Theory]
    [InlineData("version", QueueListErrorCode.UnsupportedVersion)]
    [InlineData("bytes", QueueListErrorCode.LimitExceeded)]
    [InlineData("count", QueueListErrorCode.LimitExceeded)]
    public async Task IncompatibleAndOversizedRecordsArePreserved(string problem, QueueListErrorCode expected)
    {
        using var workspace = new TestWorkspace();
        var node = ValidDocument(workspace.Path);

        if (problem == "version")
        {
            node["version"] = QueueListJson.CurrentVersion + 1;
        }

        if (problem == "count")
        {
            node["entries"] = new JsonArray(Enumerable.Range(0, QueueListPolicy.MaximumEntries + 1)
                .Select(index => (JsonNode)new JsonObject
                {
                    ["sourcePath"] = Path.Combine(workspace.Path, $"{index}.mkv"),
                    ["destinationPath"] = null,
                    ["included"] = true,
                    ["folderOrigin"] = null,
                    ["outputDirectoryRoot"] = null,
                }).ToArray());
        }

        var bytes = problem == "bytes" ? new byte[QueueListJson.MaximumBytes + 1] : Encoding.UTF8.GetBytes(node.ToJsonString());
        await File.WriteAllBytesAsync(RecordPath(workspace), bytes, TestContext.Current.CancellationToken);
        var read = await Store(workspace).ReadAsync(TestContext.Current.CancellationToken);
        var write = await Store(workspace).WriteAsync(new(new([]), null, TestContext.Current.CancellationToken));
        Assert.Equal(expected, read.Failure?.Code);
        Assert.Equal(expected, write.Failure?.Code);
        Assert.Equal(bytes, await ReadBytesAsync(workspace));
    }

    /// <summary>Measures actual serialized bytes before creating a temporary file or replacing valid state.</summary>
    [Fact]
    public async Task OversizedProposedListingPreservesPreviousBytesWithoutTemporaryFiles()
    {
        using var workspace = new TestWorkspace();
        var saved = await Store(workspace).WriteAsync(new(Listing(workspace.Path), null, TestContext.Current.CancellationToken));
        var before = await ReadBytesAsync(workspace);
        var large = new QueueList(Enumerable.Range(0, 30).Select(index => new QueueListEntry(
            Path.Combine(workspace.Path, new string('x', 3000), $"{index}.mkv"), null)).ToImmutableArray());
        Assert.Null(QueueListPolicy.Validate(large));
        var result = await Store(workspace).WriteAsync(new(large, saved.Revision, TestContext.Current.CancellationToken));
        Assert.Equal(QueueListErrorCode.LimitExceeded, result.Failure?.Code);
        Assert.False(result.Committed);
        Assert.Equal(before, await ReadBytesAsync(workspace));
        AssertNoTemporaryFiles(workspace.Path);
    }

    /// <summary>A failed replacement preserves the primary write failure and reports secondary owned cleanup separately.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedWritePreservesPreviousRecordAndPrimaryFailure(bool failCleanup)
    {
        using var workspace = new TestWorkspace();
        var saved = await Store(workspace).WriteAsync(new(Listing(workspace.Path), null, TestContext.Current.CancellationToken));
        var before = await ReadBytesAsync(workspace);
        var actions = new QueueListFileActions((_, _) => throw new IOException("Primary replacement failure."), path =>
        {
            if (failCleanup)
            {
                throw new IOException("Secondary cleanup failure.");
            }

            File.Delete(path);
        });
        var result = await new PortableQueueListStore(workspace.Path, actions).WriteAsync(new(new([]), saved.Revision,
            TestContext.Current.CancellationToken));
        Assert.False(result.Committed);
        Assert.Equal(QueueListErrorCode.PersistenceUnavailable, result.Failure?.Code);
        Assert.Contains("Primary replacement failure", result.Failure!.Detail);
        Assert.Equal(failCleanup ? 1 : 0, result.CleanupIssues.IsDefault ? 0 : result.CleanupIssues.Length);
        Assert.Equal(before, await ReadBytesAsync(workspace));

        if (!failCleanup)
        {
            AssertNoTemporaryFiles(workspace.Path);
        }
    }

    /// <summary>After publication the old temporary path loses ownership even if another entry recreates it.</summary>
    [Fact]
    public async Task PublishedRecordIsCommittedEvenWhenCancellationArrivesAfterTheMove()
    {
        using var workspace = new TestWorkspace();
        using var cancellation = new CancellationTokenSource();
        string? recreated = null;
        var cleanupCalled = false;
        var actions = new QueueListFileActions((source, destination) =>
        {
            File.Move(source, destination, overwrite: true);
            recreated = source;
            File.WriteAllText(source, "unrelated recreated file");
            cancellation.Cancel();
        }, _ => cleanupCalled = true);
        var result = await new PortableQueueListStore(workspace.Path, actions).WriteAsync(new(Listing(workspace.Path), null,
            cancellation.Token));
        Assert.True(result.Committed);
        Assert.Null(result.Failure);
        Assert.False(cleanupCalled);
        Assert.Equal("unrelated recreated file", await File.ReadAllTextAsync(recreated!, TestContext.Current.CancellationToken));
        Assert.True(QueueListPolicy.ContentEquals(result.List, (await Store(workspace).ReadAsync(TestContext.Current.CancellationToken)).List));
    }

    /// <summary>A failed publisher cannot authorize cleanup of an unrelated file moved into the former temporary path.</summary>
    [Fact]
    public async Task FailedPublicationPreservesSubstitutedTemporaryFileAndPrimaryFailure()
    {
        using var workspace = new TestWorkspace();
        var sentinel = Path.Combine(workspace.Path, "unrelated.txt");
        await File.WriteAllTextAsync(sentinel, "unrelated original", TestContext.Current.CancellationToken);
        string? substitutedPath = null;
        var cleanupCalled = false;
        var actions = new QueueListFileActions((temporary, _) =>
        {
            File.Delete(temporary);
            File.Move(sentinel, temporary);
            substitutedPath = temporary;
            throw new IOException("Primary replacement failure.");
        }, _ => cleanupCalled = true);
        var result = await new PortableQueueListStore(workspace.Path, actions).WriteAsync(new(Listing(workspace.Path), null,
            TestContext.Current.CancellationToken));
        Assert.False(result.Committed);
        Assert.Contains("Primary replacement failure", result.Failure!.Detail);
        Assert.Contains("no longer identifies", Assert.Single(result.CleanupIssues).Detail);
        Assert.False(cleanupCalled);
        Assert.Equal("unrelated original", await File.ReadAllTextAsync(substitutedPath!, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(RecordPath(workspace)));
    }

    /// <summary>A separately running helper holds the same production OS lock until explicitly released.</summary>
    [Fact]
    public async Task SeparateProcessWriterIsBusyAndRetrySucceedsOnlyAfterRelease()
    {
        using var workspace = new TestWorkspace();
        var saved = await Store(workspace).WriteAsync(new(Listing(workspace.Path), null, TestContext.Current.CancellationToken));
        var before = await ReadBytesAsync(workspace);
        using var worker = StartLockWorker(Path.Combine(workspace.Path, PortableQueueListStore.LockFileName));

        try
        {
            var ready = await worker.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(ProcessTimeout, TestContext.Current.CancellationToken);
            Assert.Equal("PRESET_LOCK_ACQUIRED", ready);
            var blocked = await Store(workspace).WriteAsync(new(new([]), saved.Revision, TestContext.Current.CancellationToken))
                .WaitAsync(ProcessTimeout, TestContext.Current.CancellationToken);
            Assert.Equal(QueueListErrorCode.Busy, blocked.Failure?.Code);
            Assert.False(blocked.Committed);
            Assert.Equal(before, await ReadBytesAsync(workspace));
            Assert.Null((await Store(workspace).ReadAsync(TestContext.Current.CancellationToken)).Failure);
            await worker.StandardInput.WriteLineAsync(ReadOnlyMemory<char>.Empty, TestContext.Current.CancellationToken);
            await worker.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(ProcessTimeout, TestContext.Current.CancellationToken);
            Assert.Equal(0, worker.ExitCode);
            var retry = await Store(workspace).WriteAsync(new(new([]), saved.Revision, TestContext.Current.CancellationToken));
            Assert.True(retry.Committed);
            Assert.Empty(retry.List!.Entries);
        }
        finally
        {
            if (!worker.HasExited)
            {
                worker.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync(CancellationToken.None).WaitAsync(ProcessTimeout, CancellationToken.None);
            }
        }
    }

    /// <summary>Precancelled reads and writes do not create portable storage or report success.</summary>
    [Fact]
    public async Task PrecancelledOperationsLeaveMissingStateMissing()
    {
        using var workspace = new TestWorkspace();
        var data = Path.Combine(workspace.Path, "data");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var store = new PortableQueueListStore(data);
        var read = await store.ReadAsync(cancellation.Token);
        var write = await store.WriteAsync(new(Listing(workspace.Path), null, cancellation.Token));
        Assert.Equal(QueueListErrorCode.Cancelled, read.Failure?.Code);
        Assert.Equal(QueueListErrorCode.Cancelled, write.Failure?.Code);
        Assert.False(Directory.Exists(data));
    }

    /// <summary>Produces a complete recursive entry and a missing explicit source without requiring inspection.</summary>
    private static QueueList Listing(string root)
    {
        var sourceRoot = Path.Combine(root, "Сериал с пробелами");
        var relative = Path.Combine("Season 1", "эпизод [literal].mkv");
        var outputRoot = Path.Combine(root, "Результаты остаются");
        return new([
            new(Path.Combine(sourceRoot, relative), Path.Combine(outputRoot, "Season 1", "эпизод [literal].trimmed.mkv"),
                true, new(sourceRoot, relative), outputRoot),
            new(Path.Combine(root, "missing file.mkv"), null, false),
        ]);
    }

    /// <summary>Starts the existing controlled helper without introducing another production executable.</summary>
    private static Process StartLockWorker(string lockPath)
    {
        var runtime = new DirectoryInfo(Path.GetDirectoryName(typeof(object).Assembly.Location)!);
        var runtimeRoot = runtime.Parent!.Parent!.Parent!.FullName;
        var executable = Path.Combine(runtimeRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var helper = Path.Combine(MediaTestEnvironment.RepositoryDirectory, "tests", "MovieSift.ProcessTestWorker", "bin",
            configuration, "net10.0", "MovieSift.ProcessTestWorker.dll");
        Assert.True(File.Exists(helper));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(helper);
        start.ArgumentList.Add("preset-lock");
        start.ArgumentList.Add(lockPath);
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the controlled lock holder.");
    }

    /// <summary>Builds one valid document before a focused untrusted-record mutation.</summary>
    private static JsonObject ValidDocument(string root) => JsonNode.Parse(QueueListJson.Write(Listing(root), Guid.NewGuid()))!.AsObject();

    /// <summary>Changes only one required structure or safety invariant per invalid-record case.</summary>
    private static byte[] CorruptDocument(string rootPath, string corruption)
    {
        var root = ValidDocument(rootPath);
        var entries = root["entries"]!.AsArray();
        var entry = entries[0]!.AsObject();

        switch (corruption)
        {
            case "malformed": return "{broken"u8.ToArray();
            case "null-root": return "null"u8.ToArray();
            case "missing-version": root.Remove("version"); break;
            case "missing-revision": root.Remove("revision"); break;
            case "empty-revision": root["revision"] = Guid.Empty; break;
            case "missing-entries": root.Remove("entries"); break;
            case "null-entries": root["entries"] = null; break;
            case "null-entry": entries[0] = null; break;
            case "missing-source": entry.Remove("sourcePath"); break;
            case "null-source": entry["sourcePath"] = null; break;
            case "relative-source": entry["sourcePath"] = "relative.mkv"; break;
            case "missing-destination": entry.Remove("destinationPath"); break;
            case "missing-included": entry.Remove("included"); break;
            case "missing-origin": entry.Remove("folderOrigin"); break;
            case "null-origin-root": entry["folderOrigin"]!["rootPath"] = null; break;
            case "relative-traversal": entry["folderOrigin"]!["relativePath"] = Path.Combine("..", "escape.mkv"); break;
            case "mismatched-origin": entry["folderOrigin"]!["relativePath"] = "other.mkv"; break;
            case "escaped-output": entry["destinationPath"] = Path.Combine(rootPath, "escape.mkv"); break;
            case "missing-output-root": entry.Remove("outputDirectoryRoot"); break;
            case "unknown-job": entry["approvedPlan"] = new JsonObject(); break;
            case "duplicate-source": entries.Add(entry.DeepClone()); break;
            case "duplicate-property": return Encoding.UTF8.GetBytes(root.ToJsonString().Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal));
            case "invalid-property-unicode": return Encoding.UTF8.GetBytes(root.ToJsonString().Replace("\"included\":true", "\"included\":true,\"\\uD800\":0", StringComparison.Ordinal));
            case "invalid-path-unicode": return Encoding.UTF8.GetBytes(root.ToJsonString().Replace("missing file.mkv", "missing\\uD800.mkv", StringComparison.Ordinal));
            case "invalid-utf8": return [0x7B, 0xFF, 0x7D];
            default: throw new ArgumentOutOfRangeException(nameof(corruption));
        }

        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    /// <summary>Creates a fresh actual persistence boundary over the owned test directory.</summary>
    private static PortableQueueListStore Store(TestWorkspace workspace) => new(workspace.Path);

    /// <summary>Resolves only this test's fixed listing record.</summary>
    private static string RecordPath(TestWorkspace workspace) => Path.Combine(workspace.Path, PortableQueueListStore.RecordFileName);

    /// <summary>Reads the previous complete bytes independently for preservation assertions.</summary>
    private static Task<byte[]> ReadBytesAsync(TestWorkspace workspace) => File.ReadAllBytesAsync(RecordPath(workspace), TestContext.Current.CancellationToken);

    /// <summary>Checks that successful or normally handled failures leave no operation-owned temporary file.</summary>
    private static void AssertNoTemporaryFiles(string directory) => Assert.Empty(Directory.GetFiles(directory, ".queue-list.*.tmp"));
}
