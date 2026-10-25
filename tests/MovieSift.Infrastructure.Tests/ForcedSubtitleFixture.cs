using System.Globalization;
using MovieSift.Infrastructure.MediaTools;
using MovieSift.Infrastructure.Processes;
using Xunit;

namespace MovieSift.Infrastructure.Tests;

/// <summary>Creates two original subtitle payloads whose forced flags switch under a deliberate input permutation.</summary>
internal static class ForcedSubtitleFixture
{
    internal const string SubtitleTitle = "Captions";
    private const int OriginalSubtitleId = 3;
    private const int AddedSubtitleId = 4;
    private const int MaximumDiagnosticsCharacters = 32768;
    private static readonly TimeSpan FixtureTimeout = TimeSpan.FromMinutes(2);
    private const string AddedSubtitleText = "1\n00:00:00,125 --> 00:00:01,625\nOriginal added forced-switch subtitle.\n\n";

    /// <summary>Adds one tiny SRT to the existing synthetic ordinary fixture and flips only forced flags in the reordered copy.</summary>
    internal static async Task<ForcedSubtitleFixturePair> CreateAsync(ForcedSubtitleFixtureRequest request)
    {
        var first = Path.Combine(request.Directory, "A forced субтитры.mkv");
        var second = Path.Combine(request.Directory, "B forced перестановка.mkv");
        var subtitles = Path.Combine(request.Directory, "original added subtitles.srt");
        var originalSubtitleId = OriginalSubtitleId.ToString(CultureInfo.InvariantCulture);
        var addedSubtitleId = AddedSubtitleId.ToString(CultureInfo.InvariantCulture);
        await File.WriteAllTextAsync(subtitles, AddedSubtitleText, TestContext.Current.CancellationToken);
        await MergeAsync([
            "--output", first, "--track-order", "0:0,0:1,0:2,0:3,1:0",
            "--language", $"{originalSubtitleId}:eng", "--track-name", $"{originalSubtitleId}:{SubtitleTitle}",
            "--forced-display-flag", $"{originalSubtitleId}:1", request.TemplatePath,
            "--language", "0:eng", "--track-name", $"0:{SubtitleTitle}", "--forced-display-flag", "0:0",
            "--default-track-flag", "0:0", "--track-enabled-flag", "0:1",
            subtitles]);
        await MergeAsync([
            "--output", second, "--track-order", "0:4,0:2,0:3,0:0,0:1",
            "--forced-display-flag", $"{originalSubtitleId}:0", "--forced-display-flag", $"{addedSubtitleId}:1", first]);
        return new(first, second);
    }

    /// <summary>Runs the pinned native muxer with the existing statistics/locale policy and no shell parsing.</summary>
    private static async Task MergeAsync(IReadOnlyList<string> arguments)
    {
        var tools = MkvToolPaths.FromDirectory(MediaTestEnvironment.ToolDirectory);
        var environment = new Dictionary<string, string>();

        if (tools.NativeLibraryDirectory is { } libraries)
        {
            environment["LD_LIBRARY_PATH"] = libraries;
        }

        if (OperatingSystem.IsLinux())
        {
            environment["LC_ALL"] = "C.UTF-8";
            environment["LANG"] = "C.UTF-8";
        }

        var policyArguments = new[] { "--disable-track-statistics-tags", "--normalize-language-ietf", "off" };
        var result = await new OwnedProcessRunner().RunAsync(new(tools.MkvMerge, policyArguments.Concat(arguments).ToArray(),
            MaximumDiagnosticsCharacters, FixtureTimeout, null, TestContext.Current.CancellationToken, environment));
        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
    }
}

internal sealed record ForcedSubtitleFixtureRequest(string TemplatePath, string Directory);
internal sealed record ForcedSubtitleFixturePair(string First, string Second);
