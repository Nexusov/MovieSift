using System.Globalization;
using MovieSift.Application;
using MovieSift.Application.Ports;
using MovieSift.Core.Failures;
using MovieSift.Core.Media;
using MovieSift.Core.Planning;
using MovieSift.Infrastructure.Files;
using MovieSift.Infrastructure.Processes;

namespace MovieSift.Infrastructure.MediaTools;

/// <summary>Materializes proven missing DTS durations only in a separately reserved, identity-bound input copy.</summary>
internal sealed class MkvDtsInputProjection(MkvDtsProjectionServices services)
{
    private const int CopyBufferBytes = 1024 * 1024;
    private const int MaximumDiagnosticsCharacters = 32768;

    /// <summary>Leaves no-change jobs and tracks with an existing duration on their established lifecycle.</summary>
    internal static bool RequiresProjection(RemuxPlan plan) => !plan.IsNoChange && plan.RetainedTracks.Any(NeedsDuration);

    /// <summary>Validates every affected core frame, copies without opening the original for writing, then invokes the pinned native header editor.</summary>
    internal async Task PrepareAsync(PrepareInputRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        var expected = request.Plan.Inspection.Source;

        if (request.Plan.Inspection.Container != MediaContainer.Matroska || request.InputProjection.OriginalSource != expected)
        {
            throw new OperationFailureException(new(ErrorCode.OwnershipInvalid, "The reserved input copy does not belong to this source plan."));
        }

        var targets = request.Plan.RetainedTracks.Where(NeedsDuration).ToArray();

        if (targets.Length == 0)
        {
            throw new OperationFailureException(new(ErrorCode.InvalidSelection, "This plan does not require a DTS input projection."));
        }

        await ValidateOriginalAsync(expected, request.CancellationToken).ConfigureAwait(false);
        OwnedInputProjectionAccess.Validate(request.InputProjection);
        var edits = new List<(ulong Uid, ulong Duration)>();

        foreach (var track in targets)
        {
            var validIdentity = track.Uid is > 0 && request.Plan.Inspection.Tracks.Count(other => other.Uid == track.Uid) == 1;
            var validNumber = ulong.TryParse(track.Properties.GetValueOrDefault("number"), NumberStyles.None,
                CultureInfo.InvariantCulture, out var number) && number > 0;

            if (!validIdentity || !validNumber || !DtsDefaultDurationPolicy.TryReadHeader(track, out var header))
            {
                throw Unsupported("A retained DTS track needs a unique track identity and supported core-frame evidence before its missing duration can be prepared.");
            }

            var proven = await DtsLacedTimestampEvidence.ValidateProfileAsync(new(expected.FullPath, number, header,
                request.CancellationToken)).ConfigureAwait(false);

            if (!proven)
            {
                throw Unsupported("The retained DTS track's frame durations or lacing cannot be established safely; no media processing was started.");
            }

            edits.Add((track.Uid!.Value, DtsDefaultDurationPolicy.RoundedDuration(header)));
        }

        await ValidateOriginalAsync(expected, request.CancellationToken).ConfigureAwait(false);
        await CopyAsync(request).ConfigureAwait(false);
        await ValidateOriginalAsync(expected, request.CancellationToken).ConfigureAwait(false);
        OwnedInputProjectionAccess.Validate(request.InputProjection);
        await ValidateEditorAsync(request.CancellationToken).ConfigureAwait(false);
        var arguments = new List<string> { "--output-charset", "UTF-8", request.InputProjection.CreatedFile.FullPath };

        foreach (var (uid, duration) in edits)
        {
            arguments.AddRange(["--edit", "track:=" + uid.ToString(CultureInfo.InvariantCulture), "--set",
                "default-duration=" + duration.ToString(CultureInfo.InvariantCulture)]);
        }

        var result = await services.RunAsync(new(services.MkvPropEditPath, arguments, MaximumDiagnosticsCharacters,
            null, null, request.CancellationToken)).ConfigureAwait(false);

        if (result.ExitCode != 0 || result.OutputTruncated || result.StandardErrorTruncated || result.ProtocolLineTruncated)
        {
            throw new OperationFailureException(new(ErrorCode.ToolFailed,
                $"The owned input copy's DTS header preparation did not complete cleanly (exit {result.ExitCode}). {result.StandardOutput} {result.StandardError}"));
        }

        request.CancellationToken.ThrowIfCancellationRequested();
        OwnedInputProjectionAccess.Validate(request.InputProjection);
        await ValidateOriginalAsync(expected, request.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>Copies to the existing reserved inode and reports actual bytes without deleting or rewriting ownership evidence.</summary>
    private async Task CopyAsync(PrepareInputRequest request)
    {
        var expected = request.Plan.Inspection.Source;
        await using var original = FileSourceAccess.OpenRegularReadStream(expected.FullPath, FileOptions.SequentialScan);
        var opened = FileSourceAccess.ReadRegularRevision(new(expected.FullPath, original.SafeFileHandle));

        if (opened != expected)
        {
            throw new OperationFailureException(new(ErrorCode.SourceChanged));
        }

        await using var copy = OwnedInputProjectionAccess.OpenReadWrite(request.InputProjection);

        if (copy.Length != 0)
        {
            throw new OperationFailureException(new(ErrorCode.OwnershipInvalid, "The reserved input copy is not empty; its existing content was preserved."));
        }

        var buffer = new byte[CopyBufferBytes];
        long copied = 0;
        request.Progress?.Report(0);

        while (true)
        {
            var count = await original.ReadAsync(buffer, request.CancellationToken).ConfigureAwait(false);

            if (count == 0)
            {
                break;
            }

            await copy.WriteAsync(buffer.AsMemory(0, count), request.CancellationToken).ConfigureAwait(false);
            copied += count;
            request.Progress?.Report(Math.Min(100, copied * 100d / expected.Length));
        }

        if (copied != expected.Length)
        {
            throw new OperationFailureException(new(ErrorCode.SourceChanged));
        }

        await copy.FlushAsync(request.CancellationToken).ConfigureAwait(false);
        copy.Flush(flushToDisk: true);
    }

    /// <summary>Requires the pinned executable before allowing it to edit the owned copy.</summary>
    private async Task ValidateEditorAsync(CancellationToken cancellationToken)
    {

        if (!Path.IsPathFullyQualified(services.MkvPropEditPath) || !File.Exists(services.MkvPropEditPath))
        {
            throw new OperationFailureException(new(ErrorCode.ToolUnavailable, "The bundled mkvpropedit executable is unavailable."));
        }

        var version = await services.RunAsync(new(services.MkvPropEditPath, ["--version"], MaximumDiagnosticsCharacters,
            TimeSpan.FromMinutes(2), null, cancellationToken)).ConfigureAwait(false);
        var expected = "mkvpropedit v" + MkvToolPaths.SupportedVersion + " ";

        if (version.ExitCode != 0 || version.OutputTruncated || version.StandardErrorTruncated ||
            !version.StandardOutput.StartsWith(expected, StringComparison.Ordinal))
        {
            throw new OperationFailureException(new(ErrorCode.ToolVersionMismatch,
                "The input projection requires the exact bundled MKVToolNix editor version."));
        }
    }

    /// <summary>Checks the original independently before and after every potentially long preparation boundary.</summary>
    private async Task ValidateOriginalAsync(SourceRevision expected, CancellationToken cancellationToken)
    {
        var current = await services.SourceAccess.ReadRevisionAsync(new(expected.FullPath, cancellationToken)).ConfigureAwait(false);

        if (current != expected)
        {
            throw new OperationFailureException(new(ErrorCode.SourceChanged));
        }
    }

    /// <summary>Targets the inspected DTS category only when its duration is genuinely absent.</summary>
    private static bool NeedsDuration(MediaTrack track) => DtsDefaultDurationPolicy.IsDts(track) &&
        !track.Properties.ContainsKey(DtsDefaultDurationPolicy.PropertyName);

    /// <summary>Reports an explicit unsupported representation instead of guessing a codec clock.</summary>
    private static OperationFailureException Unsupported(string detail) => new(new(ErrorCode.UnsupportedFeature, detail));
}

internal sealed record MkvDtsProjectionServices(ISourceAccess SourceAccess, string MkvPropEditPath,
    Func<ProcessRequest, Task<ProcessResult>> RunAsync);
