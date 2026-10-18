using System.Globalization;
using System.Xml.Linq;
using TrackTrim.Application;
using TrackTrim.Application.Ports;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Media;
using TrackTrim.Infrastructure.Processes;

namespace TrackTrim.Infrastructure.MediaTools;

public sealed record MkvBackendOptions(ISourceAccess SourceAccess, MkvToolPaths Tools,
    string? ScratchDirectory = null, Action<string>? Diagnostic = null)
{
    internal Action<int>? RemuxProcessStarted { get; init; }
}

/// <summary>Implements the pinned Matroska workflow with real MKVToolNix workers and structural verification.</summary>
public sealed class MkvBackend(MkvBackendOptions options) : IMediaBackend
{
    private const int MaximumDiagnosticsCharacters = 32768;
    private static readonly TimeSpan InspectionTimeout = TimeSpan.FromMinutes(2);
    private static readonly HashSet<string> PermittedChangedTrackProperties = new(StringComparer.Ordinal)
    {
        "uid", "number", "num_index_entries", "minimum_timestamp", "language_ietf",
        "default_track", "forced_track", "enabled_track", "language", "track_name", "_source_writing_application",
    };
    private readonly OwnedProcessRunner processRunner = new();

    /// <summary>Inspects the detected container, required properties, and supported chapter/tag structures.</summary>
    public async Task<MediaInspection> InspectAsync(InspectRequest request)
    {
        await ValidateToolsAsync(request.CancellationToken).ConfigureAwait(false);
        var revision = await options.SourceAccess.ReadRevisionAsync(new SourceRequest(
            request.SourcePath, request.CancellationToken)).ConfigureAwait(false);
        var inspection = await IdentifyAsync(new IdentifyRequest(revision, request.CancellationToken)).ConfigureAwait(false);
        var scratch = CreateScratchDirectory();
        var terminationConfirmed = true;

        try
        {
            await ExtractMetadataAsync(new MetadataExtraction(inspection, scratch, request.CancellationToken)).ConfigureAwait(false);
            return inspection;
        }
        catch (OperationFailureException exception) when (exception.Failure.Code == ErrorCode.ProcessTerminationFailed)
        {
            terminationConfirmed = false;
            throw;
        }
        finally
        {

            if (terminationConfirmed)
            {
                DeleteScratchDirectory(scratch);
            }
        }
    }

    /// <summary>Runs only the approved retention command and reports documented machine progress.</summary>
    public async Task<ToolOutcome> RemuxAsync(RemuxRequest request)
    {
        ValidatePlanBackend(request.Plan.Inspection.Backend);
        await ValidateToolsAsync(request.CancellationToken).ConfigureAwait(false);
        var result = await RunAsync(new ProcessRequest(options.Tools.MkvMerge, MkvArguments.BuildRemux(request),
            MaximumDiagnosticsCharacters, null,
            line => ReportProgress(new ProgressLine(line, request.Progress)), request.CancellationToken,
            ProcessStarted: options.RemuxProcessStarted)).ConfigureAwait(false);
        return new ToolOutcome(result.ExitCode, CombineDiagnostics(result));
    }

