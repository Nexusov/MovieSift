using MovieSift.Application;
using MovieSift.Application.Ports;
using MovieSift.Core.Failures;
using MovieSift.Core.Media;
using MovieSift.Infrastructure.Persistence;

namespace MovieSift.Infrastructure.Files;

/// <summary>Keeps the original in owned backup storage until replacement and its durable outcome are established.</summary>
public sealed partial class OwnedOutputStore
{
    /// <summary>Uses a same-filesystem replacement with a unique backup, never delete-then-move or direct media writes.</summary>
    private async Task<PublishedOutput> ReplaceSourceAsync(OutputRequest request)
    {
        var record = await ReadOwnedRecordAsync(request);
        var output = record.Output;

        if (record.Stage != PublishingStage || record.VerifiedCandidate != request.VerifiedCandidate)
        {
            throw InvalidOwnership("Replacement requires the persisted verified candidate revision.");
        }

        var attempted = false;

        try
        {
            replacementBoundary?.Invoke(ReplacementBoundary.BeforeReplace, output);
            FileSourceAccess.ValidateReplaceableSource(output.ExpectedSource!);
            await ValidateReplacementCandidateAsync(output, request.VerifiedCandidate);
            var backup = BackupPath(output);

            if (BackupExists(output))
            {
                throw InvalidOwnership("The replacement backup path already exists; it was not adopted.");
            }

            ReplacementFilePermissions.Prepare(output.ExpectedSource!, request.VerifiedCandidate!);
            attempted = true;
            File.Replace(output.CandidatePath, output.DestinationPath, backup, ignoreMetadataErrors: false);
            replacementBoundary?.Invoke(ReplacementBoundary.Replaced, output);

            if (!await HasCommittedReplacementAsync(record))
            {
                throw new OperationFailureException(new(ErrorCode.PublicationUncertain,
                    "Replacement could not be confirmed. The original backup and ownership record were preserved."));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationFailureException)
        {
            // Some platform errors can follow a successful rename. Preserve the actual publication fact when both identities prove it.
            if (attempted && await HasCommittedReplacementAsync(record))
            {
                return await CompleteReplacementAsync(record);
            }

            if (!attempted || await IsUnchangedReplacementAsync(record))
            {
                await WriteRecordAsync(new(output, ReservedStage));
                var failure = exception is OperationFailureException operation
                    ? operation.Failure : new Failure(ErrorCode.PublicationFailed, exception.Message);
                throw new OperationFailureException(failure, innerException: exception);
            }

            throw new OperationFailureException(new(ErrorCode.PublicationUncertain,
                "The replacement outcome is uncertain. No backup or candidate was removed. " + exception.Message), innerException: exception);
        }

        return await CompleteReplacementAsync(record);
    }

    /// <summary>Records known publication before allowing cleanup; a journal failure must not erase that publication fact.</summary>
    private async Task<PublishedOutput> CompleteReplacementAsync(ValidatedOwnershipRecord record)
    {
        var output = record.Output;

        try
        {
            replacementBoundary?.Invoke(ReplacementBoundary.BeforePublishedRecord, output);
            await WriteRecordAsync(new(output, PublishedStage, record.VerifiedCandidate));
            return new(output.DestinationPath, record.VerifiedCandidate!.Length);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationFailureException)
        {
            return new(output.DestinationPath, record.VerifiedCandidate!.Length,
                [new(output.DirectoryPath, "The replacement completed, but its recovery record could not be finalized. The original backup was retained. " + exception.Message)]);
        }
    }

    /// <summary>Requires the candidate at its final name and the inspected original at the unique backup name.</summary>
    private static async Task<bool> HasCommittedReplacementAsync(ValidatedOwnershipRecord record) =>
        !File.Exists(record.Output.CandidatePath) &&
        await MatchesRevisionAsync(record.Output.DestinationPath, record.VerifiedCandidate) &&
        await MatchesRevisionAsync(BackupPath(record.Output), record.Output.ExpectedSource);

    /// <summary>Allows ordinary failed-operation cleanup only when the platform left both original and candidate unchanged.</summary>
    private static async Task<bool> IsUnchangedReplacementAsync(ValidatedOwnershipRecord record) =>
        !BackupExists(record.Output) &&
        await MatchesRevisionAsync(record.Output.DestinationPath, record.Output.ExpectedSource) &&
        await MatchesRevisionAsync(record.Output.CandidatePath, record.VerifiedCandidate);

    /// <summary>Checks real regular-file identity, size and modification time while allowing the intended rename.</summary>
    private static async Task<bool> MatchesRevisionAsync(string path, SourceRevision? expected)
    {
        if (expected is null)
        {
            return false;
        }

        try
        {
            var current = await new FileSourceAccess().ReadRevisionAsync(new(path));
            return current.FileIdentity == expected.FileIdentity && current.Length == expected.Length &&
                current.LastWriteUtcTicks == expected.LastWriteUtcTicks;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationFailureException)
        {
            return false;
        }
    }

    /// <summary>Never removes a backup from an uncertain swap, or after an external change to the published destination.</summary>
    private static async Task PrepareReplacementCleanupAsync(ValidatedOwnershipRecord record)
    {
        var backup = BackupPath(record.Output);

        if (record.Stage == PublishingStage)
        {
            throw new IOException("The replacement was interrupted before its durable completion record. Preserve the backup and inspect the files manually.");
        }

        if (record.Stage == ReservedStage)
        {
            if (BackupExists(record.Output))
            {
                throw new IOException("An unexpected backup prevented cleanup of an uncommitted operation.");
            }

            return;
        }

        if (Directory.EnumerateFileSystemEntries(record.Output.DirectoryPath).Contains(record.Output.CandidatePath, StringComparer.Ordinal))
        {
            throw new IOException("A candidate path reappeared after publication; the backup and unexpected file were preserved.");
        }

        if (!await MatchesRevisionAsync(record.Output.DestinationPath, record.VerifiedCandidate))
        {
            throw new IOException("The published destination changed; the original backup was preserved.");
        }

        if (BackupExists(record.Output))
        {
            if (!await MatchesRevisionAsync(backup, record.Output.ExpectedSource))
            {
                throw new IOException("The backup no longer identifies the inspected original; it was preserved.");
            }

            File.Delete(backup);
        }
    }

    /// <summary>Requires the exact candidate revision captured before full media verification.</summary>
    private static async Task ValidateReplacementCandidateAsync(OwnedOutput output, SourceRevision? candidate)
    {
        if (!IsValidCandidateRevision(output, candidate) || !await MatchesRevisionAsync(output.CandidatePath, candidate))
        {
            throw new OperationFailureException(new(ErrorCode.VerificationFailed, "The replacement candidate no longer matches its verified revision."));
        }

        FileSourceAccess.ValidateReplaceableSource(candidate!);
    }

    /// <summary>Accepts only bounded revision evidence for the fixed candidate path.</summary>
    private static bool IsValidCandidateRevision(OwnedOutput output, SourceRevision? candidate) =>
        candidate is not null && candidate.FullPath == output.CandidatePath && IsValidRevision(candidate) &&
        candidate.FileIdentity != output.ExpectedSource?.FileIdentity;

    /// <summary>Rejects incomplete deserialized revision evidence before using it to authorize cleanup.</summary>
    private static bool IsValidRevision(SourceRevision revision) =>
        !string.IsNullOrWhiteSpace(revision.FileIdentity) && revision.FileIdentity.Length <= 128 &&
        revision.Length >= 0 && revision.LastWriteUtcTicks > 0 && revision.LastWriteUtcTicks <= DateTime.MaxValue.Ticks;

    /// <summary>Derives the one permitted backup path without accepting arbitrary paths from an ownership record.</summary>
    private static string BackupPath(OwnedOutput output) =>
        Path.Combine(output.DirectoryPath, "original" + MediaContainerPolicy.Extension(output.Container));

    /// <summary>Includes dangling symlinks, which File.Exists must not mistake for a free backup name.</summary>
    private static bool BackupExists(OwnedOutput output)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var backup = BackupPath(output);
        return Directory.EnumerateFileSystemEntries(output.DirectoryPath).Any(path => string.Equals(path, backup, comparison));
    }
}
