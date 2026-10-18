using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using TrackTrim.Application.Ports;
using TrackTrim.Core.Media;
using TrackTrim.Core.Planning;
using TrackTrim.Infrastructure.Files;
using TrackTrim.Infrastructure.Persistence;
using Xunit;

namespace TrackTrim.Application.Tests;

/// <summary>Checks real local publication, alias detection and bounded portable records.</summary>
public sealed class PortableFileTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"TrackTrim tests Кириллица {Guid.NewGuid():N}");
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
        var failure = await Assert.ThrowsAsync<OperationFailureException>(() => access.ValidateDestinationAsync(new(original, alias)));
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

    /// <summary>Reports corrupt ownership records without authorizing any deletion.</summary>
    [Fact]
    public async Task CorruptRecordRemainsVisibleAndCannotAuthorizeCleanup()
    {
        var request = await CreateOutputAsync();
        var recordPath = Path.Combine(root, "data", "ownership", $"{request.Output.JobId:N}.json");
        await File.WriteAllTextAsync(recordPath, "{bad json", TestContext.Current.CancellationToken);
        var entry = Assert.Single(await request.Store.FindInterruptedAsync(TestContext.Current.CancellationToken));
        Assert.False(entry.CanClean);
        Assert.Null(entry.Output);
        Assert.True(File.Exists(recordPath));
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
    private async Task<OutputFixture> CreateOutputAsync()
    {
        var source = Path.Combine(root, "source.mkv");
        await File.WriteAllTextAsync(source, "original", TestContext.Current.CancellationToken);
        var revision = await new FileSourceAccess().ReadRevisionAsync(new(source));
        var tracks = ImmutableArray.Create(new MediaTrack(0, 1, TrackKind.Video, "video", "und", null, "", null,
            true, false, true, ImmutableDictionary<string, string>.Empty));
        var inspection = new MediaInspection(revision, new("mkvmerge", "102.0", 20), tracks, [], "", false, false);
        var plan = new RemuxPlan(Guid.NewGuid(), inspection, tracks, Path.Combine(root, "output.mkv"), true);
        var store = new OwnedOutputStore(Path.Combine(root, "data"));
        return new(store, await store.ReserveAsync(new(plan)));
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

    private sealed record OutputFixture(OwnedOutputStore Store, OwnedOutput Output);
}
