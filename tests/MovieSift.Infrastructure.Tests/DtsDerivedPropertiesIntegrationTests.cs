using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using MovieSift.Application.Inspection;
using MovieSift.Application.Jobs;
using MovieSift.Application.Ports;
using MovieSift.Core.Media;
using MovieSift.Core.Planning;
using MovieSift.Infrastructure.Files;
using MovieSift.Infrastructure.MediaTools;
using MovieSift.Infrastructure.Processes;
using Xunit;

namespace MovieSift.Infrastructure.Tests;

/// <summary>Checks payload-proven DTS header derivation through actual copy and replacement jobs.</summary>
public sealed class DtsDerivedPropertiesIntegrationTests
{
    /// <summary>Legacy missing DTS headers are admitted only while all retained payloads and explicit output safety survive.</summary>
    [MediaTheory]
    [InlineData("dts-derived-properties.mkv", OutputMode.Copy)]
    [InlineData("dts-derived-properties.mkv", OutputMode.ReplaceSource)]
    [InlineData("dts-derived-properties-laced.mkv", OutputMode.Copy)]
    [InlineData("dts-derived-properties-laced.mkv", OutputMode.ReplaceSource)]
    public async Task MissingDtsHeadersAreDerivedWithoutChangingRetainedPayloads(string fixtureName, OutputMode outputMode)
    {
        using var workspace = new TestWorkspace();
        var fixture = Path.Combine(MediaTestEnvironment.FixtureDirectory, fixtureName);
        Assert.True(File.Exists(fixture), "The required original DTS fixture must be generated before mandatory media tests.");
        var originalHash = await HashAsync(fixture);
        var sourceDirectory = Path.Combine(workspace.Path, "Исходники с пробелами");
        Directory.CreateDirectory(sourceDirectory);
        var source = Path.Combine(sourceDirectory, "original DTS серия.mkv");
        File.Copy(fixture, source);
        var destination = outputMode == OutputMode.ReplaceSource ? source : Path.Combine(sourceDirectory, "result DTS.trimmed.mkv");
        var scratch = Path.Combine(workspace.Path, "scratch");
        var access = new FileSourceAccess();
        Process? worker = null;
        var tools = MkvToolPaths.FromDirectory(MediaTestEnvironment.ToolDirectory) with { IsDevelopmentOverride = true };
        var backend = new MkvBackend(new(access, tools, scratch)
        {
            RemuxProcessStarted = identifier =>
            {
                worker = Process.GetProcessById(identifier);
                _ = worker.Handle;
            }
        });

        try
        {
            var inspected = await new InspectionService(backend, access).InspectAsync(new(source, TestContext.Current.CancellationToken));
            Assert.Null(inspected.Failure);
            var inspection = Assert.IsType<MediaInspection>(inspected.Inspection);
            var dts = Assert.Single(inspection.Tracks, track => track.Properties.GetValueOrDefault("codec_id") == "A_DTS");
            Assert.False(dts.Properties.ContainsKey("default_duration"));
            Assert.False(dts.Properties.ContainsKey("audio_bits_per_sample"));
            Assert.Equal(6, dts.Channels);
            var retained = inspection.Tracks.Where(track => track.Kind == TrackKind.Video || track.Id == dts.Id).ToArray();
            var plan = Assert.IsType<RemuxPlan>(PlanBuilder.Build(new(inspection, retained.Select(track => track.Id).ToArray(),
                destination, false, OutputMode: outputMode)).Plan);
            var data = Path.Combine(workspace.Path, "data");
            var coordinator = new JobCoordinator(backend, access, new OwnedOutputStore(data));
            var result = await coordinator.RunAsync(new(plan, CancellationToken: TestContext.Current.CancellationToken));
            Assert.True(result.Phase == JobPhase.Completed, $"{result.Phase}: {result.Failure}; {result.Verification}; {result.Tool}");
            Assert.Null(result.Failure);
            Assert.True(result.Verification!.Passed, result.Verification.Details);
            Assert.Equal(destination, result.PublishedPath);
            Assert.Empty(result.CleanupIssues);
            Assert.NotNull(worker);
            Assert.True(worker.HasExited, "The retained owned remux handle must be reaped before completion.");

            var output = await backend.InspectAsync(new(destination, TestContext.Current.CancellationToken));
            Assert.Equal(retained.Select(track => track.Uid), output.Tracks.Select(track => track.Uid));
            var outputDts = Assert.Single(output.Tracks, track => track.Properties.GetValueOrDefault("codec_id") == "A_DTS");
            Assert.Equal("10666667", outputDts.Properties["default_duration"]);
            Assert.Equal("16", outputDts.Properties["audio_bits_per_sample"]);
            Assert.Equal(dts.Title, outputDts.Title);
            Assert.Equal(dts.Language, outputDts.Language);
            Assert.Equal(dts.IsDefault, outputDts.IsDefault);
            Assert.Equal(dts.IsForced, outputDts.IsForced);
            Assert.Equal(dts.IsEnabled, outputDts.IsEnabled);

            foreach (var track in retained)
            {
                var actual = Assert.Single(output.Tracks, candidate => candidate.Uid == track.Uid);
                await MediaPayloadEvidence.AssertIdenticalAsync(new(fixture, track.Id, destination, actual.Id,
                    workspace.Path, $"dts-retained-{track.Id}"));
            }

            Assert.Equal(originalHash, await HashAsync(fixture));

            if (outputMode == OutputMode.Copy)
            {
                Assert.Equal(originalHash, await HashAsync(source));
            }

            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(data, "ownership")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
            Assert.Empty(Directory.GetDirectories(sourceDirectory));
            Assert.Equal(outputMode == OutputMode.Copy ? 2 : 1, Directory.GetFiles(sourceDirectory).Length);
        }
        finally
        {
            worker?.Dispose();
        }
    }

