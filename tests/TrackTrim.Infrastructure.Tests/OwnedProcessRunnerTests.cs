using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using TrackTrim.Infrastructure.Processes;
using Xunit;

namespace TrackTrim.Infrastructure.Tests;

/// <summary>Tests actual OS processes for bounded drainage, argument safety, direct cancellation, and descendants.</summary>
public sealed class OwnedProcessRunnerTests
{
    private const int OutputBoundCharacters = 1024;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Proves bounded retention while both real OS pipes exceed ordinary buffer capacity.</summary>
    [Fact]
    public async Task DrainsBothPipesBeyondTheirCapacityWithBoundedRetainedDiagnostics()
    {
        var result = await new OwnedProcessRunner().RunAsync(WorkerRequest(new WorkerOptions(["flood"])));
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.OutputTruncated);
        Assert.Equal(OutputBoundCharacters, result.StandardOutput.Length);
        Assert.Equal(OutputBoundCharacters, result.StandardError.Length);
    }

    /// <summary>Proves that argument arrays preserve values without shell interpretation.</summary>
    [Fact]
    public async Task ArgumentListRoundTripsUnicodeQuotesMetacharactersAndLeadingDashes()
    {
        string[] values = ["space path", "Русский файл", "& ; $(not-a-command)", "\"quotes\"", "--leading", "@option-file"];
        var result = await new OwnedProcessRunner().RunAsync(WorkerRequest(new WorkerOptions(["echo", .. values])));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(values, JsonSerializer.Deserialize<string[]>(result.StandardOutput));
    }

    /// <summary>Checks direct and descendant process liveness after deterministic cancellation.</summary>
    [Theory]
    [InlineData("wait", false)]
    [InlineData("tree", true)]
    public async Task CancellationReapsOwnedParentAndItsActualChild(string mode, bool expectsChild)
    {
        using var cancellationSource = new CancellationTokenSource();
        var parentReady = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var childReady = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = WorkerRequest(new WorkerOptions([mode], line =>
        {

            if (line.StartsWith("PARENT:", StringComparison.Ordinal))
            {
                parentReady.TrySetResult(int.Parse(line.AsSpan("PARENT:".Length), CultureInfo.InvariantCulture));
            }

            if (line.StartsWith("CHILD:", StringComparison.Ordinal))
            {
                childReady.TrySetResult(int.Parse(line.AsSpan("CHILD:".Length), CultureInfo.InvariantCulture));
            }
        }, cancellationSource.Token));
        var execution = new OwnedProcessRunner().RunAsync(request);
        var parentIdentifier = await parentReady.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        var childIdentifier = expectsChild ? await childReady.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken) : (int?)null;
        await cancellationSource.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        Assert.True(HasExited(parentIdentifier));

        if (childIdentifier is { } identifier)
        {
            Assert.True(HasExited(identifier));
        }
    }

    /// <summary>Distinguishes a simulated exit-one outcome from real media-warning evidence.</summary>
    [Fact]
    public async Task PreservesControlledWarningExitWithoutCallingItMediaSuccess()
    {
        var result = await new OwnedProcessRunner().RunAsync(WorkerRequest(new WorkerOptions(["warning"])));
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("not a real mkvmerge warning fixture", result.StandardError, StringComparison.Ordinal);
    }

    /// <summary>Builds a controlled helper invocation using the exact runtime hosting the test suite.</summary>
    private static ProcessRequest WorkerRequest(WorkerOptions options)
    {
        var runtimeDirectory = new DirectoryInfo(Path.GetDirectoryName(typeof(object).Assembly.Location)!);
        var dotnetDirectory = runtimeDirectory.Parent!.Parent!.Parent!.FullName;
        var dotnetPath = Path.Combine(dotnetDirectory, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var helperPath = Path.Combine(MediaTestEnvironment.RepositoryDirectory, "tests", "TrackTrim.ProcessTestWorker",
            "bin", configuration, "net10.0", "TrackTrim.ProcessTestWorker.dll");
        Assert.True(File.Exists(helperPath), $"The controlled process helper must be built: {helperPath}");
        return new ProcessRequest(dotnetPath, [helperPath, .. options.Arguments], OutputBoundCharacters,
            TestTimeout, options.LineCallback, options.CancellationToken);
    }

    /// <summary>Checks actual process liveness after runner cancellation has completed.</summary>
    private static bool HasExited(int identifier)
    {
        try
        {
            using var process = Process.GetProcessById(identifier);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private sealed record WorkerOptions(IReadOnlyList<string> Arguments, Action<string>? LineCallback = null,
        CancellationToken CancellationToken = default);
}
