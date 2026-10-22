using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using TrackTrim.Application.Inspection;
using TrackTrim.Application.Jobs;
using TrackTrim.Application.Ports;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Media;
using TrackTrim.Core.Planning;
using TrackTrim.Infrastructure.Files;
using TrackTrim.Infrastructure.MediaTools;
using TrackTrim.Infrastructure.Processes;
using Xunit;

namespace TrackTrim.Infrastructure.Tests;

/// <summary>Exercises the real loss case where equal-count QT chapter tracks expose only one global ffprobe chapter list.</summary>
public sealed class Mp4ChapterReferenceIntegrationTests(ITestOutputHelper evidence)
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Rejects two distinct referenced chapter tracks before any remux, candidate reservation or publication.</summary>
    [MediaFact]
    public async Task DistinctChapterTracksAreRejectedBeforeOutputOwnership()
    {
        using var workspace = new TestWorkspace();
        using var workers = new ObservedMediaWorkers();
        var cancellationToken = TestContext.Current.CancellationToken;
        var source = CopyFixture(workspace.Path, "multiple-qt-chapters.mp4");
        var sourceHash = await HashAsync(source);
        var probe = await ProbeChaptersAsync(source);
        Assert.Equal(2, probe.TrackCount);
        Assert.Equal(new[] { 2, 2 }, probe.SampleCounts);
        Assert.Equal(new[] { "Other chapter", "Another chapter variant" }, probe.Titles);
        var sourceAccess = new FileSourceAccess();
        var backend = CreateBackend(sourceAccess, workers.Observe);
        var store = new OwnedOutputStore(Path.Combine(workspace.Path, "data"));
        var destination = Path.Combine(workspace.Path, "must not publish.mp4");
        var result = await new InspectionService(backend, sourceAccess).InspectAsync(new(source, cancellationToken));

        // When the regression returns, capture the complete production loss rather than reporting a probe-only mismatch.
        if (result.Inspection is { } inspection)
        {
            var plan = CreatePlan(inspection, destination);
            var job = await new JobCoordinator(backend, sourceAccess, store).RunAsync(new(plan, CancellationToken: cancellationToken));
            var published = job.PublishedPath is null ? null : await ProbeChaptersAsync(job.PublishedPath);
            evidence.WriteLine(JsonSerializer.Serialize(new
            {
                kind = "real-distinct-qt-chapter-regression",
                inputChapterTracks = probe.TrackCount,
                inputSampleCounts = probe.SampleCounts,
                probe.Titles,
                job.Phase,
                rawToolExit = job.Tool?.ExitCode,
                job.Tool?.Diagnostics,
                verificationPassed = job.Verification?.Passed,
                publishedChapterTracks = published?.TrackCount,
                publishedTitles = published?.Titles,
                sourceUnchanged = sourceHash == await HashAsync(source),
            }));
        }

        Assert.Null(result.Inspection);
        Assert.Equal(ErrorCode.UnsupportedFeature, result.Failure?.Code);
        Assert.Empty(workers.Identifiers);
        Assert.False(File.Exists(destination));
        Assert.Equal(sourceHash, await HashAsync(source));
        Assert.Empty(await store.FindInterruptedAsync(cancellationToken));
        AssertClean(workspace.Path);
        evidence.WriteLine(JsonSerializer.Serialize(new
        {
            kind = "real-distinct-qt-chapter-rejection",
            sourceSha256 = sourceHash,
            inputChapterTracks = probe.TrackCount,
            inputSampleCounts = probe.SampleCounts,
            rejection = result.Failure,
            remuxWorkers = 0,
            publishedOutput = false,
            candidateAndJournalAbsent = true,
            sourceUnchanged = true,
        }));
    }

    /// <summary>Allows repeated references to the same ordinary chapter track and preserves its semantic list through the existing lifecycle.</summary>
    [MediaFact]
    public async Task RepeatedReferencesToOneChapterTrackRemainSupported()
    {
        using var workspace = new TestWorkspace();
        using var workers = new ObservedMediaWorkers();
        var cancellationToken = TestContext.Current.CancellationToken;
        var source = CopyFixture(workspace.Path, "repeated-qt-reference.mp4");
        var sourceHash = await HashAsync(source);
        var probe = await ProbeChaptersAsync(source);
        Assert.Equal(1, probe.TrackCount);
        Assert.Equal(new[] { 2 }, probe.SampleCounts);
        Assert.Equal(new[] { "First chapter", "Вторая глава" }, probe.Titles);
        var sourceAccess = new FileSourceAccess();
        var backend = CreateBackend(sourceAccess, workers.Observe);
        var inspected = await new InspectionService(backend, sourceAccess).InspectAsync(new(source, cancellationToken));
        Assert.Null(inspected.Failure);
        var plan = CreatePlan(Assert.IsType<MediaInspection>(inspected.Inspection), Path.Combine(workspace.Path, "one chapter list.mp4"));
        var store = new OwnedOutputStore(Path.Combine(workspace.Path, "data"));
        var result = await new JobCoordinator(backend, sourceAccess, store).RunAsync(new(plan, CancellationToken: cancellationToken));
        Assert.True(result.Phase == JobPhase.Completed, $"{result.Phase}: {result.Failure}; {result.Tool}; {result.Verification}");
        Assert.True(result.Verification!.Passed);
        var outputProbe = await ProbeChaptersAsync(plan.DestinationPath);
        Assert.Equal(1, outputProbe.TrackCount);
        Assert.Equal(probe.Titles, outputProbe.Titles);
        Assert.Equal(sourceHash, await HashAsync(source));
        Assert.Empty(result.CleanupIssues);
        workers.AssertSequentialAndExited(1);
        Assert.Empty(await store.FindInterruptedAsync(cancellationToken));
        AssertClean(workspace.Path);
        evidence.WriteLine(JsonSerializer.Serialize(new
        {
            kind = "real-repeated-qt-reference-control",
            sourceSha256 = sourceHash,
            chapterTracks = outputProbe.TrackCount,
            outputProbe.Titles,
            result.Phase,
            verificationPassed = result.Verification.Passed,
            sourceUnchanged = true,
            ownedCleanupEmpty = true,
        }));
    }

    /// <summary>Copies only a reproducible development fixture into an isolated owned workspace.</summary>
    private static string CopyFixture(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        File.Copy(Path.Combine(MediaTestEnvironment.FixtureDirectory, "mp4", name), path);
        return path;
    }

    /// <summary>Constructs the same pinned adapter and process-start observation hook as the existing media suite.</summary>
    private static FfmpegBackend CreateBackend(FileSourceAccess sourceAccess, Action<int> started) => new(
        new FfmpegBackendOptions(sourceAccess, FfmpegToolPaths.FromDirectory(MediaTestEnvironment.ToolDirectory))
        { RemuxProcessStarted = started });

    /// <summary>Creates an actual changed plan from the inspected media while preserving every video and all audio.</summary>
    private static RemuxPlan CreatePlan(MediaInspection inspection, string destination) => Assert.IsType<RemuxPlan>(
        PlanBuilder.Build(new(inspection, inspection.Tracks.Where(track => track.Kind != TrackKind.Subtitles)
            .Select(track => track.Id).ToArray(), destination, false)).Plan);

    /// <summary>Independently records actual chapter stream counts and the one global list exposed by the pinned tool.</summary>
    private static async Task<ChapterProbe> ProbeChaptersAsync(string path)
    {
        var result = await new OwnedProcessRunner().RunAsync(new(FfmpegToolPaths.FromDirectory(MediaTestEnvironment.ToolDirectory).Ffprobe,
            FfmpegArguments.BuildProbe(path), Mp4Probe.MaximumJsonCharacters, ProbeTimeout, null,
            TestContext.Current.CancellationToken));
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.OutputTruncated);
        Assert.True(string.IsNullOrWhiteSpace(result.StandardError), result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var chapterStreams = document.RootElement.GetProperty("streams").EnumerateArray()
            .Where(stream => stream.GetProperty("codec_type").GetString() == "data"
                && stream.GetProperty("codec_tag_string").GetString() == "text").ToArray();
        return new(chapterStreams.Length,
            chapterStreams.Select(stream => int.Parse(stream.GetProperty("nb_frames").GetString()!, System.Globalization.CultureInfo.InvariantCulture)).ToImmutableArray(),
            document.RootElement.GetProperty("chapters").EnumerateArray().Select(chapter =>
                chapter.GetProperty("tags").GetProperty("title").GetString()!).ToImmutableArray());
    }

    /// <summary>Requires no candidate directories or ownership records after either preflight refusal or awaited successful work.</summary>
    private static void AssertClean(string directory)
    {
        var ownership = Path.Combine(directory, "data", "ownership");

        if (Directory.Exists(ownership))
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(ownership));
        }

        Assert.DoesNotContain(Directory.EnumerateDirectories(directory),
            path => Path.GetFileName(path).StartsWith(".tracktrim-", StringComparison.Ordinal));
    }

    /// <summary>Hashes the complete untouched source independently of revision metadata.</summary>
    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, TestContext.Current.CancellationToken));
    }

    private sealed record ChapterProbe(int TrackCount, ImmutableArray<int> SampleCounts, ImmutableArray<string> Titles);
}