    /// <summary>Derived intra-lace times never authorize changing an actual encoded block timestamp.</summary>
    [MediaFact]
    public async Task ShiftedDtsBlockTimestampsFailVerificationDespiteIdenticalPayloads()
    {
        const int shiftMilliseconds = 100;
        const int maximumDiagnostics = 32768;
        using var workspace = new TestWorkspace();
        var source = Path.Combine(MediaTestEnvironment.FixtureDirectory, "dts-derived-properties-laced.mkv");
        var sourceHash = await HashAsync(source);
        var scratch = Path.Combine(workspace.Path, "scratch");
        var tools = MkvToolPaths.FromDirectory(MediaTestEnvironment.ToolDirectory) with { IsDevelopmentOverride = true };
        var backend = new MkvBackend(new(new FileSourceAccess(), tools, scratch));
        var inspection = await backend.InspectAsync(new(source, TestContext.Current.CancellationToken));
        var dts = Assert.Single(inspection.Tracks, track => track.Properties.GetValueOrDefault("codec_id") == "A_DTS");
        var retained = inspection.Tracks.Where(track => track.Kind == TrackKind.Video || track.Id == dts.Id).ToArray();
        var candidate = Path.Combine(workspace.Path, "deliberately-shifted.mkv");
        var plan = Assert.IsType<RemuxPlan>(PlanBuilder.Build(new(inspection, retained.Select(track => track.Id).ToArray(),
            Path.Combine(workspace.Path, "not-published.mkv"), false)).Plan);
        var arguments = MkvArguments.BuildRemux(new RemuxRequest(plan, candidate, null)).ToList();
        arguments.InsertRange(arguments.Count - 1, ["--sync",
            $"{dts.Id.ToString(CultureInfo.InvariantCulture)}:{shiftMilliseconds.ToString(CultureInfo.InvariantCulture)}"]);
        var environment = tools.NativeLibraryDirectory is { } nativeLibraryDirectory
            ? new Dictionary<string, string> { ["LD_LIBRARY_PATH"] = nativeLibraryDirectory } : null;
        var remux = await new OwnedProcessRunner().RunAsync(new(tools.MkvMerge, arguments, maximumDiagnostics,
            TimeSpan.FromMinutes(2), null, TestContext.Current.CancellationToken, environment));
        Assert.Equal(0, remux.ExitCode);
        var verification = await backend.VerifyAsync(new(plan, candidate, TestContext.Current.CancellationToken));
        Assert.False(verification.Passed);
        Assert.Contains("timestamps", verification.Details, StringComparison.Ordinal);
        var output = await backend.InspectAsync(new(candidate, TestContext.Current.CancellationToken));
        var actualDts = Assert.Single(output.Tracks, track => track.Uid == dts.Uid);
        await MediaPayloadEvidence.AssertIdenticalAsync(new(source, dts.Id, candidate, actualDts.Id,
            workspace.Path, "shifted-dts"));
        Assert.Equal(sourceHash, await HashAsync(source));
        Assert.False(File.Exists(plan.DestinationPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
    }

    /// <summary>Hashes complete original bytes without decoding or rewriting media.</summary>
    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }
}