    /// <summary>Verifies the approved stream mapping, exact supported metadata, and per-track frame timestamps.</summary>
    public async Task<VerificationOutcome> VerifyAsync(VerifyRequest request)
    {
        ValidatePlanBackend(request.Plan.Inspection.Backend);
        await ValidateToolsAsync(request.CancellationToken).ConfigureAwait(false);
        var sourceInspection = request.Plan.Inspection;
        var candidateRevision = await options.SourceAccess.ReadRevisionAsync(new SourceRequest(
            request.CandidatePath, request.CancellationToken)).ConfigureAwait(false);
        var candidate = await IdentifyAsync(new IdentifyRequest(candidateRevision, request.CancellationToken)).ConfigureAwait(false);
        var trackFailure = CompareTracks(new TrackComparison(request.Plan.RetainedTracks, candidate.Tracks));

        if (trackFailure is not null)
        {
            return new VerificationOutcome(false, trackFailure);
        }

        if (sourceInspection.Title != candidate.Title || sourceInspection.HasChapters != candidate.HasChapters)
        {
            return new VerificationOutcome(false, "The container title or chapter presence changed.");
        }

        if (!AttachmentsMatch(new AttachmentComparison(sourceInspection.Attachments, candidate.Attachments)))
        {
            return new VerificationOutcome(false, "Attachment metadata changed.");
        }

        var sourceScratch = CreateScratchDirectory();
        var outputScratch = CreateScratchDirectory();
        var terminationConfirmed = true;

        try
        {
            var sourceMetadata = await ExtractMetadataAsync(new MetadataExtraction(
                sourceInspection, sourceScratch, request.CancellationToken)).ConfigureAwait(false);
            var outputMetadata = await ExtractMetadataAsync(new MetadataExtraction(
                candidate, outputScratch, request.CancellationToken)).ConfigureAwait(false);

            if (sourceMetadata.Chapters is not null && outputMetadata.Chapters is not null
                && !MkvMetadataXml.Equivalent(new MkvMetadataXml.XmlComparison(sourceMetadata.Chapters, outputMetadata.Chapters)))
            {
                return new VerificationOutcome(false, "Chapter semantic content changed.");
            }

            var uidMapping = request.Plan.RetainedTracks.Select((track, index) =>
                KeyValuePair.Create(track.Uid, candidate.Tracks[index].Uid)).ToDictionary();
            var expectedTags = sourceMetadata.Tags is null ? new XDocument(new XElement("Tags"))
                : MkvMetadataXml.ExpectedTags(new MkvMetadataXml.TagExpectation(sourceMetadata.Tags, uidMapping));
            var actualTags = outputMetadata.Tags ?? new XDocument(new XElement("Tags"));

            if (!MkvMetadataXml.Equivalent(new MkvMetadataXml.XmlComparison(expectedTags, actualTags)))
            {
                return new VerificationOutcome(false, "Global or retained-track tag semantic content changed.");
            }

            await ExtractTimestampsAsync(new TimestampExtraction(sourceInspection.Source.FullPath,
                request.Plan.RetainedTracks, sourceScratch, request.CancellationToken)).ConfigureAwait(false);
            await ExtractTimestampsAsync(new TimestampExtraction(candidate.Source.FullPath,
                candidate.Tracks, outputScratch, request.CancellationToken)).ConfigureAwait(false);

            for (var trackIndex = 0; trackIndex < candidate.Tracks.Length; trackIndex++)
            {
                var sourceTrack = request.Plan.RetainedTracks[trackIndex];
                var outputTrack = candidate.Tracks[trackIndex];
                var timestampsMatch = await MkvTimestampComparison.EquivalentAsync(new TrackTimestampComparison(
                    TimestampPath(new TrackPath(sourceScratch, sourceTrack.Id)),
                    TimestampPath(new TrackPath(outputScratch, outputTrack.Id)), sourceTrack, outputTrack,
                    request.CancellationToken)).ConfigureAwait(false);

                if (!timestampsMatch)
                {
                    return new VerificationOutcome(false, $"Retained track {sourceTrack.Id} frame timestamps or frame count changed.");
                }
            }

            return new VerificationOutcome(true,
                "Structural verification passed: approved track order/mapping, codec properties, effective flags, title, semantic chapters/tags, attachment metadata, and frame timestamp count/values (1 ms tolerance). Encoded payloads and attachment bytes are fixture-tested, not hashed per job.");
        }
        catch (OperationFailureException exception) when (exception.Failure.Code == ErrorCode.ProcessTerminationFailed)
        {
            terminationConfirmed = false;
            throw;
        }
        finally
        {

            if (terminationConfirmed)
            {
                DeleteScratchDirectory(sourceScratch);
                DeleteScratchDirectory(outputScratch);
            }
        }
    }

