using Avalonia.Headless.XUnit;
using MovieSift.Application;
using MovieSift.Core.Failures;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace MovieSift.Desktop.Tests;

/// <summary>Protects operational diagnostics for failures returned as values rather than unexpected exceptions.</summary>
public sealed class BatchFailureLoggingTests
{
    /// <summary>Inspection failures remain authoritative and are not re-emitted by unrelated later operations.</summary>
    [AvaloniaFact]
    public async Task ReturnedInspectionFailureIsLoggedOnceWithoutChangingIt()
    {
        var sink = new CapturedEvents();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var fixture = new BatchViewModelFixture(logger: logger);
        var source = fixture.Dialogs.Sources[0];
        var expected = new Failure(ErrorCode.UnsupportedFeature, "Controlled linked-segment rejection", ErrorReason.LinkedSegments);
        fixture.Backend.InspectionFailures[source] = expected;
        await fixture.LoadAsync();
        var item = fixture.Batch.Items.Single(item => item.SourcePath == source);
        Assert.Same(expected, item.Snapshot.Failure);
        var entry = Assert.Single(sink.Events, entry => Scalar(entry, "Code") is ErrorCode.UnsupportedFeature);
        Assert.Equal(expected.Reason, Scalar(entry, "Reason"));
        Assert.Equal(expected.Detail, Scalar(entry, "Detail"));
        Assert.Equal(item.Id, Scalar(entry, "ItemId"));
        await fixture.Batch.ChooseOutputCommand.ExecuteAsync(null);
        Assert.Single(sink.Events, entry => Scalar(entry, "Code") is ErrorCode.UnsupportedFeature);
        Assert.Same(expected, item.Snapshot.Failure);
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>Expected destination failures are logged even though review catches them into a result.</summary>
    [AvaloniaFact]
    public async Task ReturnedReviewFailureIncludesItsActualCodeAndDetail()
    {
        var sink = new CapturedEvents();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var fixture = new BatchViewModelFixture(logger: logger);
        await fixture.LoadAsync();
        fixture.Batch.Items[0].Tracks.First(track => !track.IsRequired).Keep = false;
        var expected = new Failure(ErrorCode.DestinationExists, "Controlled collision, sentinel remains untouched");
        fixture.SourceAccess.OnValidateDestination = _ => throw new OperationFailureException(expected);
        await fixture.Batch.ReviewCommand.ExecuteAsync(null);
        Assert.False(fixture.Batch.CanRun);
        Assert.True(fixture.Batch.HasError);
        Assert.Contains(sink.Events, entry => Scalar(entry, "Code") is ErrorCode.DestinationExists &&
            Equals(Scalar(entry, "Detail"), expected.Detail));
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>A worker failure retains its raw diagnostic evidence in the event and in the existing result.</summary>
    [AvaloniaFact]
    public async Task ReturnedWorkerFailureIncludesExitAndDiagnostics()
    {
        const int failureExitCode = 2;
        var sink = new CapturedEvents();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var fixture = new BatchViewModelFixture(logger: logger);
        await fixture.PrepareAsync();
        var source = fixture.Batch.Items[0].SourcePath;
        fixture.Backend.ExitCodes[source] = failureExitCode;
        await fixture.Batch.RunCommand.ExecuteAsync(null);
        var result = fixture.Batch.Items[0].Snapshot.Result!;
        Assert.Equal(ErrorCode.ToolFailed, result.Failure!.Code);
        var entry = Assert.Single(sink.Events, entry => Scalar(entry, "ToolExitCode") is failureExitCode);
        Assert.Equal(result.Tool!.Diagnostics, Scalar(entry, "ToolDiagnostics"));
        Assert.Equal(result.Failure.Detail, Scalar(entry, "Detail"));
        Assert.Single(fixture.Backend.RemuxRequests);
    }

    /// <summary>A successful worker cannot hide a later preservation-verification failure from the log.</summary>
    [AvaloniaFact]
    public async Task VerificationRejectionIncludesItsActualMismatchWithSuccessfulWorkerExit()
    {
        var sink = new CapturedEvents();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var fixture = new BatchViewModelFixture(logger: logger);
        await fixture.PrepareAsync();
        fixture.Backend.Verification = new(false, "Controlled retained track properties changed");
        await fixture.Batch.RunCommand.ExecuteAsync(null);
        var result = fixture.Batch.Items[0].Snapshot.Result!;
        Assert.Equal(ErrorCode.VerificationFailed, result.Failure!.Code);
        var entry = Assert.Single(sink.Events, entry => Scalar(entry, "Code") is ErrorCode.VerificationFailed);
        Assert.Equal(0, Scalar(entry, "ToolExitCode"));
        Assert.Equal(result.Verification!.Details, Scalar(entry, "VerificationDetails"));
        Assert.Equal(result.Failure.Detail, Scalar(entry, "Detail"));
        Assert.Null(result.PublishedPath);
        Assert.Single(fixture.Backend.RemuxRequests);
    }

    /// <summary>Returned failures reach an actual portable-style log file after its buffered sink is flushed.</summary>
    [AvaloniaFact]
    public async Task ReturnedFailureIsWrittenToTheFileSinkWithoutChangingIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MovieSift diagnostic tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "moviesift.log");

        try
        {
            var expected = new Failure(ErrorCode.ToolUnavailable, "Controlled file diagnostic evidence");

            using (var logger = new LoggerConfiguration().WriteTo.File(path).CreateLogger())
            {
                var fixture = new BatchViewModelFixture(logger: logger);
                var source = fixture.Dialogs.Sources[0];
                fixture.Backend.InspectionFailures[source] = expected;
                await fixture.LoadAsync();
                Assert.Same(expected, fixture.Batch.Items.Single(item => item.SourcePath == source).Snapshot.Failure);
                Assert.Empty(fixture.Backend.RemuxRequests);
            }

            var contents = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.NotEmpty(contents);
            Assert.Contains(nameof(ErrorCode.ToolUnavailable), contents, StringComparison.Ordinal);
            Assert.Contains(expected.Detail, contents, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>A faulty diagnostic sink cannot hide the original failure or leave the command busy.</summary>
    [AvaloniaFact]
    public async Task SinkFailureCannotReplaceAnInspectionFailure()
    {
        using var logger = new LoggerConfiguration().WriteTo.Sink(new FailingSink()).CreateLogger();
        var fixture = new BatchViewModelFixture(logger: logger);
        var source = fixture.Dialogs.Sources[0];
        var expected = new Failure(ErrorCode.ToolUnavailable, "Controlled missing tool");
        fixture.Backend.InspectionFailures[source] = expected;
        await fixture.LoadAsync();
        Assert.Same(expected, fixture.Batch.Items.Single(item => item.SourcePath == source).Snapshot.Failure);
        Assert.False(fixture.Batch.IsBusy);
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>Reads typed structured properties instead of depending on localized rendered messages.</summary>
    private static object? Scalar(LogEvent entry, string name) =>
        entry.Properties.TryGetValue(name, out var value) && value is ScalarValue scalar ? scalar.Value : null;

    /// <summary>Collects actual Serilog events produced by the existing desktop logger.</summary>
    private sealed class CapturedEvents : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        /// <summary>Retains each event for deterministic assertions.</summary>
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    /// <summary>Models an unavailable diagnostic destination without throwing from the media backend.</summary>
    private sealed class FailingSink : ILogEventSink
    {
        /// <summary>Rejects writes so the authoritative operation outcome can be checked independently.</summary>
        public void Emit(LogEvent logEvent) => throw new IOException("Controlled diagnostic sink failure");
    }
}
