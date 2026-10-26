using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using MovieSift.Application.Ports;
using MovieSift.Core.Media;
using MovieSift.Core.Planning;
using MovieSift.Infrastructure.Files;
using MovieSift.Infrastructure.Persistence;
using Xunit;

namespace MovieSift.Application.Tests;

/// <summary>Checks real local publication, alias detection and bounded portable records.</summary>
public sealed class PortableFileTests : IDisposable
{
    private const uint FifoOwnerReadWritePermissions = 0x180;
    private const int LinuxReadWrite = 2;
    private const int LinuxNonBlocking = 0x800;
    private const int LinuxCloseOnExec = 0x80000;
    private static readonly TimeSpan FifoReadTimeout = TimeSpan.FromSeconds(5);
    private readonly string root = Path.Combine(Path.GetTempPath(), $"MovieSift tests Кириллица {Guid.NewGuid():N}");
    public static bool IsLinux => OperatingSystem.IsLinux();

    /// <summary>Creates an isolated directory for real filesystem checks.</summary>
    public PortableFileTests() => Directory.CreateDirectory(root);

    /// <summary>Checks hard-link identity rather than relying on different path strings.</summary>
    [Fact]
    public async Task HardLinkHasSameSourceIdentityAndCannotBeDestination()
    {
        var source = Path.Combine(root, "source.mkv");
        var alias = Path.Combine(root, "alias.mkv");
        await File.WriteAllTextAsync(source, "original", TestContext.Current.CancellationToken);

        if (OperatingSystem.IsWindows())
        {
            Assert.True(CreateHardLink(alias, source, IntPtr.Zero));
        }

        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(0, Link(source, alias));
        }