    /// <summary>Checks both executables against the same pinned distribution before using a plan.</summary>
    public async Task ValidateToolsAsync(CancellationToken cancellationToken = default)
    {
        var tools = new[]
        {
            new ToolVersionRequest(options.Tools.MkvMerge, "mkvmerge"),
            new ToolVersionRequest(options.Tools.MkvExtract, "mkvextract"),
        };

        foreach (var tool in tools)
        {

            if (!Path.IsPathFullyQualified(tool.Path) || !File.Exists(tool.Path))
            {
                throw Failure(new BackendFailure(ErrorCode.ToolUnavailable, $"Required bundled executable is missing: {tool.Path}"));
            }

            var result = await RunAsync(new ProcessRequest(tool.Path, ["--version"],
                MaximumDiagnosticsCharacters, InspectionTimeout, null, cancellationToken)).ConfigureAwait(false);
            var expectedPrefix = $"{tool.Name} v{MkvToolPaths.SupportedVersion} ";

            if (result.ExitCode != 0 || result.OutputTruncated || !result.StandardOutput.StartsWith(expectedPrefix, StringComparison.Ordinal))
            {
                throw Failure(new BackendFailure(ErrorCode.ToolVersionMismatch,
                    $"Expected {tool.Name} {MkvToolPaths.SupportedVersion} at {tool.Path}; received {result.StandardOutput.Trim()}."));
            }
        }
    }

    /// <summary>Rejects incomplete identification and snapshots the real probe properties.</summary>
    private async Task<MediaInspection> IdentifyAsync(IdentifyRequest request)
    {
        // JSON field names and values are invariant without selecting a platform-specific tool UI locale.
        var result = await RunAsync(new ProcessRequest(options.Tools.MkvMerge,
            ["--output-charset", "UTF-8", "-J", request.Source.FullPath],
            MkvIdentification.MaximumJsonCharacters, InspectionTimeout, null, request.CancellationToken)).ConfigureAwait(false);

        if (result.ExitCode != 0 || result.OutputTruncated)
        {
            throw Failure(new BackendFailure(ErrorCode.InvalidToolOutput,
                $"Identification failed or exceeded its output bound. {CombineDiagnostics(result)}"));
        }

        var inspection = MkvIdentification.Parse(new MkvIdentification.IdentificationRequest(result.StandardOutput, request.Source));
        await MkvSegmentInfoReader.ValidateAsync(new(request.Source.FullPath, request.CancellationToken)).ConfigureAwait(false);
        return inspection;
    }

    /// <summary>Extracts complete XML into application-owned names and validates unsupported structures.</summary>
    private async Task<ExtractedMetadata> ExtractMetadataAsync(MetadataExtraction request)
    {
        var arguments = new List<string> { request.Inspection.Source.FullPath };
        var chaptersPath = Path.Combine(request.Directory, "chapters.xml");
        var tagsPath = Path.Combine(request.Directory, "tags.xml");

        if (request.Inspection.HasChapters)
        {
            arguments.AddRange(["chapters", chaptersPath]);
        }

        if (request.Inspection.HasTags)
        {
            arguments.AddRange(["tags", tagsPath]);
        }

        if (arguments.Count == 1)
        {
            return new ExtractedMetadata(null, null);
        }

        var result = await RunAsync(new ProcessRequest(options.Tools.MkvExtract, arguments,
            MaximumDiagnosticsCharacters, InspectionTimeout, null, request.CancellationToken)).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw Failure(new BackendFailure(ErrorCode.InvalidToolOutput, $"Required metadata extraction failed: {CombineDiagnostics(result)}"));
        }

        var chapters = request.Inspection.HasChapters ? MkvMetadataXml.Read(chaptersPath) : null;
        var tags = request.Inspection.HasTags ? MkvMetadataXml.Read(tagsPath) : null;

        if (chapters is not null)
        {
            MkvMetadataXml.ValidateChapters(chapters);
        }

        if (tags is not null)
        {
            MkvMetadataXml.ValidateTags(tags);
            MkvMetadataXml.ValidateTrackTargets(new MkvMetadataXml.TrackTargetValidation(tags,
                request.Inspection.Tracks.Select(track => track.Uid).ToHashSet()));
        }

