using System.Collections.Immutable;
using System.Globalization;
using MovieSift.Application;
using MovieSift.Application.Ports;
using MovieSift.Core.Failures;
using MovieSift.Core.Media;
using MovieSift.Infrastructure.Processes;

namespace MovieSift.Infrastructure.MediaTools;

public sealed record FfmpegBackendOptions(ISourceAccess SourceAccess, FfmpegToolPaths Tools)
{
    internal Action<int>? RemuxProcessStarted { get; init; }
}

/// <summary>Implements MP4-only stream copying with bounded capability checks, exact retained mapping and streamed packet verification.</summary>
public sealed class FfmpegBackend(FfmpegBackendOptions options) : IMediaBackend
{
    private const int MaximumDiagnosticsCharacters = 32768;
    private static readonly TimeSpan InspectionTimeout = TimeSpan.FromMinutes(2);
    private readonly OwnedProcessRunner processRunner = new();

    /// <summary>Checks actual MP4 boxes and the pinned probe rather than trusting an extension or MOV-family format label.</summary>
    public async Task<MediaInspection> InspectAsync(InspectRequest request)
    {
        await ValidateToolsAsync(request.CancellationToken).ConfigureAwait(false);
        var revision = await options.SourceAccess.ReadRevisionAsync(new(request.SourcePath, request.CancellationToken)).ConfigureAwait(false);
        return await IdentifyAsync(revision, request.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>Copies the approved file-local streams only and never treats an FFmpeg error or successful diagnostic as an overridable MKV warning.</summary>
    public async Task<ToolOutcome> RemuxAsync(RemuxRequest request)
    {
        ValidateBackend(request.Plan.Inspection);
        await ValidateToolsAsync(request.CancellationToken).ConfigureAwait(false);
        var invalidCandidate = !Path.IsPathFullyQualified(request.CandidatePath) || File.Exists(request.CandidatePath)
            || Directory.Exists(request.CandidatePath) || Path.GetFullPath(request.CandidatePath) == request.Plan.Inspection.Source.FullPath;

        if (invalidCandidate)
        {
            throw new OperationFailureException(new(ErrorCode.InvalidDestination, "A new owned MP4 candidate distinct from the source is required."));
        }

        var result = await RunAsync(new(options.Tools.Ffmpeg, FfmpegArguments.BuildRemux(request), MaximumDiagnosticsCharacters,
            null, line => ReportProgress(line, request), request.CancellationToken, ProcessStarted: options.RemuxProcessStarted)).ConfigureAwait(false);
        var disposition = FfmpegDiagnostics.Classify(result, request.Plan.Inspection);

        if (disposition != ToolDisposition.Success)
        {
            return new(result.ExitCode, Diagnostics(result), disposition);
        }

        var candidateRevision = await options.SourceAccess.ReadRevisionAsync(new(request.CandidatePath, request.CancellationToken)).ConfigureAwait(false);
        var candidate = await IdentifyAsync(candidateRevision, request.CancellationToken).ConfigureAwait(false);
        await Mp4CandidateFlags.RestoreAsync(new(request.Plan.Inspection.Source, request.Plan.RetainedTracks,
            candidate, request.CancellationToken)).ConfigureAwait(false);
        return new(result.ExitCode, Diagnostics(result), ToolDisposition.Success);
    }

    /// <summary>Verifies supported metadata and exact rational packet PTS/DTS/duration, payload hashes, count and skip/padding for every retained stream.</summary>
    public async Task<VerificationOutcome> VerifyAsync(VerifyRequest request)
    {
        ValidateBackend(request.Plan.Inspection);
        await ValidateToolsAsync(request.CancellationToken).ConfigureAwait(false);
        var candidateRevision = await options.SourceAccess.ReadRevisionAsync(new(request.CandidatePath, request.CancellationToken)).ConfigureAwait(false);
        var candidate = await IdentifyAsync(candidateRevision, request.CancellationToken).ConfigureAwait(false);
        var trackFailure = Mp4Probe.CompareTracks(new(request.Plan.RetainedTracks, candidate.Tracks));

        if (trackFailure is not null)
        {
            return new(false, trackFailure);
        }

        var expectedProperties = request.Plan.Inspection.ContainerProperties ?? ImmutableDictionary<string, string>.Empty;
        var actualProperties = candidate.ContainerProperties ?? ImmutableDictionary<string, string>.Empty;

        if (expectedProperties.Count != actualProperties.Count || expectedProperties.Where(property => property.Key != "duration").Any(property =>
            !actualProperties.TryGetValue(property.Key, out var value) || property.Value != value))
        {
            return new(false, "The supported MP4 container metadata or ordinary chapter presentation boundaries changed.");
        }

        var expectedPackets = await ReadPacketEvidenceAsync(request.Plan.Inspection.Source.FullPath,
            request.Plan.RetainedTracks, request.CancellationToken).ConfigureAwait(false);
        var actualPackets = await ReadPacketEvidenceAsync(candidate.Source.FullPath, candidate.Tracks,
            request.CancellationToken).ConfigureAwait(false);

        for (var index = 0; index < candidate.Tracks.Length; index++)
        {
            var sourceTrack = request.Plan.RetainedTracks[index];
            var outputTrack = candidate.Tracks[index];

            if (expectedPackets[sourceTrack.Id] != actualPackets[outputTrack.Id])
            {
                return new(false, $"Retained MP4 input stream {sourceTrack.Id} changed packet payloads, order, exact rational presentation/decode timing, count, or skip/padding information.");
            }
        }

        return new(true, "MP4 verification passed: explicit retained mapping/order, codec initialization, effective dispositions, supported tags/chapters/artwork, streamed encoded packet SHA256/count and rational PTS/DTS/duration including AAC skip/padding. Container track IDs, brands, layout, bitrate statistics and writer metadata may change.");
    }

    /// <summary>Requires the exact pinned native pair and rejects nonredistributable builds.</summary>
    public async Task ValidateToolsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var tool in new[] { new ToolVersion(options.Tools.Ffmpeg, "ffmpeg"), new ToolVersion(options.Tools.Ffprobe, "ffprobe") })
        {
            if (!Path.IsPathFullyQualified(tool.Path) || !File.Exists(tool.Path))
            {
                throw new OperationFailureException(new(ErrorCode.ToolUnavailable, $"Required bundled executable is missing: {tool.Path}."));
            }

            var result = await RunAsync(new(tool.Path, ["-version"], MaximumDiagnosticsCharacters, InspectionTimeout,
                null, cancellationToken)).ConfigureAwait(false);
            var expected = $"{tool.Name} version {FfmpegToolPaths.SupportedVersion} ";
            var unsupported = result.ExitCode != 0 || result.OutputTruncated || !result.StandardOutput.StartsWith(expected, StringComparison.Ordinal)
                || result.StandardOutput.Contains("--enable-nonfree", StringComparison.Ordinal);

            if (unsupported)
            {
                throw new OperationFailureException(new(ErrorCode.ToolVersionMismatch, $"Expected {tool.Name} {FfmpegToolPaths.SupportedVersion}; received {Diagnostics(result)}."));
            }
        }
    }

