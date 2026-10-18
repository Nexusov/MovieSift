using System.Security.Cryptography;
using TrackTrim.Application.Inspection;
using TrackTrim.Application.Jobs;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Media;
using TrackTrim.Core.Planning;
using TrackTrim.Infrastructure.Files;
using TrackTrim.Infrastructure.MediaTools;
using TrackTrim.Infrastructure.Processes;
using Xunit;

namespace TrackTrim.Infrastructure.Tests;

/// <summary>Exercises SegmentFamily capability rejection through the actual production lifecycle.</summary>
public sealed class SegmentFamilyIntegrationTests
{
    /// <summary>Rejects family-linked input before planning while an ordinary control can still publish.</summary>
    [MediaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreflightRejectsSegmentFamilyAndOrdinaryControlPublishes(bool hasSegmentFamily)
    {
        using var workspace = new TestWorkspace();
        var cancellationToken = TestContext.Current.CancellationToken;
        var sourcePath = Path.Combine(workspace.Path, "source.mkv");
        var destinationPath = Path.Combine(workspace.Path, "result.mkv");
        await CreateSourceAsync(new(sourcePath, workspace.Path, hasSegmentFamily, cancellationToken));
        var sourceHash = await HashAsync(new(sourcePath, cancellationToken));
        var sourceAccess = new FileSourceAccess();
        var backend = MediaTestEnvironment.CreateBackend(Path.Combine(workspace.Path, "scratch"));
        var outputStore = new OwnedOutputStore(Path.Combine(workspace.Path, "data"));
        var inspection = await new InspectionService(backend, sourceAccess).InspectAsync(new(sourcePath, cancellationToken));
        JobResult? jobResult = null;

        if (inspection.Inspection is { } acceptedInspection)
        {
            var firstAudio = acceptedInspection.Tracks.First(track => track.Kind == TrackKind.Audio);
            var retainedIds = acceptedInspection.Tracks.Where(track => track.Kind == TrackKind.Video || track.Id == firstAudio.Id)
                .Select(track => track.Id).ToArray();
            var plan = PlanBuilder.Build(new(acceptedInspection, retainedIds, destinationPath, false)).Plan!;
            jobResult = await new JobCoordinator(backend, sourceAccess, outputStore).RunAsync(new(plan, CancellationToken: cancellationToken));
        }

        Assert.Equal(sourceHash, await HashAsync(new(sourcePath, cancellationToken)));

        if (hasSegmentFamily)
        {
            Assert.True(inspection.Failure?.Code == ErrorCode.UnsupportedFeature,
                $"Expected preflight rejection; inspection failure: {inspection.Failure}; lifecycle phase: {jobResult?.Phase}.");
            Assert.NotNull(inspection.Failure);
            Assert.Equal(ErrorReason.LinkedSegments, inspection.Failure.Reason);
            Assert.Null(inspection.Inspection);
            Assert.Null(jobResult);
            Assert.False(File.Exists(destinationPath));
            Assert.Empty(Directory.EnumerateDirectories(workspace.Path, ".tracktrim-*"));
            return;
        }

        Assert.Null(inspection.Failure);
        Assert.NotNull(jobResult);
        Assert.Equal(JobPhase.Completed, jobResult.Phase);
        Assert.Equal(destinationPath, jobResult.PublishedPath);
    }

    /// <summary>Generates a real family-linked source using the already pinned production distribution.</summary>
    private static async Task CreateSourceAsync(SourceFixture request)
    {
        var ordinaryPath = Path.Combine(MediaTestEnvironment.FixtureDirectory, "ordinary.mkv");

        if (!request.HasSegmentFamily)
        {
            File.Copy(ordinaryPath, request.Path);
            return;
        }

        var infoPath = Path.Combine(request.Directory, "segment-info.xml");
        await File.WriteAllTextAsync(infoPath,
            "<Info><SegmentFamily format=\"hex\">11001111112211331144115511661177</SegmentFamily></Info>", request.CancellationToken);
        var tools = MkvToolPaths.FromDirectory(MediaTestEnvironment.ToolDirectory);
        var environment = new Dictionary<string, string> { ["LC_ALL"] = "C.UTF-8", ["LANG"] = "C.UTF-8" };

        if (tools.NativeLibraryDirectory is { } libraryDirectory)
        {
            environment["LD_LIBRARY_PATH"] = libraryDirectory;
        }

        var outcome = await new OwnedProcessRunner().RunAsync(new(tools.MkvMerge,
            ["--segmentinfo", infoPath, "--normalize-language-ietf", "off", "--disable-track-statistics-tags", "-o", request.Path, ordinaryPath],
            32768, TimeSpan.FromMinutes(2), null, request.CancellationToken, environment));
        Assert.Equal(0, outcome.ExitCode);
    }

    /// <summary>Checks original bytes independently from metadata and lifecycle outcomes.</summary>
    private static async Task<string> HashAsync(HashRequest request)
    {
        await using var stream = File.OpenRead(request.Path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, request.CancellationToken));
    }

    private sealed record SourceFixture(string Path, string Directory, bool HasSegmentFamily, CancellationToken CancellationToken);
    private sealed record HashRequest(string Path, CancellationToken CancellationToken);
}