        return new ExtractedMetadata(chapters, tags);
    }

    /// <summary>Extracts frame timestamps using local backend IDs and application-owned filenames.</summary>
    private async Task ExtractTimestampsAsync(TimestampExtraction request)
    {
        var arguments = new List<string> { request.SourcePath, "timestamps_v2" };
        arguments.AddRange(request.Tracks.Select(track =>
            $"{track.Id.ToString(CultureInfo.InvariantCulture)}:{TimestampPath(new TrackPath(request.Directory, track.Id))}"));
        var result = await RunAsync(new ProcessRequest(options.Tools.MkvExtract, arguments,
            MaximumDiagnosticsCharacters, null, null, request.CancellationToken)).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw Failure(new BackendFailure(ErrorCode.VerificationFailed, $"Required timestamp extraction failed: {CombineDiagnostics(result)}"));
        }
    }

    /// <summary>Checks retained mapping by explicit order, UID map, all known properties, and effective flags.</summary>
    private static string? CompareTracks(TrackComparison request)
    {

        if (request.Expected.Count != request.Actual.Count)
        {
            return "The retained track count does not match the approved plan.";
        }

        for (var trackIndex = 0; trackIndex < request.Expected.Count; trackIndex++)
        {
            var expected = request.Expected[trackIndex];
            var actual = request.Actual[trackIndex];
            var permitsUidRemapping = expected.Properties.TryGetValue("_source_writing_application", out var writer)
                && writer.StartsWith("MakeMKV", StringComparison.OrdinalIgnoreCase);

            if (expected.Uid != actual.Uid && !permitsUidRemapping)
            {
                return $"Retained input track {expected.Id} lost its UID identity mapping.";
            }

            var commonMetadataMatches = expected.Kind == actual.Kind && expected.Codec == actual.Codec
                && LanguageAliasPolicy.Equivalent(new LanguageComparison(expected.Language, actual.Language)) && expected.Title == actual.Title
                && expected.Channels == actual.Channels && expected.IsDefault == actual.IsDefault
                && expected.IsForced == actual.IsForced && expected.IsEnabled == actual.IsEnabled;

            if (!commonMetadataMatches)
            {
                return $"The retained track mapping or effective metadata changed at output position {trackIndex}.";
            }

            var expectedProperties = expected.Properties.Where(property => !PermittedChangedTrackProperties.Contains(property.Key)).ToDictionary();
            var actualProperties = actual.Properties.Where(property => !PermittedChangedTrackProperties.Contains(property.Key)).ToDictionary();

            if (AacDefaultDurationPolicy.PermitsAddition(new TrackSemanticComparison(expected, actual)))
            {
                actualProperties.Remove(AacDefaultDurationPolicy.PropertyName);
            }

            var propertiesMatch = expectedProperties.Count == actualProperties.Count
                && expectedProperties.All(property => actualProperties.TryGetValue(property.Key, out var value) && property.Value == value);

            if (!propertiesMatch || !LanguageIetfMatches(new TrackLanguageComparison(expected, actual)))
            {
                return $"Known codec, timing-header, disposition, or metadata properties changed for retained input track {expected.Id}.";
            }
        }

        return null;
    }

    /// <summary>Allows an IETF language addition only when it is equivalent to the original legacy field.</summary>
    private static bool LanguageIetfMatches(TrackLanguageComparison request)
    {
        return LanguageAliasPolicy.MatchesIetf(new IetfLanguageComparison(request.Expected.Language,
            request.Expected.LanguageIetf, request.Actual.LanguageIetf));
    }

    /// <summary>Checks attachment order, UIDs, names, types, and sizes; fixture tests check their payloads.</summary>
    private static bool AttachmentsMatch(AttachmentComparison request)
    {
        return request.Expected.Count == request.Actual.Count && request.Expected.Zip(request.Actual).All(pair =>
            pair.First.Uid == pair.Second.Uid && pair.First.Name == pair.Second.Name
            && pair.First.ContentType == pair.Second.ContentType && pair.First.Size == pair.Second.Size
            && pair.First.Description == pair.Second.Description);
    }

    /// <summary>Translates process ownership failures into stable application outcomes.</summary>
    private async Task<ProcessResult> RunAsync(ProcessRequest request)
    {
        try
        {
            var environment = new Dictionary<string, string>(request.Environment ?? new Dictionary<string, string>());

            if (options.Tools.NativeLibraryDirectory is { } nativeLibraryDirectory)
            {
                environment["LD_LIBRARY_PATH"] = nativeLibraryDirectory;
            }

            if (OperatingSystem.IsLinux())
            {
                // MKVToolNix derives Unix argv encoding from LC_ALL/LANG; C.UTF-8 preserves Unicode paths on a minimal C-locale host.
                environment["LC_ALL"] = "C.UTF-8";
                environment["LANG"] = "C.UTF-8";
            }

            return await processRunner.RunAsync(request with { Environment = environment }).ConfigureAwait(false);
        }
        catch (ProcessTerminationException exception)
        {
            throw Failure(new BackendFailure(ErrorCode.ProcessTerminationFailed, exception.Message));
        }
        catch (TimeoutException exception)
        {
            throw Failure(new BackendFailure(ErrorCode.ToolFailed, exception.Message));
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw Failure(new BackendFailure(ErrorCode.ToolUnavailable, exception.Message));
        }
    }

    /// <summary>Parses the documented invariant GUI progress protocol, independently of translated UI labels.</summary>
    private static void ReportProgress(ProgressLine request)
    {
        const string prefix = "#GUI#progress ";

        if (!request.Line.StartsWith(prefix, StringComparison.Ordinal))
        {
            return;
        }

        var value = request.Line.AsSpan(prefix.Length).TrimEnd('%');

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percentage))
        {
            request.Progress?.Report(Math.Clamp(percentage, 0, 100));
        }
    }

    /// <summary>Allocates a uniquely owned metadata directory in portable application data.</summary>
    private string CreateScratchDirectory()
    {
        var root = options.ScratchDirectory ?? Path.Combine(AppContext.BaseDirectory, "data", "inspection");
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>Removes only a directory created by this adapter after all workers have exited.</summary>
    private void DeleteScratchDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            options.Diagnostic?.Invoke($"Inspection metadata cleanup failed for {directory}: {exception.Message}");
        }
    }

    /// <summary>Rejects a plan approved against a different backend version or identification schema.</summary>
    private static void ValidatePlanBackend(BackendIdentity backend)
    {
        var expected = new BackendIdentity("MKVToolNix", MkvToolPaths.SupportedVersion,
            MkvIdentification.SupportedIdentificationFormat);

        if (backend != expected)
        {
            throw Failure(new BackendFailure(ErrorCode.StalePlan, "The approved backend identity no longer matches the pinned implementation."));
        }
    }

    /// <summary>Uses local track IDs only inside application-owned timestamp filenames.</summary>
    private static string TimestampPath(TrackPath request)
    {
        return Path.Combine(request.Directory, $"track-{request.TrackId.ToString(CultureInfo.InvariantCulture)}.timestamps.txt");
    }

    /// <summary>Combines bounded raw diagnostics without treating translated text as a machine API.</summary>
    private static string CombineDiagnostics(ProcessResult result)
    {
        var suffix = result.OutputTruncated ? "\n[Diagnostics truncated; both streams were fully drained.]" : string.Empty;
        return $"{result.StandardOutput}\n{result.StandardError}{suffix}".Trim();
    }

    /// <summary>Creates a stable expected backend failure.</summary>
    private static OperationFailureException Failure(BackendFailure failure)
    {
        return new OperationFailureException(new Failure(failure.Code, failure.Detail));
    }

    private sealed record IdentifyRequest(SourceRevision Source, CancellationToken CancellationToken);
    private sealed record MetadataExtraction(MediaInspection Inspection, string Directory, CancellationToken CancellationToken);
    private sealed record ExtractedMetadata(XDocument? Chapters, XDocument? Tags);
    private sealed record TimestampExtraction(string SourcePath, IReadOnlyList<MediaTrack> Tracks, string Directory, CancellationToken CancellationToken);
    private sealed record TrackComparison(IReadOnlyList<MediaTrack> Expected, IReadOnlyList<MediaTrack> Actual);
    private sealed record AttachmentComparison(IReadOnlyList<Attachment> Expected, IReadOnlyList<Attachment> Actual);
    private sealed record TrackLanguageComparison(MediaTrack Expected, MediaTrack Actual);
    private sealed record TrackPath(string Directory, int TrackId);
    private sealed record ToolVersionRequest(string Path, string Name);
    private sealed record ProgressLine(string Line, IProgress<double>? Progress);
    private sealed record BackendFailure(ErrorCode Code, string Detail);
}
