using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using MovieSift.Application.Ports;
using MovieSift.Application.Updates;
using MovieSift.Infrastructure.Files;

namespace MovieSift.Infrastructure.Updates;

/// <summary>Retrieves a public stable release, verifies its archive and hands installation to the bundled helper.</summary>
public sealed class GitHubUpdateService(HttpClient client, string applicationDirectory, AppVersion currentVersion,
    string runtimeIdentifier, bool installationLeaseHeld = true) : IUpdateService
{
    private const string ReleaseApi = "https://api.github.com/repos/Nexusov/MovieSift/releases/latest";
    private const string ReleaseRoot = "https://github.com/Nexusov/MovieSift/releases/";
    private const int MaximumFeedBytes = 1024 * 1024;
    private readonly string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory));
    private readonly SemaphoreSlim stagingGate = new(1, 1);
    private PreparedUpdate? prepared;
    private ReservationDirectory? preparedOwnership;
    private string? preparedPlanHash;
    private Dictionary<string, string>? preparedHelperHashes;

    /// <summary>Checks asynchronously; no network failure can become media processing authority.</summary>
    public async Task<UpdateRelease?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var response = await SendAsync(new(ReleaseApi), timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[MaximumFeedBytes + 1];
            var count = await body.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, timeout.Token);
            if (count > MaximumFeedBytes)
            {
                throw new UpdateException(UpdateErrorCode.InvalidRelease, "The release response exceeds its size limit.");
            }
            using var json = JsonDocument.Parse(buffer.AsMemory(0, count), new() { MaxDepth = 16 });
            return ParseRelease(json.RootElement);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException(UpdateErrorCode.Network, "The update check timed out.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new UpdateException(UpdateErrorCode.Network, "GitHub could not be reached for an update check.", exception);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            throw new UpdateException(UpdateErrorCode.InvalidRelease, "GitHub returned an invalid release record.", exception);
        }
    }

    /// <summary>Downloads and checks a complete fresh tree without writing any active installation file.</summary>
    public async Task<PreparedUpdate> StageAsync(UpdateRelease release, IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!await stagingGate.WaitAsync(0, cancellationToken))
        {
            throw new UpdateException(UpdateErrorCode.Busy, "Another update operation is already running.");
        }
        string? session = null;
        ReservationDirectory? ownership = null;
        Exception? primaryFailure = null;
        try
        {
            if (prepared is not null)
            {
                throw new UpdateException(UpdateErrorCode.Busy, "A prepared update is already awaiting installation or discard.");
            }
            ValidateInstallation();
            ValidateRelease(release);
            var parent = Path.GetDirectoryName(directory)!;
            session = Path.Combine(parent, $".moviesift-update-{Guid.NewGuid():N}");
            ownership = ReservationDirectory.CreateNew(session);
            var archivePath = Path.Combine(session, release.Asset.Name);
            await DownloadAsync(release.Asset, archivePath, progress, cancellationToken);
            var payload = Path.Combine(session, "payload");
            Directory.CreateDirectory(payload);
            await UpdatePackage.ExtractAsync(archivePath, payload, runtimeIdentifier, cancellationToken);
            var manifestHash = await UpdatePackage.VerifyAsync(payload, runtimeIdentifier, release.Version, null, cancellationToken);
            File.Delete(archivePath);
            preparedHelperHashes = await CopyHelperRuntimeAsync(session, cancellationToken);
            using var process = Process.GetCurrentProcess();
            var plan = new UpdateInstallPlan(1, Guid.NewGuid().ToString("N"), directory, runtimeIdentifier,
                release.Version.ToString(), manifestHash, process.Id, process.StartTime.ToUniversalTime().Ticks);
            await UpdateInstallPlan.WriteAsync(session, plan, cancellationToken);
            preparedPlanHash = await UpdatePackage.HashAsync(Path.Combine(session, UpdateInstallPlan.FileName), cancellationToken);
            ownership.ValidateCurrent(session);
            prepared = new(session, release.Version);
            preparedOwnership = ownership;
            ownership = null;
            return prepared;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            primaryFailure = new UpdateException(UpdateErrorCode.Network, "The update download timed out.", exception);
            throw primaryFailure;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Application.OperationFailureException or HttpRequestException or JsonException)
        {
            primaryFailure = new UpdateException(exception is HttpRequestException ? UpdateErrorCode.Network : UpdateErrorCode.Storage,
                "The update could not be prepared; the existing application was not changed.", exception);
            throw primaryFailure;
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            try
            {
                if (session is not null && ownership is not null && prepared?.SessionDirectory != session)
                {
                    ownership.ValidateCurrent(session);
                    _ = UpdatePackage.EnumerateRegularFiles(session).ToArray();
                    Directory.Delete(session, recursive: true);
                }
            }
            catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException or Application.OperationFailureException or UpdateException)
            {
                throw new UpdateException(primaryFailure is UpdateException updateFailure ? updateFailure.Code : UpdateErrorCode.Storage,
                    $"{primaryFailure?.Message ?? "Update preparation failed."} Temporary update cleanup also failed; storage was preserved at {session}: {cleanupException.Message}",
                    primaryFailure ?? cleanupException);
            }
            finally
            {
                ownership?.Dispose();
                stagingGate.Release();
            }
        }
    }

    /// <summary>Starts only this service's prepared helper; the UI must then complete its safe shutdown.</summary>
    public async Task LaunchInstallerAsync(PreparedUpdate update, CancellationToken cancellationToken = default)
    {
        if (!await stagingGate.WaitAsync(0, cancellationToken))
        {
            throw new UpdateException(UpdateErrorCode.Busy, "Another update operation is already running.");
        }
        try
        {
            if (prepared != update)
            {
                throw new UpdateException(UpdateErrorCode.Installer, "The update does not belong to this preparation session.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            var planPath = Path.Combine(update.SessionDirectory, UpdateInstallPlan.FileName);
            var hash = await UpdatePackage.HashAsync(planPath, cancellationToken);
            preparedOwnership!.ValidateCurrent(update.SessionDirectory);
            if (hash != preparedPlanHash)
            {
                throw new UpdateException(UpdateErrorCode.Integrity, "The prepared installation plan was modified.");
            }
            foreach (var entry in preparedHelperHashes ?? throw new UpdateException(UpdateErrorCode.Installer, "The helper identity is missing."))
            {
                if (!string.Equals(await UpdatePackage.HashAsync(Path.Combine(update.SessionDirectory, "helper", entry.Key), cancellationToken),
                    entry.Value, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UpdateException(UpdateErrorCode.Integrity, "The prepared helper/runtime was modified.");
                }
            }
            var name = runtimeIdentifier == "win-x64" ? "MovieSift.Updater.exe" : "MovieSift.Updater";
            var info = new ProcessStartInfo(Path.Combine(update.SessionDirectory, "helper", name))
            {
                UseShellExecute = false,
                WorkingDirectory = update.SessionDirectory,
                CreateNoWindow = true
            };
            info.ArgumentList.Add(planPath);
            info.ArgumentList.Add(hash);
            try
            {
                using var process = Process.Start(info) ?? throw new IOException("The update helper did not start.");
                prepared = null;
                preparedOwnership?.Dispose();
                preparedOwnership = null;
                preparedPlanHash = null;
                preparedHelperHashes = null;
            }
            catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                throw new UpdateException(UpdateErrorCode.Installer, "The update helper could not be started.", exception);
            }
        }
        finally
        {
            stagingGate.Release();
        }
    }

    /// <summary>Deletes only this session's still-owned preparation before the helper has accepted it.</summary>
    public async Task DiscardAsync(PreparedUpdate update, CancellationToken cancellationToken = default)
    {
        if (!await stagingGate.WaitAsync(0, cancellationToken))
        {
            throw new UpdateException(UpdateErrorCode.Busy, "Another update operation is already running.");
        }
        try
        {
            if (prepared != update || preparedOwnership is null)
            {
                throw new UpdateException(UpdateErrorCode.Installer, "The update is no longer owned by this preparation session.");
            }
            preparedOwnership.ValidateCurrent(update.SessionDirectory);
            _ = UpdatePackage.EnumerateRegularFiles(update.SessionDirectory).ToArray();
            Directory.Delete(update.SessionDirectory, recursive: true);
            preparedOwnership.Dispose();
            preparedOwnership = null;
            prepared = null;
            preparedPlanHash = null;
            preparedHelperHashes = null;
        }
        finally
        {
            stagingGate.Release();
        }
    }

    /// <summary>Copies the existing helper and its shared self-contained runtime outside the replaced tree.</summary>
    private async Task<Dictionary<string, string>> CopyHelperRuntimeAsync(string session, CancellationToken cancellationToken)
    {
        var manifest = await UpdatePackage.ReadManifestAsync(directory, cancellationToken);
        var destination = Path.Combine(session, "helper");
        Directory.CreateDirectory(destination);
        var copiedManifest = manifest.Where(entry => !entry.Key.Contains('/')).ToDictionary(entry => entry.Key, entry => entry.Value);
        foreach (var entry in copiedManifest)
        {
            var source = Path.Combine(directory, entry.Key);
            var target = Path.Combine(destination, entry.Key);
            await using (var input = FileSourceAccess.OpenRegularReadStream(source))
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await input.CopyToAsync(output, cancellationToken);
            }
            if (!string.Equals(await UpdatePackage.HashAsync(target, cancellationToken), entry.Value, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateException(UpdateErrorCode.Integrity, "The installed helper/runtime does not match its package manifest.");
            }
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(target, File.GetUnixFileMode(source) & (UnixFileMode)0x1FF);
            }
        }
        return copiedManifest;
    }

    private UpdateRelease? ParseRelease(JsonElement root)
    {
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
        {
            return null;
        }
        var tag = root.GetProperty("tag_name").GetString();
        if (!AppVersion.TryParse(tag, out var version))
        {
            throw new UpdateException(UpdateErrorCode.InvalidRelease, "The latest release has no supported stable version.");
        }
        if (version.CompareTo(currentVersion) <= 0)
        {
            return null;
        }
        var name = runtimeIdentifier == "win-x64" ? "MovieSift-win-x64.zip" : "MovieSift-linux-x64.tar.gz";
        var assets = root.GetProperty("assets").EnumerateArray()
            .Where(asset => asset.GetProperty("name").GetString() == name).ToArray();
        if (assets.Length != 1)
        {
            throw new UpdateException(UpdateErrorCode.InvalidRelease, "The release has no unique archive for this platform.");
        }
        var asset = assets[0];
        var digest = asset.GetProperty("digest").GetString() ?? string.Empty;
        if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || !UpdatePackage.IsSha256(digest[7..]))
        {
            throw new UpdateException(UpdateErrorCode.InvalidRelease, "The release archive has no SHA256 digest.");
        }
        var release = new UpdateRelease(version, new(root.GetProperty("html_url").GetString()!),
            new(name, new(asset.GetProperty("browser_download_url").GetString()!), asset.GetProperty("size").GetInt64(), digest[7..]));
        ValidateRelease(release);
        var expected = $"{ReleaseRoot}download/{Uri.EscapeDataString(tag!)}/{name}";
        if (release.Asset.DownloadUri.AbsoluteUri != expected)
        {
            throw new UpdateException(UpdateErrorCode.InvalidRelease, "The release download does not belong to the expected repository and tag.");
        }
        return release;
    }

    private void ValidateRelease(UpdateRelease release)
    {
        var expectedName = runtimeIdentifier == "win-x64" ? "MovieSift-win-x64.zip" : "MovieSift-linux-x64.tar.gz";
        if (runtimeIdentifier is not ("win-x64" or "linux-x64") || release.Version.CompareTo(currentVersion) <= 0 ||
            release.Asset.Name != expectedName || release.Asset.Size <= 0 || release.Asset.Size > UpdatePackage.MaximumArchiveBytes ||
            !UpdatePackage.IsSha256(release.Asset.Sha256) ||
            !release.ReleasePage.AbsoluteUri.StartsWith(ReleaseRoot + "tag/", StringComparison.Ordinal) ||
            !release.Asset.DownloadUri.AbsoluteUri.StartsWith(ReleaseRoot + "download/", StringComparison.Ordinal))
        {
            throw new UpdateException(UpdateErrorCode.InvalidRelease, "The offered update is not a valid newer MovieSift release.");
        }
    }

    private void ValidateInstallation()
    {
        var helper = Path.Combine(directory, runtimeIdentifier == "win-x64" ? "MovieSift.Updater.exe" : "MovieSift.Updater");
        if (!installationLeaseHeld || !File.Exists(Path.Combine(directory, "portable.flag")) || !File.Exists(helper) ||
            Path.GetDirectoryName(directory) is not { } parent || FileSourceAccess.GetContainingDrive(parent).DriveType == DriveType.Network)
        {
            throw new UpdateException(UpdateErrorCode.UnsupportedInstallation, "Automatic installation requires a local writable portable package with its update helper.");
        }
        PathSafety.RejectReparsePoints(directory);
    }

    private async Task DownloadAsync(UpdateAsset asset, string destination, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        using var response = await SendAsync(asset.DownloadUri, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && length != asset.Size)
        {
            throw new UpdateException(UpdateErrorCode.Integrity, "The download size does not match the release asset.");
        }
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81_920];
        long received = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, timeout.Token);
            if (count == 0)
            {
                break;
            }
            if (received > asset.Size - count)
            {
                throw new UpdateException(UpdateErrorCode.Integrity, "The download exceeds its declared size.");
            }
            await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
            hash.AppendData(buffer.AsSpan(0, count));
            received += count;
            progress?.Report(new(received, asset.Size));
        }
        var digest = Convert.ToHexString(hash.GetHashAndReset());
        if (received != asset.Size || !digest.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException(UpdateErrorCode.Integrity, "The downloaded update failed its SHA256 verification.");
        }
        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
    }

    /// <summary>Follows only bounded HTTPS redirects to GitHub's release infrastructure.</summary>
    private async Task<HttpResponseMessage> SendAsync(Uri address, CancellationToken cancellationToken)
    {
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            ValidateDownloadAddress(address);
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.UserAgent.ParseAdd($"MovieSift/{currentVersion}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            ValidateDownloadAddress(response.RequestMessage?.RequestUri ?? address);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308))
            {
                return response;
            }
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
            {
                break;
            }
            address = location.IsAbsoluteUri ? location : new(address, location);
        }
        throw new UpdateException(UpdateErrorCode.Network, "The release download exceeded its redirect limit.");
    }

    private static void ValidateDownloadAddress(Uri address)
    {
        if (address.Scheme != Uri.UriSchemeHttps || !address.IsDefaultPort || address.UserInfo.Length != 0 ||
            address.Host is not ("api.github.com" or "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
        {
            throw new UpdateException(UpdateErrorCode.InvalidRelease, "The update download was redirected outside the trusted HTTPS hosts.");
        }
    }
}
