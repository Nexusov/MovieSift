using System.Diagnostics;
using System.Globalization;

namespace MovieSift.Infrastructure.Processes;

/// <summary>Observes only the Linux descendants identified under an owned worker before its existing tree termination.</summary>
internal sealed class LinuxProcessTreeObservation : IDisposable
{
    private const int MaximumDescendants = 256;
    private const int MaximumTasks = 4_096;
    private const int MaximumMetadataBytes = 16_384;
    private const int MinimumStatFields = 20;
    private const int ParentField = 1;
    private const int BirthField = 19;
    private static readonly TimeSpan ObservationInterval = TimeSpan.FromMilliseconds(25);
    private readonly List<LinuxProcessIdentity> identities = [];
    private readonly List<Process> retainedProcesses = [];

    /// <summary>Proves ancestry and birth identity without using diagnostic output or issuing any process signals.</summary>
    internal static LinuxProcessTreeObservation Capture(Process owner, CancellationToken cancellationToken)
    {
        var result = new LinuxProcessTreeObservation();
        try
        {
            var completedBeforeObservation = owner.WaitForExit(TimeSpan.Zero);

            if (completedBeforeObservation && owner.ExitCode == 0)
            {
                // Preserve the existing normal-completion contract; the runner still requires bounded pipe drainage.
                return result;
            }

            if (completedBeforeObservation)
            {
                throw new IOException("The worker exited before its descendants could be identified.");
            }
            var root = ReadState(owner.Id) ?? throw new IOException("The owned worker metadata is unavailable.");
            var pending = new Queue<LinuxProcessIdentity>();
            pending.Enqueue(new(root.ProcessId, root.ParentProcessId, root.BirthTicks));
            var seen = new HashSet<int> { owner.Id };
            var taskCount = 0;
            while (pending.TryDequeue(out var parent))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentParent = ReadState(parent.ProcessId);
                if (currentParent is null || currentParent.BirthTicks != parent.BirthTicks)
                {
                    throw new IOException("An owned process changed before descendant observation completed.");
                }
                foreach (var task in Directory.EnumerateDirectories(ProcPath(parent.ProcessId, "task")))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++taskCount > MaximumTasks)
                    {
                        throw new IOException("The owned task observation limit was exceeded.");
                    }
                    string children;
                    try
                    {
                        children = ReadMetadata(Path.Combine(task, "children"));
                    }
                    catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
                    {
                        if (TaskHasDisappeared(task))
                        {
                            continue;
                        }
                        throw;
                    }
                    foreach (var value in children.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var identifier) || identifier <= 0)
                        {
                            throw new IOException("An owned child identifier is malformed.");
                        }
                        if (!seen.Add(identifier))
                        {
                            continue;
                        }
                        if (result.identities.Count >= MaximumDescendants)
                        {
                            throw new IOException("The owned descendant observation limit was exceeded.");
                        }
                        var child = ReadState(identifier);
                        if (child is null || child.ParentProcessId != parent.ProcessId)
                        {
                            throw new IOException("An owned child identity could not be confirmed.");
                        }
                        var retained = Process.GetProcessById(identifier);
                        result.retainedProcesses.Add(retained);
                        _ = retained.SafeHandle;
                        var confirmed = ReadState(identifier);
                        if (confirmed is null || confirmed.BirthTicks != child.BirthTicks || confirmed.ParentProcessId != parent.ProcessId)
                        {
                            throw new IOException("An owned child identity changed during capture.");
                        }
                        var identity = new LinuxProcessIdentity(identifier, parent.ProcessId, child.BirthTicks);
                        result.identities.Add(identity);
                        pending.Enqueue(identity);
                    }
                }

                var confirmedParent = ReadState(parent.ProcessId);
                if (confirmedParent is null || confirmedParent.BirthTicks != parent.BirthTicks)
                {
                    throw new IOException("An owned ancestor changed during descendant capture.");
                }
            }
            if (owner.WaitForExit(TimeSpan.Zero))
            {
                throw new IOException("The worker exited before descendant observation completed.");
            }
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    /// <summary>Waits until every captured identity can no longer write, without waiting for unrelated init-process zombie reaping.</summary>
    internal async Task WaitAsync(CancellationToken cancellationToken)
    {
        while (!HaveTerminated(identities, ReadState))
        {
            await Task.Delay(ObservationInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A reused PID is not the captured child; stopped and runnable identities are never accepted as terminated.</summary>
    internal static bool IsTerminated(LinuxProcessIdentity identity, LinuxProcessState? current) =>
        current is null || current.BirthTicks != identity.BirthTicks || current.State is 'Z' or 'X';

    /// <summary>Checks captured identities through the same read-only observation policy used by the bounded wait.</summary>
    internal static bool HaveTerminated(IReadOnlyList<LinuxProcessIdentity> captured, Func<int, LinuxProcessState?> readState) =>
        captured.All(identity => IsTerminated(identity, readState(identity.ProcessId)));

    /// <summary>Parses kernel stat fields after the final command-name parenthesis, preserving names containing spaces and parentheses.</summary>
    internal static LinuxProcessState ParseStat(string text)
    {
        var prefixEnd = text.IndexOf(' ');
        var commandEnd = text.LastIndexOf(')');
        var invalidHeader = prefixEnd <= 0 || commandEnd <= prefixEnd || commandEnd + 2 >= text.Length;
        if (invalidHeader || text[prefixEnd + 1] != '(' || text[commandEnd + 1] != ' ')
        {
            throw new IOException("Owned process metadata has an invalid header.");
        }
        var fields = text[(commandEnd + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var identifier = 0;
        var parent = 0;
        ulong birth = 0;
        var valid = fields.Length >= MinimumStatFields && fields[0].Length == 1 &&
            int.TryParse(text.AsSpan(0, prefixEnd), NumberStyles.None, CultureInfo.InvariantCulture, out identifier) && identifier > 0 &&
            int.TryParse(fields[ParentField], NumberStyles.None, CultureInfo.InvariantCulture, out parent) && parent >= 0 &&
            ulong.TryParse(fields[BirthField], NumberStyles.None, CultureInfo.InvariantCulture, out birth) && birth > 0;
        if (!valid)
        {
            throw new IOException("Owned process metadata has invalid identity fields.");
        }
        return new(identifier, parent, birth, fields[0][0]);
    }

    /// <summary>Releases retained observations only; it never terminates or signals a process.</summary>
    public void Dispose()
    {
        foreach (var process in retainedProcesses)
        {
            process.Dispose();
        }
    }

    /// <summary>Reads the same kernel identity, distinguishing a gone process from inaccessible metadata.</summary>
    private static LinuxProcessState? ReadState(int identifier)
    {
        try
        {
            var state = ParseStat(ReadMetadata(ProcPath(identifier, "stat")));
            if (state.ProcessId != identifier)
            {
                throw new IOException("Owned process metadata does not match its pathname.");
            }
            return state;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Bounds each trusted procfs record before parsing, including task child lists.</summary>
    private static string ReadMetadata(string path)
    {
        using var stream = File.OpenRead(path);
        var bytes = new byte[MaximumMetadataBytes + 1];
        var count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        if (count > MaximumMetadataBytes)
        {
            throw new IOException("Owned process metadata exceeds its observation limit.");
        }
        return System.Text.Encoding.UTF8.GetString(bytes, 0, count);
    }

    /// <summary>Only a genuinely vanished task permits a missing children record to be ignored.</summary>
    private static bool TaskHasDisappeared(string task)
    {
        try
        {
            _ = File.GetAttributes(task);
            return false;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
    }

    /// <summary>Builds fixed kernel paths only from already validated numeric process identifiers.</summary>
    private static string ProcPath(int identifier, string name) => $"/proc/{identifier.ToString(CultureInfo.InvariantCulture)}/{name}";
}

internal sealed record LinuxProcessIdentity(int ProcessId, int ParentProcessId, ulong BirthTicks);
internal sealed record LinuxProcessState(int ProcessId, int ParentProcessId, ulong BirthTicks, char State);
