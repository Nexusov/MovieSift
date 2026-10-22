using System.Globalization;
using System.Security.Cryptography;
using TrackTrim.Infrastructure.MediaTools;
using TrackTrim.Infrastructure.Processes;
using Xunit;

namespace TrackTrim.Infrastructure.Tests;

/// <summary>Checks actual extracted retained stream bytes, including text-subtitle payloads.</summary>
internal static class MediaPayloadEvidence
{
    private const int MaximumDiagnosticsCharacters = 32768;
    private static readonly TimeSpan ExtractionTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Extracts both approved stream identities and compares their complete encoded payload hashes.</summary>
    internal static async Task AssertIdenticalAsync(PayloadComparison request)
    {
        var sourcePayload = Path.Combine(request.OutputDirectory, request.Label + ".source.payload");
        var outputPayload = Path.Combine(request.OutputDirectory, request.Label + ".output.payload");
        await ExtractAsync(new PayloadExtraction(request.SourcePath, request.SourceIdentifier, sourcePayload));
        await ExtractAsync(new PayloadExtraction(request.OutputPath, request.OutputIdentifier, outputPayload));
        Assert.True(new FileInfo(sourcePayload).Length != 0, "Empty extraction cannot establish retained payload preservation.");
        Assert.Equal(await HashAsync(sourcePayload), await HashAsync(outputPayload));
    }

    /// <summary>Uses the pinned native extractor with explicit paths and the production UTF-8 locale contract.</summary>
    private static async Task ExtractAsync(PayloadExtraction request)
    {
        var tools = MkvToolPaths.FromDirectory(MediaTestEnvironment.ToolDirectory);
        var environment = new Dictionary<string, string>();

        if (tools.NativeLibraryDirectory is { } nativeLibraryDirectory)
        {
            environment["LD_LIBRARY_PATH"] = nativeLibraryDirectory;
        }

        if (OperatingSystem.IsLinux())
        {
            environment["LC_ALL"] = "C.UTF-8";
            environment["LANG"] = "C.UTF-8";
        }

        var result = await new OwnedProcessRunner().RunAsync(new ProcessRequest(tools.MkvExtract,
            [request.SourcePath, "tracks", $"{request.Identifier.ToString(CultureInfo.InvariantCulture)}:{request.DestinationPath}"],
            MaximumDiagnosticsCharacters, ExtractionTimeout, null, TestContext.Current.CancellationToken, environment));
        Assert.True(result.ExitCode == 0, result.StandardError);
    }

    /// <summary>Hashes the complete extracted file without decoding or rewriting it.</summary>
    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private sealed record PayloadExtraction(string SourcePath, int Identifier, string DestinationPath);
}

internal sealed record PayloadComparison(string SourcePath, int SourceIdentifier,
    string OutputPath, int OutputIdentifier, string OutputDirectory, string Label);
