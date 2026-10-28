using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using MovieSift.Application.Localization;
using MovieSift.Infrastructure.Localization;
using MovieSift.Infrastructure.Persistence;
using Xunit;

namespace MovieSift.Infrastructure.Tests;

/// <summary>Uses controlled HTTP and a real portable directory, never live GitHub or a media worker.</summary>
public sealed class TranslationPackStoreTests : IDisposable
{
    private const string Revision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "MovieSift translation tests " + Guid.NewGuid().ToString("N"));
    private static readonly IReadOnlyDictionary<string, string> English = new Dictionary<string, string>
    {
        ["Title"] = "Keep tracks",
        ["Count"] = "{0:N0} files · {1}"
    };
    public static bool IsLinux => OperatingSystem.IsLinux();

    /// <summary>Exercises real complete shipped-source packs through download, canonical cache serialization and a fresh offline store.</summary>
    [Theory]
    [InlineData("ru")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("pt-BR")]
    [InlineData("uk")]
    [InlineData("pl")]
    [InlineData("tr")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("zh-Hans")]
    public async Task CompleteRepositoryPacksRoundtripThroughFreshOfflineStore(string locale)
    {
        var repository = RepositoryDirectory();
        using var resourceReader = XmlReader.Create(Path.Combine(repository, "src", "MovieSift.Desktop", "Localization", "Strings.resx"),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var english = XDocument.Load(resourceReader).Root!.Elements("data").ToDictionary(
            element => element.Attribute("name")!.Value, element => element.Element("value")!.Value, StringComparer.Ordinal);
        var payload = await File.ReadAllBytesAsync(Path.Combine(repository, "translations", locale + ".json"), TestContext.Current.CancellationToken);
        var catalog = await File.ReadAllBytesAsync(Path.Combine(repository, "translations", "catalog.json"), TestContext.Current.CancellationToken);
        using var source = JsonDocument.Parse(payload);
        var expected = source.RootElement.GetProperty("messages").EnumerateObject().ToDictionary(
            property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal);
        using var client = Client((request, _) => Task.FromResult(Response(request,
            request.RequestUri!.Host == "api.github.com"
                ? JsonSerializer.SerializeToUtf8Bytes(new { @object = new { type = "commit", sha = Revision } })
                : request.RequestUri.AbsolutePath.EndsWith("catalog.json", StringComparison.Ordinal) ? catalog : payload)));
        var downloaded = await new GitHubTranslationPackStore(client, directory, english).DownloadAsync(locale,
            cancellationToken: TestContext.Current.CancellationToken);
        using var offline = Client((_, _) => throw new InvalidOperationException("Fresh store must not contact the network."));
        var restored = await new GitHubTranslationPackStore(offline, directory, english).LoadCachedAsync(locale, TestContext.Current.CancellationToken);
        Assert.NotNull(restored);
        Assert.Equal(locale, restored.Locale);
        Assert.Equal(expected.OrderBy(entry => entry.Key), downloaded.Messages.OrderBy(entry => entry.Key));
        Assert.Equal(expected.OrderBy(entry => entry.Key), restored.Messages.OrderBy(entry => entry.Key));
        const long maximumCacheBytes = 262_144;
        Assert.InRange(new FileInfo(Path.Combine(directory, "translations", locale + ".json")).Length, 1, maximumCacheBytes);
        Assert.Empty(Directory.GetFiles(Path.Combine(directory, "translations"), "*.tmp"));
    }

    /// <summary>Refuses redirected cache records and directories without changing the unrelated target.</summary>
    [Theory(SkipUnless = nameof(IsLinux), Skip = "Translation symlink regressions require Linux symlink support.")]
    [InlineData("record")]
    [InlineData("directory")]
    public async Task RedirectedCachePreservesUnrelatedTarget(string kind)
    {
        var cache = Path.Combine(directory, "translations");
        var target = Path.Combine(directory, "unrelated");
        Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "preserve.json");
        var original = Pack();
        await File.WriteAllBytesAsync(sentinel, original, TestContext.Current.CancellationToken);

        if (kind == "directory")
        {
            Directory.CreateSymbolicLink(cache, target);
        }

        if (kind == "record")
        {
            Directory.CreateDirectory(cache);
            File.CreateSymbolicLink(Path.Combine(cache, "ru.json"), sentinel);
        }

        using var client = Client((request, _) => Task.FromResult(Response(request, Body(request, Pack()))));
        var store = new GitHubTranslationPackStore(client, directory, English);
        await Assert.ThrowsAsync<TranslationPackException>(() => store.LoadCachedAsync("ru", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<TranslationPackException>(() => store.DownloadAsync("ru", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(original, await File.ReadAllBytesAsync(sentinel, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFileSystemEntries(target));
    }

    /// <summary>A missing symlink target does not make its redirected cache pathname safe to replace.</summary>
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Dangling translation symlink regression requires Linux symlink support.")]
    public async Task DanglingCacheLinkIsPreservedOnReadAndExplicitDownload()
    {
        var path = CachePath();
        var unrelated = Path.Combine(directory, "unrelated");
        Directory.CreateDirectory(unrelated);
        var sentinel = Path.Combine(unrelated, "preserve.txt");
        const string original = "unrelated original bytes";
        await File.WriteAllTextAsync(sentinel, original, TestContext.Current.CancellationToken);
        var missingTarget = Path.Combine(unrelated, "absent.json");
        File.CreateSymbolicLink(path, missingTarget);
        using var client = Client((request, _) => Task.FromResult(Response(request, Body(request, Pack()))));
        var store = new GitHubTranslationPackStore(client, directory, English);
        var readFailure = await Assert.ThrowsAsync<TranslationPackException>(() =>
            store.LoadCachedAsync("ru", TestContext.Current.CancellationToken));
        Assert.Equal(TranslationErrorCode.InvalidPack, readFailure.Code);
        var downloadFailure = await Assert.ThrowsAsync<TranslationPackException>(() =>
            store.DownloadAsync("ru", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(TranslationErrorCode.InvalidPack, downloadFailure.Code);
        Assert.Equal(missingTarget, new FileInfo(path).LinkTarget);
        Assert.False(File.Exists(missingTarget));
        Assert.Equal(original, await File.ReadAllTextAsync(sentinel, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFileSystemEntries(unrelated));
        Assert.Empty(Directory.GetFiles(Path.Combine(directory, "translations"), "*.tmp"));
    }

    /// <summary>Resolves one immutable snapshot, verifies the manifest and reloads the cache without network.</summary>
    [Fact]
    public async Task DownloadRoundtripIsBoundToSnapshotAndCacheIsOffline()
    {
        var calls = new List<string>();
        var bytes = Pack();
        using var client = Client((request, _) =>
        {
            calls.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Response(request, Body(request, bytes)));
        });
        var store = new GitHubTranslationPackStore(client, directory, English);
        var progress = new ProgressReceipt();
        var pack = await store.DownloadAsync("ru", progress, TestContext.Current.CancellationToken);
        Assert.Equal("Русский", pack.Messages["Title"]);
        Assert.Equal(3, calls.Count);
        Assert.Equal("https://api.github.com/repos/Nexusov/MovieSift/git/ref/heads/main", calls[0]);
        Assert.All(calls.Skip(1), address => Assert.Contains("/" + Revision + "/translations/", address));
        Assert.Equal(bytes.Length, progress.Last!.BytesReceived);
        using var offline = Client((_, _) => throw new InvalidOperationException("No network permitted."));
        var restored = await new GitHubTranslationPackStore(offline, directory, English).LoadCachedAsync("ru", TestContext.Current.CancellationToken);
        Assert.Equal(pack.Messages.OrderBy(entry => entry.Key), restored!.Messages.OrderBy(entry => entry.Key));
        Assert.Empty(Directory.GetFiles(Path.Combine(directory, "translations"), "*.tmp"));
    }

    /// <summary>Distinguishes an absent optional cache from corrupt user-visible cached data.</summary>
    [Fact]
    public async Task MissingCacheIsNullButCorruptionIsPreserved()
    {
        using var client = Client((_, _) => throw new InvalidOperationException("No network permitted."));
        var store = new GitHubTranslationPackStore(client, directory, English);
        Assert.Null(await store.LoadCachedAsync("ru", TestContext.Current.CancellationToken));
        var path = CachePath();
        await File.WriteAllTextAsync(path, "not json", TestContext.Current.CancellationToken);
        var failure = await Assert.ThrowsAsync<TranslationPackException>(() => store.LoadCachedAsync("ru", TestContext.Current.CancellationToken));
        Assert.Equal(TranslationErrorCode.InvalidPack, failure.Code);
        Assert.Equal("not json", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    /// <summary>Rejects complete-looking content whose placeholders, keys, locale or version differ.</summary>
    [Theory]
    [InlineData("{0:N0} files · {0}", "ru", 1)]
    [InlineData("{0} files · {1}", "ru", 1)]
    [InlineData("{0:N0} files · {1}", "de", 1)]
    [InlineData("{0:N0} files · {1}", "ru", 2)]
    public async Task InvalidDownloadedConfigurationDoesNotReplaceCache(string count, string locale, int version)
    {
        var path = CachePath();
        var previous = Pack();
        await File.WriteAllBytesAsync(path, previous, TestContext.Current.CancellationToken);
        var invalid = Pack(count, locale, version);
        using var client = Client((request, _) => Task.FromResult(Response(request, Body(request, invalid))));
        var failure = await Assert.ThrowsAsync<TranslationPackException>(() =>
            new GitHubTranslationPackStore(client, directory, English).DownloadAsync("ru", cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(failure.Code is TranslationErrorCode.InvalidPack or TranslationErrorCode.Incompatible);
        Assert.Equal(previous, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    /// <summary>Rejects truncated, oversized, duplicate and incomplete documents before changing the cached pack.</summary>
    [Theory]
    [InlineData("{\"version\":1,\"version\":1,\"locale\":\"ru\",\"messages\":{}}")]
    [InlineData("{\"version\":1,\"locale\":\"ru\",\"messages\":{\"Title\":\"Name\"}}")]
    [InlineData("{\"version\":1,\"locale\":\"ru\",\"messages\":{\"Title\":\"Name\",\"Title\":\"Name\",\"Count\":\"{0:N0} files · {1}\"}}")]
    public async Task RejectsMalformedSchema(string json)
    {
        using var client = Client((request, _) => Task.FromResult(Response(request, Body(request, Encoding.UTF8.GetBytes(json)))));
        await Assert.ThrowsAsync<TranslationPackException>(() =>
            new GitHubTranslationPackStore(client, directory, English).DownloadAsync("ru", cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(directory, "translations", "ru.json")));
    }

    /// <summary>Manifest mismatch and hostile path identifiers cannot be accepted as usable translations.</summary>
    [Fact]
    public async Task DigestMismatchAndUnknownLocalesAreRejected()
    {
        using var client = Client((request, _) => Task.FromResult(Response(request,
            request.RequestUri!.AbsolutePath.EndsWith("ru.json", StringComparison.Ordinal) ? Pack("different") : Body(request, Pack()))));
        var store = new GitHubTranslationPackStore(client, directory, English);
        var failure = await Assert.ThrowsAsync<TranslationPackException>(() => store.DownloadAsync("ru", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(TranslationErrorCode.InvalidPack, failure.Code);
        await Assert.ThrowsAsync<TranslationPackException>(() => store.DownloadAsync("../ru", cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(directory, "translations", "ru.json")));
    }

    /// <summary>Cancellation leaves the prior cache untouched and another instance gets an explicit busy result.</summary>
    [Fact]
    public async Task ConcurrentDownloadIsBusyAndCancellationPreservesCache()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = Client(async (_, cancellationToken) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        });
        var path = CachePath();
        var previous = Pack();
        await File.WriteAllBytesAsync(path, previous, TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = new GitHubTranslationPackStore(client, directory, English).DownloadAsync("ru", cancellationToken: cancellation.Token);
        await entered.Task;
        var busy = await Assert.ThrowsAsync<TranslationPackException>(() =>
            new GitHubTranslationPackStore(client, directory, English).DownloadAsync("ru", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(TranslationErrorCode.Busy, busy.Code);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(previous, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    /// <summary>Maps transport deadlines to a recoverable translation error rather than an unobserved command cancellation.</summary>
    [Fact]
    public async Task TransportTimeoutHasExplicitUnavailableOutcome()
    {
        using var client = Client((_, _) => throw new OperationCanceledException("Controlled transport timeout"));
        var failure = await Assert.ThrowsAsync<TranslationPackException>(() =>
            new GitHubTranslationPackStore(client, directory, English).DownloadAsync("ru", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(TranslationErrorCode.Unavailable, failure.Code);
        Assert.False(File.Exists(Path.Combine(directory, "translations", "ru.json")));
    }

    /// <summary>Rejects server redirection without following a user-controlled translation location.</summary>
    [Fact]
    public async Task RedirectResponseIsNotATranslation()
    {
        using var client = Client((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            RequestMessage = request,
            Headers = { Location = new Uri("https://example.invalid/translation.json") }
        }));
        var failure = await Assert.ThrowsAsync<TranslationPackException>(() =>
            new GitHubTranslationPackStore(client, directory, English).DownloadAsync("ru", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(TranslationErrorCode.InvalidPack, failure.Code);
    }

    /// <summary>Bounds local data and refuses a directory in place of the expected cache record.</summary>
    [Fact]
    public async Task OversizedAndNonregularCacheEntriesRemainUntouched()
    {
        using var client = Client((_, _) => throw new InvalidOperationException("No network permitted."));
        var store = new GitHubTranslationPackStore(client, directory, English);
        var path = CachePath();
        const int oversizedBytes = 262_145;
        await File.WriteAllBytesAsync(path, new byte[oversizedBytes], TestContext.Current.CancellationToken);
        var failure = await Assert.ThrowsAsync<TranslationPackException>(() => store.LoadCachedAsync("ru", TestContext.Current.CancellationToken));
        Assert.Equal(TranslationErrorCode.InvalidPack, failure.Code);
        Assert.Equal(oversizedBytes, new FileInfo(path).Length);
        File.Delete(path);
        Directory.CreateDirectory(path);
        await Assert.ThrowsAsync<TranslationPackException>(() => store.LoadCachedAsync("ru", TestContext.Current.CancellationToken));
        Assert.True(Directory.Exists(path));
    }

    /// <summary>Persists full region/script locale identities instead of reducing them to two-letter codes.</summary>
    [Theory]
    [InlineData("pt-BR")]
    [InlineData("zh-Hans")]
    [InlineData("ja")]
    public async Task SettingsPreserveKnownLocale(string locale)
    {
        var settings = new PortableSettingsStore(directory);
        await settings.SaveLanguageAsync(new(locale, TestContext.Current.CancellationToken));
        Assert.Equal(locale, await new PortableSettingsStore(directory).LoadLanguageAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Removes only this fixture's temporary test directory.</summary>
    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Creates the owned fixture cache parent and returns its canonical data-only file path.</summary>
    private string CachePath()
    {
        Directory.CreateDirectory(Path.Combine(directory, "translations"));
        return Path.Combine(directory, "translations", "ru.json");
    }

    /// <summary>Finds the checked-out source fixtures rather than substituting abbreviated test translations.</summary>
    private static string RepositoryDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "MovieSift.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("The source checkout containing translation fixtures is required.");
    }

    /// <summary>Serializes a small production-shaped pack with distinguishable translated content.</summary>
    private static byte[] Pack(string count = "{0:N0} files · {1}", string locale = "ru", int version = 1) =>
        JsonSerializer.SerializeToUtf8Bytes(new { version, locale, messages = new Dictionary<string, string> { ["Title"] = "Русский", ["Count"] = count } });

    /// <summary>Produces controlled immutable-snapshot API responses and matching manifests.</summary>
    private static byte[] Body(HttpRequestMessage request, byte[] pack)
    {
        if (request.RequestUri!.Host == "api.github.com")
        {
            return JsonSerializer.SerializeToUtf8Bytes(new { @object = new { type = "commit", sha = Revision } });
        }

        return request.RequestUri.AbsolutePath.EndsWith("catalog.json", StringComparison.Ordinal)
            ? JsonSerializer.SerializeToUtf8Bytes(new { version = 1, packs = new[] { new { locale = "ru", file = "ru.json", sha256 = Convert.ToHexStringLower(SHA256.HashData(pack)), bytes = pack.Length } } })
            : pack;
    }

    /// <summary>Retains the actual request identity so redirect rejection is exercised realistically.</summary>
    private static HttpResponseMessage Response(HttpRequestMessage request, byte[] bytes) => new(HttpStatusCode.OK)
    { RequestMessage = request, Content = new ByteArrayContent(bytes) };

    /// <summary>Creates an isolated controllable client with no real network access.</summary>
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) => new(new Handler(response));

    /// <summary>Forwards requests to deterministic per-test synchronization and responses.</summary>
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request, cancellationToken);
    }

    /// <summary>Records progress synchronously without racing the test assertion with a dispatcher.</summary>
    private sealed class ProgressReceipt : IProgress<TranslationDownloadProgress>
    {
        internal TranslationDownloadProgress? Last { get; private set; }
        public void Report(TranslationDownloadProgress value) => Last = value;
    }
}
