using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MovieSift.Application;
using MovieSift.Application.Localization;
using MovieSift.Infrastructure.Files;
using MovieSift.Infrastructure.Persistence;

namespace MovieSift.Infrastructure.Localization;

/// <summary>Fetches data-only translation packs from one immutable official repository snapshot and caches validated dictionaries.</summary>
public sealed partial class GitHubTranslationPackStore(HttpClient client, string dataDirectory,
    IReadOnlyDictionary<string, string> englishMessages) : ITranslationPackStore
{
    private const string CommitApi = "https://api.github.com/repos/Nexusov/MovieSift/git/ref/heads/main";
    private const string RawRoot = "https://raw.githubusercontent.com/Nexusov/MovieSift/";
    private const int MaximumReferenceBytes = 8_192;
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(30);
    private readonly string cacheDirectory = Path.Combine(Path.GetFullPath(dataDirectory), "translations");

    /// <summary>Reads only a bounded regular local file; startup never initiates a network request.</summary>
    public async Task<TranslationPack?> LoadCachedAsync(string locale, CancellationToken cancellationToken = default)
    {
        ValidateLocale(locale);
        var path = Path.Combine(cacheDirectory, locale + ".json");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateExistingAncestors(cacheDirectory);

            if (!ExistsWithoutRedirection(path))
            {
                return null;
            }

            await using var stream = FileSourceAccess.OpenRegularReadStream(path);
            var bytes = await ReadBoundedAsync(stream, TranslationPackJson.MaximumPackBytes, null, null, cancellationToken);
            return TranslationPackJson.Read(bytes, locale, englishMessages);
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            throw MapFailure(exception);
        }
    }

    /// <summary>Requires explicit download intent, rejects redirects and publishes only a complete compatible package.</summary>
    public async Task<TranslationPack> DownloadAsync(string locale, IProgress<TranslationDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateLocale(locale);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DownloadTimeout);

        try
        {
            ValidateExistingAncestors(cacheDirectory);
            Directory.CreateDirectory(cacheDirectory);
            PathSafety.RejectReparsePoints(cacheDirectory);
            using var exclusion = PortableRecordFileLock.Acquire(Path.Combine(cacheDirectory, "download.lock"));
            var revisionBytes = await GetAsync(new(CommitApi), MaximumReferenceBytes, null, timeout.Token);
            using var revisionJson = JsonDocument.Parse(revisionBytes, new() { MaxDepth = 16 });
            var target = revisionJson.RootElement.GetProperty("object");
            var revision = target.GetProperty("sha").GetString();

            if (target.GetProperty("type").GetString() != "commit" || revision is null || !CommitPattern().IsMatch(revision))
            {
                throw TranslationPackJson.Invalid("GitHub did not identify an immutable source revision.");
            }

            var root = RawRoot + revision + "/translations/";
            var catalog = await GetAsync(new(root + "catalog.json"), TranslationPackJson.MaximumCatalogBytes, null, timeout.Token);
            var expected = TranslationPackJson.ReadCatalog(catalog, locale);
            var bytes = await GetAsync(new(root + locale + ".json"), TranslationPackJson.MaximumPackBytes, progress, timeout.Token);

            if (bytes.LongLength != expected.Bytes || Convert.ToHexStringLower(SHA256.HashData(bytes)) != expected.Hash)
            {
                throw TranslationPackJson.Invalid("The translation bytes do not match their snapshot manifest.");
            }

            var pack = TranslationPackJson.Read(bytes, locale, englishMessages);
            await CacheAsync(pack, timeout.Token);
            return pack;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TranslationPackException(TranslationErrorCode.Unavailable, "The translation request timed out.", exception);
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            throw MapFailure(exception);
        }
    }

    /// <summary>Writes a bounded canonical record using the existing identity-checked temporary-file publisher.</summary>
    private async Task CacheAsync(TranslationPack pack, CancellationToken cancellationToken)
    {
        var path = Path.Combine(cacheDirectory, pack.Locale + ".json");
        var record = JsonSerializer.SerializeToElement(new { version = 1, locale = pack.Locale, messages = pack.Messages });
        var serialized = JsonSerializer.SerializeToUtf8Bytes(record, new JsonSerializerOptions { WriteIndented = true, MaxDepth = 16 });

        if (serialized.Length > TranslationPackJson.MaximumPackBytes)
        {
            throw TranslationPackJson.Invalid("The cached representation exceeds its byte budget.");
        }

        PathSafety.RejectReparsePoints(cacheDirectory);

        if (ExistsWithoutRedirection(path))
        {
            await using var existing = FileSourceAccess.OpenRegularReadStream(path);
        }

        await BoundedJson.WriteAsync(new JsonWriteRequest<JsonElement>(path, record, cancellationToken));
    }

    /// <summary>Uses fixed HTTPS endpoints, machine responses and bounded asynchronous draining without executable resources.</summary>
    private async Task<byte[]> GetAsync(Uri address, int maximumBytes, IProgress<TranslationDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.UserAgent.ParseAdd("MovieSift-Translations/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.RequestMessage?.RequestUri != address || (int)response.StatusCode is >= 300 and < 400)
        {
            throw TranslationPackJson.Invalid("Redirected translation downloads are not accepted.");
        }

        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;

        if (total > maximumBytes)
        {
            throw TranslationPackJson.Invalid("The translation response exceeds its byte budget.");
        }

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        var bytes = await ReadBoundedAsync(body, maximumBytes, total, progress, cancellationToken);

        if (total is not null && bytes.LongLength != total)
        {
            throw TranslationPackJson.Invalid("The translation response is incomplete.");
        }

        return bytes;
    }

    /// <summary>Stops reading once a bounded record is exceeded, including chunked or changing local data.</summary>
    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, long? total,
        IProgress<TranslationDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var buffer = new byte[maximumBytes + 1];
        var count = 0;

        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken);

            if (read == 0)
            {
                break;
            }

            count += read;
            progress?.Report(new(count, total));
        }

        if (count > maximumBytes)
        {
            throw TranslationPackJson.Invalid("The translation record exceeds its byte budget.");
        }

        return buffer.AsSpan(0, count).ToArray();
    }

    /// <summary>Rejects unsupported identifiers before they can become URL or filesystem components.</summary>
    private static void ValidateLocale(string locale)
    {
        if (locale == "en" || !TranslationCatalog.IsSupported(locale))
        {
            throw TranslationPackJson.Invalid("Only a known downloadable locale can be requested.");
        }
    }

    /// <summary>Validates existing ancestors before creating absent portable cache directories.</summary>
    private static void ValidateExistingAncestors(string path)
    {
        var existing = path;

        while (!ExistsWithoutRedirection(existing))
        {
            existing = Path.GetDirectoryName(existing) ?? throw new IOException("No existing cache ancestor.");
        }

        PathSafety.RejectReparsePoints(existing);
    }

    /// <summary>Distinguishes an absent record from a link, including one whose target no longer exists.</summary>
    private static bool ExistsWithoutRedirection(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);

            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw TranslationPackJson.Invalid("Redirected translation cache paths are not accepted.");
            }

            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Preserves cancellation and programming failures while classifying expected I/O and data failures.</summary>
    private static bool IsExpectedFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or
        OperationFailureException or PortableRecordLockException or HttpRequestException or JsonException or
        FormatException or InvalidOperationException or KeyNotFoundException or TranslationPackException;

    /// <summary>Returns translation-only error codes independently from media ownership outcomes.</summary>
    private static TranslationPackException MapFailure(Exception exception) => exception switch
    {
        TranslationPackException translation => translation,
        PortableRecordLockException { IsBusy: true } => new(TranslationErrorCode.Busy, exception.Message, exception),
        JsonException or FormatException or InvalidOperationException or KeyNotFoundException => new(TranslationErrorCode.InvalidPack, exception.Message, exception),
        _ => new(TranslationErrorCode.Unavailable, exception.Message, exception)
    };

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitPattern();
}
