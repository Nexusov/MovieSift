using System.Diagnostics;
using System.Text;

namespace MovieSift.Infrastructure.Processes;

/// <summary>Owns a direct worker until exit, bounded drainage, and cancellation reaping complete.</summary>
internal sealed class OwnedProcessRunner
{
    private const int ReadBufferCharacters = 4096;
    private const int MaximumProtocolLineCharacters = 4096;
    private static readonly TimeSpan TerminationTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Runs an absolute executable without a shell and continuously drains both streams.</summary>
    public async Task<ProcessResult> RunAsync(ProcessRequest request)
    {

        if (!Path.IsPathFullyQualified(request.ExecutablePath))
        {
            throw new ArgumentException("The worker executable path must be absolute.", nameof(request));
        }

        request.CancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo(request.ExecutablePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in request.Environment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[name] = value;
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        try
        {
            request.ProcessStarted?.Invoke(process.Id);
        }
        catch
        {
            await TerminateAsync(process).ConfigureAwait(false);
            throw;
        }

        using var drainageSource = new CancellationTokenSource();
        var standardOutputTask = DrainAsync(new DrainRequest(
            process.StandardOutput, request.MaximumOutputCharacters, request.StandardOutputLine, drainageSource.Token));
        var standardErrorTask = DrainAsync(new DrainRequest(
            process.StandardError, request.MaximumOutputCharacters, null, drainageSource.Token));
        using var timeoutSource = new CancellationTokenSource();

        if (request.Timeout is { } timeout)
        {
            timeoutSource.CancelAfter(timeout);
        }

        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            request.CancellationToken, timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                await TerminateAsync(process).ConfigureAwait(false);
                await Task.WhenAll(standardOutputTask, standardErrorTask).WaitAsync(TerminationTimeout).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is ProcessTerminationException or TimeoutException)
            {
                await StopDrainageAsync(new DrainageShutdown(drainageSource, standardOutputTask, standardErrorTask)).ConfigureAwait(false);
                throw new ProcessTerminationException("The worker tree or its output pipes did not terminate safely.", exception);
            }

            request.CancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("The media inspection worker exceeded its time limit.");
        }

        try
        {
            await Task.WhenAll(standardOutputTask, standardErrorTask).WaitAsync(TerminationTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            await StopDrainageAsync(new DrainageShutdown(drainageSource, standardOutputTask, standardErrorTask)).ConfigureAwait(false);
            throw new ProcessTerminationException("A descendant may still own the worker output pipes.", exception);
        }

        var standardOutput = await standardOutputTask.ConfigureAwait(false);
        var standardError = await standardErrorTask.ConfigureAwait(false);
        request.CancellationToken.ThrowIfCancellationRequested();
        return new ProcessResult(process.ExitCode, standardOutput.Text, standardError.Text,
            standardOutput.Truncated || standardError.Truncated,
            standardError.Truncated, standardOutput.ProtocolLineTruncated);
    }

    /// <summary>Cancels pipe reads and observes both tasks when worker termination cannot be established.</summary>
    private static async Task StopDrainageAsync(DrainageShutdown request)
    {
        await request.CancellationSource.CancelAsync().ConfigureAwait(false);

        try
        {
            await Task.WhenAll(request.StandardOutput, request.StandardError).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected cancellation is observed; it does not establish process-tree termination.
        }
    }

    /// <summary>Terminates the owned worker tree and waits independently of the caller cancellation.</summary>
    private static async Task TerminateAsync(Process process)
    {
        try
        {

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            using var terminationSource = new CancellationTokenSource(TerminationTimeout);
            await process.WaitForExitAsync(terminationSource.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception
            or OperationCanceledException or AggregateException)
        {
            throw new ProcessTerminationException("The media worker may still be writing its candidate.", exception);
        }
    }

    /// <summary>Retains a bounded prefix while draining all data and parsing bounded progress lines.</summary>
    private static async Task<DrainResult> DrainAsync(DrainRequest request)
    {
        var buffer = new char[ReadBufferCharacters];
        var retained = new StringBuilder();
        var line = new StringBuilder();
        var truncated = false;
        var lineTruncated = false;
        var protocolLineTruncated = false;
        int charactersRead;

        while ((charactersRead = await request.Reader.ReadAsync(buffer, request.CancellationToken).ConfigureAwait(false)) != 0)
        {
            var available = Math.Max(0, request.MaximumCharacters - retained.Length);
            var keepCount = Math.Min(charactersRead, available);
            retained.Append(buffer, 0, keepCount);
            truncated |= keepCount != charactersRead;

            if (request.LineCallback is null)
            {
                continue;
            }

            for (var characterIndex = 0; characterIndex < charactersRead; characterIndex++)
            {
                var character = buffer[characterIndex];

                if (character is '\n' or '\r')
                {

                    if (!lineTruncated && line.Length != 0)
                    {
                        request.LineCallback(line.ToString());
                    }

                    line.Clear();
                    lineTruncated = false;
                    continue;
                }

                if (line.Length < MaximumProtocolLineCharacters)
                {
                    line.Append(character);
                    continue;
                }

                lineTruncated = true;
                protocolLineTruncated = true;
            }
        }

        if (!lineTruncated && line.Length != 0)
        {
            request.LineCallback?.Invoke(line.ToString());
        }

        return new DrainResult(retained.ToString(), truncated, protocolLineTruncated);
    }

    private sealed record DrainRequest(StreamReader Reader, int MaximumCharacters, Action<string>? LineCallback, CancellationToken CancellationToken);
    private sealed record DrainageShutdown(CancellationTokenSource CancellationSource, Task<DrainResult> StandardOutput, Task<DrainResult> StandardError);
    private sealed record DrainResult(string Text, bool Truncated, bool ProtocolLineTruncated);
}
