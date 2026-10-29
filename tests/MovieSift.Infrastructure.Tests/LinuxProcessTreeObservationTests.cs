using System.Globalization;
using MovieSift.Infrastructure.Processes;
using Xunit;

namespace MovieSift.Infrastructure.Tests;

/// <summary>Checks Linux identity parsing and termination decisions without requiring Linux or issuing signals.</summary>
public sealed class LinuxProcessTreeObservationTests
{
    private const int ProcessIdentifier = 12402;
    private const int ParentIdentifier = 12390;
    private const ulong BirthTicks = 987654;

    /// <summary>Kernel command names may contain spaces and parentheses before the stable identity fields.</summary>
    [Fact]
    public void ParsesBirthAndParentAfterTheFinalCommandParenthesis()
    {
        var state = LinuxProcessTreeObservation.ParseStat(Stat("media ) worker (child)", 'R'));
        Assert.Equal(new LinuxProcessState(ProcessIdentifier, ParentIdentifier, BirthTicks, 'R'), state);
    }

    /// <summary>Missing or malformed identity fields never become a guessed process observation.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("12402 (worker) R 12390")]
    [InlineData("12402 worker) R 12390")]
    [InlineData("12402 (worker)R 12390")]
    public void RejectsMalformedStatRecords(string record)
    {
        Assert.Throws<IOException>(() => LinuxProcessTreeObservation.ParseStat(record));
    }

    /// <summary>Runnable, sleeping and stopped descendants remain owned writers until actual termination is observed.</summary>
    [Theory]
    [InlineData('R', false)]
    [InlineData('S', false)]
    [InlineData('D', false)]
    [InlineData('T', false)]
    [InlineData('Z', true)]
    [InlineData('X', true)]
    public void AcceptsOnlyTerminatedCapturedStates(char state, bool expected)
    {
        var identity = new LinuxProcessIdentity(ProcessIdentifier, ParentIdentifier, BirthTicks);
        var current = new LinuxProcessState(ProcessIdentifier, ParentIdentifier, BirthTicks, state);
        Assert.Equal(expected, LinuxProcessTreeObservation.IsTerminated(identity, current));
    }

    /// <summary>A missing process or a different birth at the same PID cannot be the captured descendant.</summary>
    [Fact]
    public void RecognizesDisappearanceAndPidReuseWithoutSignallingTheReplacement()
    {
        var identity = new LinuxProcessIdentity(ProcessIdentifier, ParentIdentifier, BirthTicks);
        Assert.True(LinuxProcessTreeObservation.IsTerminated(identity, null));
        Assert.True(LinuxProcessTreeObservation.IsTerminated(identity,
            new(ProcessIdentifier, ParentIdentifier, BirthTicks + 1, 'R')));
    }

    /// <summary>The production observation policy waits for every captured identity, including one still runnable after its parent exits.</summary>
    [Fact]
    public void ControlledObservationDoesNotFinishUntilTheFinalCapturedChildStops()
    {
        var first = new LinuxProcessIdentity(ProcessIdentifier, ParentIdentifier, BirthTicks);
        var second = new LinuxProcessIdentity(ProcessIdentifier + 1, ProcessIdentifier, BirthTicks + 1);
        LinuxProcessIdentity[] captured = [first, second];
        var current = new Dictionary<int, LinuxProcessState?>
        {
            [first.ProcessId] = null,
            [second.ProcessId] = new(second.ProcessId, second.ParentProcessId, second.BirthTicks, 'R'),
        };
        Assert.False(LinuxProcessTreeObservation.HaveTerminated(captured, identifier => current[identifier]));
        current[second.ProcessId] = current[second.ProcessId]! with { State = 'Z' };
        Assert.True(LinuxProcessTreeObservation.HaveTerminated(captured, identifier => current[identifier]));
    }

    /// <summary>Inaccessible owned metadata remains uncertainty instead of being converted into an empty tree.</summary>
    [Fact]
    public void ObservationFailureDoesNotMeanTermination()
    {
        LinuxProcessIdentity[] captured = [new(ProcessIdentifier, ParentIdentifier, BirthTicks)];
        Assert.Throws<IOException>(() => LinuxProcessTreeObservation.HaveTerminated(captured,
            _ => throw new IOException("Controlled procfs observation failure.")));
    }

    /// <summary>Creates only the stat identity fields used by the production parser.</summary>
    private static string Stat(string command, char state)
    {
        var fields = new string[20];
        Array.Fill(fields, "0");
        fields[0] = state.ToString();
        fields[1] = ParentIdentifier.ToString(CultureInfo.InvariantCulture);
        fields[19] = BirthTicks.ToString(CultureInfo.InvariantCulture);
        return $"{ProcessIdentifier.ToString(CultureInfo.InvariantCulture)} ({command}) {string.Join(' ', fields)}";
    }
}