        var access = new FileSourceAccess();
        var original = await access.ReadRevisionAsync(new(source));
        var linked = await access.ReadRevisionAsync(new(alias));
        Assert.Equal(original.FileIdentity, linked.FileIdentity);
        var failure = await Assert.ThrowsAsync<OperationFailureException>(() => access.ValidateDestinationAsync(new(original, alias, original.Length)));
        Assert.Equal(Core.Failures.ErrorCode.InvalidDestination, failure.Failure.Code);
        Assert.Equal("original", await File.ReadAllTextAsync(source, TestContext.Current.CancellationToken));
    }

    /// <summary>Rejects a Linux FIFO before a blocking read-only open can occur.</summary>
    [Fact(SkipUnless = nameof(IsLinux), Skip = "The FIFO regression requires Linux.")]
    public async Task LinuxFifoIsRejectedWithoutOpeningAReader()
    {
        const uint OwnerReadWrite = 0x180;
        var fifo = Path.Combine(root, "not-regular.mkv");
        Assert.Equal(0, MakeFifo(fifo, OwnerReadWrite));
        var access = new FileSourceAccess();
        var failure = await Assert.ThrowsAsync<OperationFailureException>(() => access.ReadRevisionAsync(new(fifo)));
        Assert.Equal(Core.Failures.ErrorCode.InvalidSource, failure.Failure.Code);
    }

    /// <summary>Rejects FIFO ownership evidence without blocking discovery or authorizing deletion.</summary>
    [Theory(SkipUnless = nameof(IsLinux), Skip = "The ownership FIFO regressions require Linux.")]
    [InlineData("record")]
    [InlineData("marker")]
    public async Task LinuxFifoOwnershipEvidenceCannotBlockOrAuthorizeCleanup(string evidenceKind)
    {
        var request = await CreateOutputAsync();
        var recordPath = Path.Combine(root, "data", "ownership", $"{request.Output.JobId:N}.json");
        var markerPath = Path.Combine(request.Output.DirectoryPath, "owner.json");
        var fifoPath = evidenceKind == "record" ? recordPath : markerPath;
        var retainedEvidencePath = evidenceKind == "record" ? markerPath : recordPath;
        var cancellationToken = TestContext.Current.CancellationToken;
        var retainedEvidence = await File.ReadAllTextAsync(retainedEvidencePath, cancellationToken);
        await File.WriteAllTextAsync(request.Output.CandidatePath, "candidate evidence", cancellationToken);
        File.Delete(fifoPath);
        Assert.Equal(0, MakeFifo(fifoPath, FifoOwnerReadWritePermissions));
        var discovery = Task.Run(() => request.Store.FindInterruptedAsync(cancellationToken), CancellationToken.None);

        try
        {
            var entry = Assert.Single(await discovery.WaitAsync(FifoReadTimeout, cancellationToken));
            Assert.Equal("InvalidRecord", entry.Stage);
            Assert.False(entry.CanClean);
            Assert.Null(entry.Output);
            Assert.Equal(recordPath, entry.Issue);
        }
        finally
        {
            if (!discovery.IsCompleted)
            {
                await ReleaseBlockedFifoReadAsync(new(fifoPath, discovery));
            }
        }

        var cleanup = Task.Run(() => request.Store.CleanupAsync(new(request.Output)), CancellationToken.None);

        try
        {
            Assert.Single(await cleanup.WaitAsync(FifoReadTimeout, cancellationToken));
        }
        finally
        {
            if (!cleanup.IsCompleted)
            {
                await ReleaseBlockedFifoReadAsync(new(fifoPath, cleanup));
            }
        }

        Assert.True(File.Exists(fifoPath));
        Assert.Equal(retainedEvidence, await File.ReadAllTextAsync(retainedEvidencePath, cancellationToken));
        Assert.Equal("candidate evidence", await File.ReadAllTextAsync(request.Output.CandidatePath, cancellationToken));
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(root, "source.mkv"), cancellationToken));
    }

    /// <summary>Uses English for non-regular settings without blocking startup or deleting the FIFO.</summary>
    [Fact(SkipUnless = nameof(IsLinux), Skip = "The settings FIFO regression requires Linux.")]
    public async Task LinuxFifoSettingsFallBackWithoutBlockingStartup()
    {
        var dataDirectory = Path.Combine(root, "data");
        Directory.CreateDirectory(dataDirectory);
        var fifoPath = Path.Combine(dataDirectory, "settings.json");
        Assert.Equal(0, MakeFifo(fifoPath, FifoOwnerReadWritePermissions));
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new PortableSettingsStore(dataDirectory);
        var loading = Task.Run(() => store.LoadLanguageAsync(cancellationToken), CancellationToken.None);

        try
        {
            Assert.Equal("en", await loading.WaitAsync(FifoReadTimeout, cancellationToken));
        }
        finally
        {
            if (!loading.IsCompleted)
            {
                await ReleaseBlockedFifoReadAsync(new(fifoPath, loading));
            }
        }

        Assert.True(File.Exists(fifoPath));
    }

    /// <summary>Enforces a collision appearing after reservation at the actual move operation.</summary>
    [Fact]
    public async Task LateCollisionLeavesBothExistingDestinationAndSourceUnchanged()
    {
        var request = await CreateOutputAsync();
        await File.WriteAllTextAsync(request.Output.CandidatePath, "candidate", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(request.Output.DestinationPath, "pre-existing", TestContext.Current.CancellationToken);
        await request.Store.MarkPublishingAsync(new(request.Output));
        var failure = await Assert.ThrowsAsync<OperationFailureException>(() => request.Store.PublishAsync(new(request.Output)));
        Assert.Equal(Core.Failures.ErrorCode.DestinationExists, failure.Failure.Code);
        Assert.Equal("pre-existing", await File.ReadAllTextAsync(request.Output.DestinationPath, TestContext.Current.CancellationToken));
        Assert.Equal("candidate", await File.ReadAllTextAsync(request.Output.CandidatePath, TestContext.Current.CancellationToken));
    }

    /// <summary>Never deletes the final file during post-publication cleanup.</summary>
    [Fact]
    public async Task SuccessfulPublicationSurvivesOwnedCleanup()
    {
        var request = await CreateOutputAsync();
        await File.WriteAllTextAsync(request.Output.CandidatePath, "retained", TestContext.Current.CancellationToken);
        await request.Store.MarkPublishingAsync(new(request.Output));
        var publication = await request.Store.PublishAsync(new(request.Output));
        var issues = await request.Store.CleanupAsync(new(request.Output));
        Assert.Empty(issues);
        Assert.Equal(8, publication.Length);
        Assert.Equal("retained", await File.ReadAllTextAsync(publication.Path, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(request.Output.DirectoryPath));
    }

    /// <summary>Reserves an explicitly typed MP4 candidate and leaves the published media intact after cleanup.</summary>
    [Fact]
    public async Task Mp4PublicationUsesTypedCandidateAndRecord()
    {
        var request = await CreateOutputAsync(MediaContainer.Mp4);
        var recordPath = Path.Combine(root, "data", "ownership", $"{request.Output.JobId:N}.json");
        var record = JsonNode.Parse(await File.ReadAllTextAsync(recordPath, TestContext.Current.CancellationToken))!;

        Assert.Equal(MediaContainer.Mp4, request.Output.Container);
        Assert.Equal("candidate.mp4", Path.GetFileName(request.Output.CandidatePath));
        Assert.Equal(3, record["Version"]!.GetValue<int>());
        Assert.Equal((int)MediaContainer.Mp4, record["Container"]!.GetValue<int>());
        await File.WriteAllTextAsync(request.Output.CandidatePath, "retained mp4", TestContext.Current.CancellationToken);
        await request.Store.MarkPublishingAsync(new(request.Output));
        var published = await request.Store.PublishAsync(new(request.Output));

        Assert.Empty(await request.Store.CleanupAsync(new(request.Output)));
        Assert.Equal("retained mp4", await File.ReadAllTextAsync(published.Path, TestContext.Current.CancellationToken));
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(root, "source.mp4"), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(recordPath));
    }

    /// <summary>Authentic version-one MKV records retain their existing conservative cleanup behavior.</summary>
    [Fact]
    public async Task LegacyMkvJournalRemainsReadableWithoutAnExplicitContainer()
    {
        var request = await CreateOutputAsync();
        var recordPath = Path.Combine(root, "data", "ownership", $"{request.Output.JobId:N}.json");
        var record = JsonNode.Parse(await File.ReadAllTextAsync(recordPath, TestContext.Current.CancellationToken))!.AsObject();
        record["Version"] = 1;
        record.Remove("Container");
        record["Output"]!.AsObject().Remove("Container");
        await File.WriteAllTextAsync(recordPath, record.ToJsonString(), TestContext.Current.CancellationToken);

        var interrupted = Assert.Single(await request.Store.FindInterruptedAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Reserved", interrupted.Stage);
        Assert.Equal(request.Output, interrupted.Output);
        Assert.False(interrupted.CanClean);
        Assert.Empty(await request.Store.CleanupAsync(new(request.Output)));
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(root, "source.mkv"), TestContext.Current.CancellationToken));
    }

    /// <summary>A legacy version number cannot authorize a new MP4 ownership layout.</summary>
    [Fact]
    public async Task LegacyRecordCannotAuthorizeMp4CandidateCleanup()
    {
        var request = await CreateOutputAsync(MediaContainer.Mp4);
        var recordPath = Path.Combine(root, "data", "ownership", $"{request.Output.JobId:N}.json");
        var record = JsonNode.Parse(await File.ReadAllTextAsync(recordPath, TestContext.Current.CancellationToken))!.AsObject();
        record["Version"] = 1;
        record.Remove("Container");
        await File.WriteAllTextAsync(recordPath, record.ToJsonString(), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(request.Output.CandidatePath, "candidate evidence", TestContext.Current.CancellationToken);

        Assert.Equal("InvalidRecord", Assert.Single(await request.Store.FindInterruptedAsync(TestContext.Current.CancellationToken)).Stage);
        Assert.Single(await request.Store.CleanupAsync(new(request.Output)));
        Assert.Equal("candidate evidence", await File.ReadAllTextAsync(request.Output.CandidatePath, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(recordPath));
    }

    /// <summary>Refuses cleanup when unknown contents appear in a private job directory.</summary>
    [Fact]
    public async Task UnexpectedFilesPreventCleanup()
    {
        var request = await CreateOutputAsync();
        var unknown = Path.Combine(request.Output.DirectoryPath, "unrelated.txt");
        await File.WriteAllTextAsync(unknown, "preserve", TestContext.Current.CancellationToken);
        Assert.Single(await request.Store.CleanupAsync(new(request.Output)));
        Assert.Equal("preserve", await File.ReadAllTextAsync(unknown, TestContext.Current.CancellationToken));
    }

    /// <summary>Does not authorize recovered-candidate deletion merely because its app owner died.</summary>
    [Fact]
    public async Task DeadParentDoesNotAuthorizeCandidateCleanup()
    {
        var request = await CreateOutputAsync();
        await File.WriteAllTextAsync(request.Output.CandidatePath, "may still be written", TestContext.Current.CancellationToken);
        var recordPath = Path.Combine(root, "data", "ownership", $"{request.Output.JobId:N}.json");
        var record = JsonNode.Parse(await File.ReadAllTextAsync(recordPath, TestContext.Current.CancellationToken))!;
        record["OwnerProcessId"] = int.MaxValue;
        await File.WriteAllTextAsync(recordPath, record.ToJsonString(), TestContext.Current.CancellationToken);
        var entries = await request.Store.FindInterruptedAsync(TestContext.Current.CancellationToken);
        Assert.False(Assert.Single(entries).CanClean);
        Assert.Single(await request.Store.CleanupAsync(new(request.Output)));
        Assert.True(File.Exists(request.Output.CandidatePath));
    }

    /// <summary>Rejects malformed and incomplete ownership records without deleting any evidence.</summary>
    [Theory]
    [InlineData("$", "{bad json")]
    [InlineData("$", "null")]
    [InlineData("Version", null)]
    [InlineData("Version", "99")]
    [InlineData("Output.OutputMode", "99")]
    [InlineData("Output.OutputMode", "1")]
    [InlineData("Container", null)]
    [InlineData("Container", "null")]
    [InlineData("Container", "99")]
    [InlineData("Container", "1")]
    [InlineData("Output.Container", "99")]
    [InlineData("Output.Container", "1")]
    [InlineData("Output", null)]
    [InlineData("Output", "null")]
    [InlineData("Output.JobId", null)]
    [InlineData("Output.JobId", "\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("Output.DirectoryPath", null)]
    [InlineData("Output.DirectoryPath", "null")]
    [InlineData("Output.DirectoryPath", "\"relative-directory\"")]
    [InlineData("Output.DirectoryPath", "\"\\u0000\"")]
    [InlineData("Output.CandidatePath", null)]
    [InlineData("Output.CandidatePath", "null")]
    [InlineData("Output.CandidatePath", "\"relative-candidate.mkv\"")]
    [InlineData("Output.DestinationPath", null)]
    [InlineData("Output.DestinationPath", "null")]
    [InlineData("Output.DestinationPath", "\"relative-destination.mkv\"")]
    [InlineData("Output.OwnershipToken", null)]
    [InlineData("Output.OwnershipToken", "null")]
    [InlineData("Output.OwnershipToken", "\"invalid\"")]
    [InlineData("Stage", null)]
    [InlineData("Stage", "null")]
    [InlineData("Stage", "\"Unknown\"")]
    [InlineData("OwnerProcessId", null)]
    [InlineData("OwnerProcessId", "0")]
    [InlineData("OwnerProcessId", "-1")]
    [InlineData("OwnerStartedUtcTicks", null)]
    [InlineData("OwnerStartedUtcTicks", "0")]
    [InlineData("OwnerStartedUtcTicks", "-1")]
    [InlineData("OwnerStartedUtcTicks", "3155378976000000000")]
    public async Task CorruptRecordRemainsVisibleAndCannotAuthorizeCleanup(string memberPath, string? replacementJson)
    {
        var request = await CreateOutputAsync();
        var recordPath = Path.Combine(root, "data", "ownership", $"{request.Output.JobId:N}.json");
        var markerPath = Path.Combine(request.Output.DirectoryPath, "owner.json");
        var originalMarker = await File.ReadAllTextAsync(markerPath, TestContext.Current.CancellationToken);
        var originalRecord = await File.ReadAllTextAsync(recordPath, TestContext.Current.CancellationToken);
        var corruptedRecord = CorruptRecordJson(new(originalRecord, memberPath, replacementJson));
        await File.WriteAllTextAsync(recordPath, corruptedRecord, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(request.Output.CandidatePath, "candidate evidence", TestContext.Current.CancellationToken);
        var entry = Assert.Single(await request.Store.FindInterruptedAsync(TestContext.Current.CancellationToken));
        var cleanupIssues = await request.Store.CleanupAsync(new(request.Output));

        Assert.Equal("InvalidRecord", entry.Stage);
        Assert.False(entry.CanClean);
        Assert.Null(entry.Output);
        Assert.Equal(recordPath, entry.Issue);
        Assert.Single(cleanupIssues);
        Assert.Equal(corruptedRecord, await File.ReadAllTextAsync(recordPath, TestContext.Current.CancellationToken));
        Assert.Equal(originalMarker, await File.ReadAllTextAsync(markerPath, TestContext.Current.CancellationToken));
        Assert.Equal("candidate evidence", await File.ReadAllTextAsync(request.Output.CandidatePath, TestContext.Current.CancellationToken));
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(root, "source.mkv"), TestContext.Current.CancellationToken));
    }

    /// <summary>Does not remove an empty reservation when its process identity is incomplete.</summary>
    [Theory]
    [InlineData("OwnerProcessId")]
    [InlineData("OwnerStartedUtcTicks")]
    public async Task MissingOwnerIdentityCannotAuthorizeEmptyDirectoryCleanup(string memberPath)
    {
        var request = await CreateOutputAsync();
        var recordPath = Path.Combine(root, "data", "ownership", $"{request.Output.JobId:N}.json");
        var originalRecord = await File.ReadAllTextAsync(recordPath, TestContext.Current.CancellationToken);
        var corruptedRecord = CorruptRecordJson(new(originalRecord, memberPath, null));
        await File.WriteAllTextAsync(recordPath, corruptedRecord, TestContext.Current.CancellationToken);
        var entry = Assert.Single(await request.Store.FindInterruptedAsync(TestContext.Current.CancellationToken));
        var cleanupIssues = await request.Store.CleanupAsync(new(request.Output));

        Assert.Equal("InvalidRecord", entry.Stage);
        Assert.False(entry.CanClean);
        Assert.Single(cleanupIssues);
        Assert.Equal(corruptedRecord, await File.ReadAllTextAsync(recordPath, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Path.Combine(request.Output.DirectoryPath, "owner.json")));
    }

    /// <summary>Rejects record filenames that disagree with their embedded operation identifier.</summary>
    [Fact]
    public async Task RenamedOwnershipRecordCannotAuthorizeCleanup()
    {
        var request = await CreateOutputAsync();
        var originalPath = Path.Combine(root, "data", "ownership", $"{request.Output.JobId:N}.json");
        var renamedPath = Path.Combine(root, "data", "ownership", $"{Guid.NewGuid():N}.json");
        var originalRecord = await File.ReadAllTextAsync(originalPath, TestContext.Current.CancellationToken);
        File.Move(originalPath, renamedPath);
        var entry = Assert.Single(await request.Store.FindInterruptedAsync(TestContext.Current.CancellationToken));

        Assert.Equal("InvalidRecord", entry.Stage);
        Assert.False(entry.CanClean);
        Assert.Null(entry.Output);
        Assert.Equal(renamedPath, entry.Issue);
        Assert.Single(await request.Store.CleanupAsync(new(request.Output)));
        Assert.Equal(originalRecord, await File.ReadAllTextAsync(renamedPath, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Path.Combine(request.Output.DirectoryPath, "owner.json")));
    }

    /// <summary>Preserves both files when a marker is malformed or lacks its matching identity.</summary>
    [Theory]
    [InlineData("$", "{bad json")]
    [InlineData("$", "null")]
    [InlineData("JobId", null)]
    [InlineData("OwnershipToken", null)]
    [InlineData("OwnershipToken", "null")]
    public async Task InvalidMarkerCannotAuthorizeCleanup(string memberPath, string? replacementJson)
    {
        var request = await CreateOutputAsync();
        var recordPath = Path.Combine(root, "data", "ownership", $"{request.Output.JobId:N}.json");
        var markerPath = Path.Combine(request.Output.DirectoryPath, "owner.json");
        var originalRecord = await File.ReadAllTextAsync(recordPath, TestContext.Current.CancellationToken);
        var originalMarker = await File.ReadAllTextAsync(markerPath, TestContext.Current.CancellationToken);
        var corruptedMarker = CorruptRecordJson(new(originalMarker, memberPath, replacementJson));
        await File.WriteAllTextAsync(markerPath, corruptedMarker, TestContext.Current.CancellationToken);
        var entry = Assert.Single(await request.Store.FindInterruptedAsync(TestContext.Current.CancellationToken));

        Assert.Equal("InvalidRecord", entry.Stage);
        Assert.False(entry.CanClean);
        Assert.Null(entry.Output);
        Assert.Equal(recordPath, entry.Issue);
        Assert.Single(await request.Store.CleanupAsync(new(request.Output)));
        Assert.Equal(originalRecord, await File.ReadAllTextAsync(recordPath, TestContext.Current.CancellationToken));
        Assert.Equal(corruptedMarker, await File.ReadAllTextAsync(markerPath, TestContext.Current.CancellationToken));
    }

    /// <summary>Requires a real hexadecimal ownership token even when both persisted files agree.</summary>
    [Fact]
    public async Task MatchingMalformedTokenCannotAuthorizeCleanup()
    {
        var request = await CreateOutputAsync();
        const int TokenCharacters = 64;
        var malformedOutput = request.Output with { OwnershipToken = new string('Z', TokenCharacters) };
        var replacementJson = $"\"{malformedOutput.OwnershipToken}\"";
        var recordPath = Path.Combine(root, "data", "ownership", $"{request.Output.JobId:N}.json");
        var markerPath = Path.Combine(request.Output.DirectoryPath, "owner.json");
        var originalRecord = await File.ReadAllTextAsync(recordPath, TestContext.Current.CancellationToken);
        var originalMarker = await File.ReadAllTextAsync(markerPath, TestContext.Current.CancellationToken);
        var corruptedRecord = CorruptRecordJson(new(originalRecord, "Output.OwnershipToken", replacementJson));
        var corruptedMarker = CorruptRecordJson(new(originalMarker, "OwnershipToken", replacementJson));
        await File.WriteAllTextAsync(recordPath, corruptedRecord, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(markerPath, corruptedMarker, TestContext.Current.CancellationToken);
        var entry = Assert.Single(await request.Store.FindInterruptedAsync(TestContext.Current.CancellationToken));

        Assert.Equal("InvalidRecord", entry.Stage);
        Assert.False(entry.CanClean);
        Assert.Single(await request.Store.CleanupAsync(new(malformedOutput)));
        Assert.Equal(corruptedRecord, await File.ReadAllTextAsync(recordPath, TestContext.Current.CancellationToken));
        Assert.Equal(corruptedMarker, await File.ReadAllTextAsync(markerPath, TestContext.Current.CancellationToken));
    }

    /// <summary>Uses English on first launch under Russian culture and persists an explicit switch.</summary>
    [Fact]
    public async Task PortableLanguageDefaultsAndPersistenceAreExplicit()
    {
        var originalCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru-RU");
            var store = new PortableSettingsStore(Path.Combine(root, "data"));
            Assert.Equal("en", await store.LoadLanguageAsync(TestContext.Current.CancellationToken));
            await store.SaveLanguageAsync(new("ru"));
            Assert.Equal("ru", await new PortableSettingsStore(Path.Combine(root, "data")).LoadLanguageAsync(TestContext.Current.CancellationToken));
            await File.WriteAllTextAsync(Path.Combine(root, "data", "settings.json"), "{\"Version\":1,\"Language\":\"invalid\"}", TestContext.Current.CancellationToken);
            Assert.Equal("en", await store.LoadLanguageAsync(TestContext.Current.CancellationToken));
            await File.WriteAllTextAsync(Path.Combine(root, "data", "settings.json"), "invalid JSON", TestContext.Current.CancellationToken);
            Assert.Equal("en", await store.LoadLanguageAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    /// <summary>Creates one real owned output adjacent to its final destination.</summary>
    private async Task<OutputFixture> CreateOutputAsync(MediaContainer container = MediaContainer.Matroska)
    {
        var extension = MediaContainerPolicy.Extension(container);
        var source = Path.Combine(root, "source" + extension);
        await File.WriteAllTextAsync(source, "original", TestContext.Current.CancellationToken);
        var revision = await new FileSourceAccess().ReadRevisionAsync(new(source));
        var tracks = ImmutableArray.Create(new MediaTrack(0, 1, TrackKind.Video, "video", "und", null, "", null,
            true, false, true, ImmutableDictionary<string, string>.Empty));
        var inspection = new MediaInspection(revision, new("mkvmerge", "102.0", 20), tracks, [], "", false, false, container);
        var plan = new RemuxPlan(Guid.NewGuid(), inspection, tracks, Path.Combine(root, "output" + extension), true);
        var store = new OwnedOutputStore(Path.Combine(root, "data"));
        return new(store, await store.ReserveAsync(new(plan)));
    }

    /// <summary>Changes one persisted field while preserving an otherwise authentic reservation.</summary>
    private static string CorruptRecordJson(RecordCorruption request)
    {
        if (request.MemberPath == "$")
        {
            return request.ReplacementJson!;
        }

        var record = JsonNode.Parse(request.OriginalJson)!.AsObject();
        var members = request.MemberPath.Split('.');
        var parent = members.Length == 1 ? record : record["Output"]!.AsObject();

        if (request.ReplacementJson is null)
        {
            parent.Remove(members[^1]);
            return record.ToJsonString();
        }

        parent[members[^1]] = JsonNode.Parse(request.ReplacementJson);
        return record.ToJsonString();
    }

    /// <summary>Releases a regressed blocking open and observes its worker before removing the test directory.</summary>
    private static async Task ReleaseBlockedFifoReadAsync(BlockedFifoRead request)
    {
        // The test-only O_RDWR descriptor also works when the queued reader has not entered open() yet.
        var descriptor = OpenFifo(request.Path, LinuxReadWrite | LinuxNonBlocking | LinuxCloseOnExec);
        Assert.True(descriptor >= 0, $"Could not release the blocked record reader (OS error {Marshal.GetLastPInvokeError()}).");
        using var releaseHandle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        var failure = await Record.ExceptionAsync(() => request.Worker.WaitAsync(FifoReadTimeout, CancellationToken.None));
        Assert.IsNotType<TimeoutException>(failure);
        Assert.True(request.Worker.IsCompleted, "The record reader must be observed before test teardown.");
    }

    /// <summary>Removes only this test's unique temporary directory.</summary>
    public void Dispose() => Directory.Delete(root, recursive: true);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string sourcePath, string linkPath);

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    private sealed record OutputFixture(OwnedOutputStore Store, OwnedOutput Output);
    private sealed record RecordCorruption(string OriginalJson, string MemberPath, string? ReplacementJson);
    private sealed record BlockedFifoRead(string Path, Task Worker);
}