    /// <summary>Checks the bounded MP4 layout before starting the probe, with all external-reference loading disabled.</summary>
    private async Task<MediaInspection> IdentifyAsync(SourceRevision source, CancellationToken cancellationToken)
    {
        var structure = await Mp4BoxReader.ReadAsync(new SourceRequest(source.FullPath, cancellationToken)).ConfigureAwait(false);
        var result = await RunAsync(new(options.Tools.Ffprobe, FfmpegArguments.BuildProbe(source.FullPath),
            Mp4Probe.MaximumJsonCharacters, InspectionTimeout, null, cancellationToken)).ConfigureAwait(false);

        if (result.ExitCode != 0 || result.OutputTruncated || !string.IsNullOrWhiteSpace(result.StandardError))
        {
            throw new OperationFailureException(new(ErrorCode.InvalidToolOutput, "MP4 probe failed or exceeded its bound. " + Diagnostics(result)));
        }

        return Mp4Probe.Parse(new(result.StandardOutput, source, structure));
    }

    /// <summary>Streams complete fixed-size packet facts through the existing owned worker rather than loading packet JSON or scratch payloads.</summary>
    private async Task<ImmutableDictionary<int, Mp4PacketFingerprint>> ReadPacketEvidenceAsync(string path,
        IEnumerable<MediaTrack> tracks, CancellationToken cancellationToken)
    {
        using var evidence = new Mp4PacketEvidence(tracks);
        var result = await RunAsync(new(options.Tools.Ffprobe, FfmpegArguments.BuildPackets(path), MaximumDiagnosticsCharacters,
            null, evidence.AcceptLine, cancellationToken)).ConfigureAwait(false);

        // Retained stdout is deliberately bounded; every complete fixed-field line is still consumed by the callback.
        if (result.ExitCode != 0 || result.StandardErrorTruncated || result.ProtocolLineTruncated || !string.IsNullOrWhiteSpace(result.StandardError))
        {
            throw new OperationFailureException(new(ErrorCode.VerificationFailed, "MP4 streamed packet inspection failed. " + result.StandardError));
        }

        return evidence.Finish();
    }

