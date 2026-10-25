using System.Security.Cryptography;
using MovieSift.Application.Ports;
using MovieSift.Core.Media;
using MovieSift.Core.Planning;
using MovieSift.Infrastructure.MediaTools;
using MovieSift.Infrastructure.Processes;
using Xunit;

namespace MovieSift.Infrastructure.Tests;

/// <summary>Exercises unnormalized FFmpeg-origin H264/AAC/SRT media with bibliographic language metadata.</summary>
public sealed class FfmpegOriginIntegrationTests
{
    private const int MaximumDiagnosticsCharacters = 32768;
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Rejects a real known final-frame duration change instead of treating all AAC remuxes as equivalent.</summary>
    [MediaTheory]
    [InlineData("ger")]
    [InlineData("fre")]
    public async Task RejectsKnownAacEndDurationChangeWithBibliographicLanguage(string language)
    {
        await AssertRetainedAsync(new FixtureRequest("h264-aac-aliases.mkv", language, false, true));
    }

    /// <summary>Reproduces the missing AAC DefaultDuration header in an original FFmpeg 7.1.1 input.</summary>
    [MediaTheory]
    [InlineData("ger")]
    [InlineData("fre")]
    public async Task RetainsAacWithSynthesizedDefaultDuration(string language)
    {
        await AssertRetainedAsync(new FixtureRequest("h264-aac-no-default-duration.mkv", language, true));
    }

    /// <summary>Rejects a real shifted AAC candidate even though its newly inferred timing header is valid.</summary>
    [MediaFact]
    public async Task RejectsChangedActualAacTimestampsWithOtherwiseEquivalentHeaders()
    {
        using var workspace = new TestWorkspace();
        var sourcePath = Path.Combine(MediaTestEnvironment.RepositoryDirectory, "tests", "MovieSift.Infrastructure.Tests",
            "Fixtures", "h264-aac-no-default-duration.mkv");
        var originalHash = await HashAsync(sourcePath);
        var backend = MediaTestEnvironment.CreateBackend(Path.Combine(workspace.Path, "scratch"));
        var inspection = await backend.InspectAsync(new InspectRequest(sourcePath));
        var germanAudio = Assert.Single(inspection.Tracks.Where(track => track.Kind == TrackKind.Audio && track.Language == "ger"));
        var retainedIdentifiers = inspection.Tracks.Where(track => track.Kind != TrackKind.Audio || track.Id == germanAudio.Id)
            .Select(track => track.Id).ToArray();
        var candidate = Path.Combine(workspace.Path, "shifted-aac.mkv");
        var plan = PlanBuilder.Build(new PlanRequest(inspection, retainedIdentifiers, candidate, false)).Plan!;
        var arguments = MkvArguments.BuildRemux(new RemuxRequest(plan, candidate, null)).ToList();
        arguments.InsertRange(arguments.Count - 1, ["--sync", "1:25"]);
        var tools = MkvToolPaths.FromDirectory(MediaTestEnvironment.ToolDirectory);
        var environment = new Dictionary<string, string> { ["LC_ALL"] = "C.UTF-8", ["LANG"] = "C.UTF-8" };

        if (tools.NativeLibraryDirectory is { } nativeLibraryDirectory)
        {
            environment["LD_LIBRARY_PATH"] = nativeLibraryDirectory;
        }

        var toolResult = await new OwnedProcessRunner().RunAsync(new ProcessRequest(tools.MkvMerge, arguments,
            MaximumDiagnosticsCharacters, WorkerTimeout, null, TestContext.Current.CancellationToken, environment));
        Assert.True(toolResult.ExitCode == 0, toolResult.StandardError);
        var verification = await backend.VerifyAsync(new VerifyRequest(plan, candidate));
        Assert.False(verification.Passed);
        Assert.Contains("timestamps or frame count", verification.Details, StringComparison.Ordinal);
        Assert.Equal(originalHash, await HashAsync(sourcePath));
    }

    /// <summary>Checks the real approved remux against an untouched direct FFmpeg fixture.</summary>
    private static async Task AssertRetainedAsync(FixtureRequest request)
    {
        using var workspace = new TestWorkspace();
        var sourcePath = Path.Combine(MediaTestEnvironment.RepositoryDirectory, "tests", "MovieSift.Infrastructure.Tests",
            "Fixtures", request.Name);
        Assert.True(File.Exists(sourcePath), "The checked-in original FFmpeg fixture is required; it must not be normalized through mkvmerge.");
        var originalHash = await HashAsync(sourcePath);
        var backend = MediaTestEnvironment.CreateBackend(Path.Combine(workspace.Path, "scratch"));
        var inspection = await backend.InspectAsync(new InspectRequest(sourcePath));
        var audio = Assert.Single(inspection.Tracks.Where(track => track.Kind == TrackKind.Audio && track.Language == request.Language));
        Assert.Equal("A_AAC", audio.Properties["codec_id"]);
        Assert.Null(audio.LanguageIetf);
        Assert.StartsWith("Lavf", audio.Properties["_source_writing_application"], StringComparison.Ordinal);
        Assert.Equal(request.MissingDefaultDuration, !audio.Properties.ContainsKey("default_duration"));
        var retainedIdentifiers = inspection.Tracks.Where(track => track.Kind != TrackKind.Audio || track.Id == audio.Id)
            .Select(track => track.Id).ToArray();
        var candidate = Path.Combine(workspace.Path, $"retained-{request.Language}.mkv");
        var plan = PlanBuilder.Build(new PlanRequest(inspection, retainedIdentifiers, candidate, false)).Plan!;
        var result = await backend.RemuxAsync(new RemuxRequest(plan, candidate, null));
        Assert.Equal(0, result.ExitCode);
        var verification = await backend.VerifyAsync(new VerifyRequest(plan, candidate));

        if (request.KnownEndChange)
        {
            Assert.False(verification.Passed);
            Assert.Contains("timestamps or frame count", verification.Details, StringComparison.Ordinal);
            Assert.Equal(originalHash, await HashAsync(sourcePath));
            return;
        }

        Assert.True(verification.Passed, verification.Details);
        var output = await backend.InspectAsync(new InspectRequest(candidate));
        Assert.Single(output.Tracks.Where(track => track.Kind == TrackKind.Subtitles));

        for (var trackIndex = 0; trackIndex < plan.RetainedTracks.Length; trackIndex++)
        {
            var sourceTrack = plan.RetainedTracks[trackIndex];
            var outputTrack = output.Tracks[trackIndex];
            await MediaPayloadEvidence.AssertIdenticalAsync(new PayloadComparison(sourcePath, sourceTrack.Id,
                candidate, outputTrack.Id, workspace.Path, $"retained-{sourceTrack.Kind}-{sourceTrack.Id}"));
        }

        Assert.Equal(originalHash, await HashAsync(sourcePath));
    }

    /// <summary>Hashes original fixture bytes to detect accidental source writes.</summary>
    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private sealed record FixtureRequest(string Name, string Language, bool MissingDefaultDuration, bool KnownEndChange = false);
}
