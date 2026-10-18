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
    private const int ProcessStateOffset = 2;
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
        using var parent = ObserveActiveProcess(parentIdentifier);
        using var child = childIdentifier is { } identifier ? ObserveActiveProcess(identifier) : null;
        await cancellationSource.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        await AssertExitedAsync(parent);

        if (child is not null)
        {
            AssertNoActiveLinuxProcess(child);
            await AssertExitedAsync(child);
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

    /// <summary>Reaps an already-started worker when a process-lifecycle observer itself fails.</summary>
    [Fact]
    public async Task FailingStartObserverCannotLeaveItsWorkerRunning()
    {
        Process? observedWorker = null;
        var request = WorkerRequest(new WorkerOptions(["wait"])) with
        {
            ProcessStarted = identifier =>
            {
                observedWorker = Process.GetProcessById(identifier);
                _ = observedWorker.SafeHandle;
                Assert.False(observedWorker.HasExited);
                throw new InvalidOperationException("controlled lifecycle observer failure");
            },
        };

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => new OwnedProcessRunner().RunAsync(request));
            Assert.Equal("controlled lifecycle observer failure", exception.Message);
            Assert.NotNull(observedWorker);
            await observedWorker.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            Assert.True(observedWorker.HasExited);
        }
        finally
        {
            observedWorker?.Dispose();
        }
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

    /// <summary>Retains the actual live process instance before cancellation rather than looking up a later PID.</summary>
    private static Process ObserveActiveProcess(int identifier)
    {
        var process = Process.GetProcessById(identifier);
        _ = process.SafeHandle;
        Assert.False(process.HasExited, "The controlled worker must be observed alive before cancellation.");
        return process;
    }

    /// <summary>Rejects a genuinely active Linux descendant before waiting for non-child process-table reaping.</summary>
    private static void AssertNoActiveLinuxProcess(Process process)
    {

        if (!OperatingSystem.IsLinux() || process.HasExited)
        {
            return;
        }

        var state = LinuxProcessState(process);
        Assert.True(state is "Z" or "X" or "exited",
            $"The runner returned while its observed descendant {process.Id} remained active (Linux state {state}).");
    }

    /// <summary>Awaits termination of the same retained process with a bound instead of racing an immediate PID lookup.</summary>
    private static async Task AssertExitedAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            Assert.Fail($"The retained worker {process.Id} did not report exit within {TestTimeout}; Linux state: {LinuxProcessState(process)}.");
        }

        Assert.True(process.HasExited);
    }

    /// <summary>Records Linux's actual process-table state without interpreting a zombie as an active writer.</summary>
    private static string LinuxProcessState(Process process)
    {

        if (!OperatingSystem.IsLinux())
        {
            return "not Linux";
        }

        try
        {
            var status = File.ReadAllText($"/proc/{process.Id.ToString(CultureInfo.InvariantCulture)}/stat");
            var commandEnd = status.LastIndexOf(')');
            return status[(commandEnd + ProcessStateOffset)..].Split(' ')[0];
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return "exited";
        }
    }

    private sealed record WorkerOptions(IReadOnlyList<string> Arguments, Action<string>? LineCallback = null,
        CancellationToken CancellationToken = default);
}