    /// <summary>Uses the pinned source duration only as a progress denominator; publication does not require container-duration equality.</summary>
    private static void ReportProgress(string line, RemuxRequest request)
    {
        const string prefix = "out_time_us=";

        if (line == "progress=end")
        {
            request.Progress?.Report(100);
            return;
        }

        if (!line.StartsWith(prefix, StringComparison.Ordinal)
            || !double.TryParse(line.AsSpan(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var time))
        {
            return;
        }

        var durationText = request.Plan.Inspection.ContainerProperties?.GetValueOrDefault("duration");

        if (double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) && duration > 0)
        {
            request.Progress?.Report(Math.Clamp(time / (duration * 1_000_000) * 100, 0, 99));
        }
    }

    /// <summary>Preserves process-tree uncertainty as a stable lifecycle failure and never cleans candidate files itself.</summary>
    private async Task<ProcessResult> RunAsync(ProcessRequest request)
    {
        try
        {
            var environment = new Dictionary<string, string> { ["LC_ALL"] = "C.UTF-8", ["LANG"] = "C.UTF-8" };
            return await processRunner.RunAsync(request with { Environment = environment }).ConfigureAwait(false);
        }
        catch (ProcessTerminationException exception)
        {
            throw new OperationFailureException(new(ErrorCode.ProcessTerminationFailed, exception.Message));
        }
        catch (TimeoutException exception)
        {
            throw new OperationFailureException(new(ErrorCode.ToolFailed, exception.Message));
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new OperationFailureException(new(ErrorCode.ToolUnavailable, exception.Message));
        }
    }

    /// <summary>Rejects a frozen plan from another format/version instead of silently choosing a different implementation.</summary>
    private static void ValidateBackend(MediaInspection inspection)
    {
        if (inspection.Container != MediaContainer.Mp4 || inspection.Backend != new BackendIdentity("FFmpeg", FfmpegToolPaths.SupportedVersion, Mp4Probe.IdentificationFormat))
        {
            throw new OperationFailureException(new(ErrorCode.StalePlan, "The approved MP4 backend identity does not match this pinned implementation."));
        }
    }

    /// <summary>Retains bounded original diagnostics and exposes truncation explicitly.</summary>
    private static string Diagnostics(ProcessResult result) =>
        (result.StandardError + (result.StandardErrorTruncated ? "\n[Diagnostics truncated; both streams fully drained.]" : string.Empty)
            + (result.ProtocolLineTruncated ? "\n[The machine protocol line bound was exceeded.]" : string.Empty)).Trim();

    private sealed record ToolVersion(string Path, string Name);
}
