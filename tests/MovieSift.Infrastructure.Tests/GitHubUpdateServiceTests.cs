using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using MovieSift.Application.Updates;
using MovieSift.Infrastructure.Updates;
using Xunit;

namespace MovieSift.Infrastructure.Tests;

/// <summary>Exercises the public release boundary without contacting GitHub or changing a real installation.</summary>
public sealed class GitHubUpdateServiceTests
{
    [Fact]
    public async Task SelectsOnlyExactPlatformArchiveAndUsesAnonymousRequest()
    {
        using var handler = new FeedHandler(request =>
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.NotEmpty(request.Headers.UserAgent);
            Assert.Null(request.Headers.Authorization);
            return JsonResponse(ReleaseJson("v1.10.0"));
        });
        using var client = new HttpClient(handler);
        var result = await Service(client).CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new AppVersion(1, 10, 0), result!.Version);
        Assert.Equal("MovieSift-linux-x64.tar.gz", result.Asset.Name);
    }

    [Theory]
    [InlineData("v1.0.0", false, false)]
    [InlineData("v0.9.0", false, false)]
    [InlineData("v2.0.0", true, false)]
    [InlineData("v2.0.0", false, true)]
    public async Task EqualOlderDraftOrPrereleaseNeverOffersInstallation(string tag, bool draft, bool prerelease)
    {
        using var client = new HttpClient(new FeedHandler(_ => JsonResponse(ReleaseJson(tag, draft, prerelease))));
        Assert.Null(await Service(client).CheckAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("sha256:bad")]
    [InlineData("sha1:123")]
    [InlineData("")]
    public async Task MissingStrongDigestCannotOfferAnUpdate(string digest)
    {
        using var client = new HttpClient(new FeedHandler(_ => JsonResponse(ReleaseJson("v1.1.0", digest: digest))));
        var failure = await Assert.ThrowsAsync<UpdateException>(() => Service(client).CheckAsync(TestContext.Current.CancellationToken));
        Assert.Equal(UpdateErrorCode.InvalidRelease, failure.Code);
    }

    [Fact]
    public async Task ExternalRedirectIsRejectedBeforeAnyRequestToThatHost()
    {
        var count = 0;
        using var client = new HttpClient(new FeedHandler(_ =>
        {
            count++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new("https://github.com.attacker.invalid/update.zip");
            return response;
        }));
        var failure = await Assert.ThrowsAsync<UpdateException>(() => Service(client).CheckAsync(TestContext.Current.CancellationToken));
        Assert.Equal(UpdateErrorCode.InvalidRelease, failure.Code);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task NetworkAndMalformedResponseHaveBoundedRecoverableFailures()
    {
        using var offline = new HttpClient(new FeedHandler(_ => throw new HttpRequestException("offline")));
        Assert.Equal(UpdateErrorCode.Network, (await Assert.ThrowsAsync<UpdateException>(() => Service(offline).CheckAsync(TestContext.Current.CancellationToken))).Code);
        using var invalid = new HttpClient(new FeedHandler(_ => JsonResponse("not json")));
        Assert.Equal(UpdateErrorCode.InvalidRelease, (await Assert.ThrowsAsync<UpdateException>(() => Service(invalid).CheckAsync(TestContext.Current.CancellationToken))).Code);
    }

    [Fact]
    public async Task DamagedDownloadNeverChangesOldInstallationAndRemovesOwnedStaging()
    {
        using var workspace = new TestWorkspace();
        var app = Path.Combine(workspace.Path, "app");
        Directory.CreateDirectory(app);
        await File.WriteAllTextAsync(Path.Combine(app, "portable.flag"), "portable", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(app, "MovieSift.Updater"), "helper", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(app, "unchanged.txt"), "original", TestContext.Current.CancellationToken);
        using var client = new HttpClient(new FeedHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }));
        var service = new GitHubUpdateService(client, app, new(1, 0, 0), "linux-x64");
        var release = new UpdateRelease(new(1, 1, 0), new("https://github.com/Nexusov/MovieSift/releases/tag/v1.1.0"),
            new("MovieSift-linux-x64.tar.gz", new("https://github.com/Nexusov/MovieSift/releases/download/v1.1.0/MovieSift-linux-x64.tar.gz"),
                3, Convert.ToHexString(SHA256.HashData(new byte[] { 9, 9, 9 }))));
        var error = await Assert.ThrowsAsync<UpdateException>(() => service.StageAsync(release, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(UpdateErrorCode.Integrity, error.Code);
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(app, "unchanged.txt"), TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetDirectories(workspace.Path));
    }

    /// <summary>Future updates copy the verified shared helper/runtime from app, never just the root launcher.</summary>
    [Fact]
    public async Task NestedInstallationStagesAndDiscardsWithoutChangingItsCurrentFiles()
    {
        using var fixture = new PortableUpdateInstallerTests.InstallationFixture();
        await fixture.PrepareNestedPayloadAsync();
        var current = Path.Combine(fixture.Session, "payload");
        var runtime = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
        var name = $"MovieSift-{runtime}" + (OperatingSystem.IsWindows() ? ".zip" : ".tar.gz");
        var archive = await CreateArchiveAsync(current, runtime);
        var before = await UpdatePackage.HashAsync(Path.Combine(current, "SHA256SUMS"), TestContext.Current.CancellationToken);
        using var client = new HttpClient(new FeedHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) }));
        var service = new GitHubUpdateService(client, current, new(1, 0, 0), runtime);
        var release = new UpdateRelease(new(1, 1, 0), new("https://github.com/Nexusov/MovieSift/releases/tag/v1.1.0"),
            new(name, new($"https://github.com/Nexusov/MovieSift/releases/download/v1.1.0/{name}"),
                archive.Length, Convert.ToHexString(SHA256.HashData(archive))));
        var prepared = await service.StageAsync(release, cancellationToken: TestContext.Current.CancellationToken);
        var helperRoot = Path.Combine(prepared.SessionDirectory, "helper");
        foreach (var helperName in new[] { "MovieSift.Updater.dll", "MovieSift.Updater.deps.json", "MovieSift.Updater.runtimeconfig.json", "MovieSift.dll" })
        {
            Assert.Equal(await UpdatePackage.HashAsync(Path.Combine(current, "app", helperName), TestContext.Current.CancellationToken),
                await UpdatePackage.HashAsync(Path.Combine(helperRoot, helperName), TestContext.Current.CancellationToken));
        }
        Assert.False(File.Exists(Path.Combine(helperRoot, OperatingSystem.IsWindows() ? "MovieSift.exe" : "MovieSift")));
        Assert.False(File.Exists(Path.Combine(helperRoot, "portable.flag")));
        Assert.Equal(before, await UpdatePackage.HashAsync(Path.Combine(current, "SHA256SUMS"), TestContext.Current.CancellationToken));
        await service.DiscardAsync(prepared, TestContext.Current.CancellationToken);
        Assert.False(Directory.Exists(prepared.SessionDirectory));
        Assert.Equal(before, await UpdatePackage.VerifyAsync(current, runtime, new(1, 1, 0), before, TestContext.Current.CancellationToken));
    }

    /// <summary>Uses the same real ZIP/tar readers as production with a small controlled complete payload.</summary>
    private static async Task<byte[]> CreateArchiveAsync(string directory, string runtime)
    {
        using var result = new MemoryStream();
        var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);

        if (runtime == "win-x64")
        {
            using (var zip = new ZipArchive(result, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var file in files)
                {
                    var relative = Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/');
                    await using var entry = zip.CreateEntry($"MovieSift-{runtime}/{relative}").Open();
                    await using var input = File.OpenRead(file);
                    await input.CopyToAsync(entry, TestContext.Current.CancellationToken);
                }
            }
            return result.ToArray();
        }

        if (OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("The Linux fixture archive requires Unix file modes.");
        }

        await using (var gzip = new GZipStream(result, CompressionLevel.Fastest, leaveOpen: true))
        await using (var tar = new TarWriter(gzip))
        {
            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/');
                await using var input = File.OpenRead(file);
                var entry = new PaxTarEntry(TarEntryType.RegularFile, $"MovieSift-{runtime}/{relative}")
                {
                    DataStream = input,
                    Mode = File.GetUnixFileMode(file)
                };
                await tar.WriteEntryAsync(entry, TestContext.Current.CancellationToken);
            }
        }
        return result.ToArray();
    }

    private static GitHubUpdateService Service(HttpClient client) => new(client, Path.GetTempPath(), new(1, 0, 0), "linux-x64");

    private static HttpResponseMessage JsonResponse(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content) };

    private static string ReleaseJson(string tag, bool draft = false, bool prerelease = false, string? digest = null) => JsonSerializer.Serialize(new
    {
        tag_name = tag,
        html_url = $"https://github.com/Nexusov/MovieSift/releases/tag/{tag}",
        draft,
        prerelease,
        assets = new[]
        {
            new
            {
                name = "MovieSift-linux-x64.tar.gz",
                browser_download_url = $"https://github.com/Nexusov/MovieSift/releases/download/{tag}/MovieSift-linux-x64.tar.gz",
                size = 123,
                digest = digest ?? "sha256:" + new string('a', 64)
            }
        }
    });

    private sealed class FeedHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
