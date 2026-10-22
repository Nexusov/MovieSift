using TrackTrim.Application.Batches;
using TrackTrim.Core.Planning;

namespace TrackTrim.Application.QueueLists;

/// <summary>Validates only listing data; restored media still requires fresh inspection and explicit approval.</summary>
public static class QueueListPolicy
{
    public const int MaximumEntries = BatchDiscoveryLimits.MaximumFiles;
    public const int MaximumPathCharacters = 4096;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Rejects the complete list when any required path, provenance or bound is invalid.</summary>
    public static QueueListFailure? Validate(QueueList? list)
    {
        if (list is null || list.Entries.IsDefault)
        {
            return Invalid("The queue list and entries must be present.");
        }

        if (list.Entries.Length > MaximumEntries)
        {
            return new(QueueListErrorCode.LimitExceeded, $"The queue list exceeds {MaximumEntries} entries.");
        }

        var sources = new HashSet<string>(PathComparer);

        foreach (var entry in list.Entries)
        {
            var invalidEntry = entry is null || !IsCanonicalAbsolutePath(entry.SourcePath) ||
                (entry.DestinationPath is not null && !IsCanonicalAbsolutePath(entry.DestinationPath));

            if (invalidEntry)
            {
                return Invalid("Every queue entry requires a canonical absolute source and valid optional destination.");
            }

            if (!sources.Add(entry!.SourcePath))
            {
                return Invalid("The queue list contains duplicate source paths.");
            }

            var provenanceFailure = ValidateOrigin(entry);

            if (provenanceFailure is not null)
            {
                return provenanceFailure;
            }
        }

        return null;
    }

    /// <summary>Compares ordered listing content rather than immutable-array storage identity.</summary>
    public static bool ContentEquals(QueueList? first, QueueList? second)
    {
        if (ReferenceEquals(first, second))
        {
            return true;
        }

        var comparable = first is not null && second is not null &&
            !first.Entries.IsDefault && !second.Entries.IsDefault;

        return comparable && first!.Entries.SequenceEqual(second!.Entries);
    }

    /// <summary>Requires original root/relative provenance to identify exactly the saved source.</summary>
    private static QueueListFailure? ValidateOrigin(QueueListEntry entry)
    {
        var origin = entry.FolderOrigin;

        if (origin is null)
        {
            return entry.OutputDirectoryRoot is null ? null : Invalid("Only folder entries may retain an output root.");
        }

        var invalidOrigin = !IsCanonicalAbsolutePath(origin.RootPath) ||
            !ValidText(origin.RelativePath) || origin.RelativePath.Length > MaximumPathCharacters;

        if (invalidOrigin)
        {
            return Invalid("The folder provenance is invalid.");
        }

        var relativeValidation = OutputPathPolicy.DeriveFolderDestination(new(origin.RootPath, origin.RelativePath));

        if (relativeValidation.Failure is not null)
        {
            return Invalid("The folder-relative source must be a safe MKV or MP4 path.");
        }

        var reconstructedSource = Path.GetFullPath(Path.Combine(origin.RootPath, origin.RelativePath));

        if (!PathComparer.Equals(reconstructedSource, entry.SourcePath))
        {
            return Invalid("The folder provenance does not identify its saved source.");
        }

        if (entry.OutputDirectoryRoot is null)
        {
            return null;
        }

        var invalidOutputRoot = !IsCanonicalAbsolutePath(entry.OutputDirectoryRoot) ||
            entry.DestinationPath is null || PathComparer.Equals(entry.OutputDirectoryRoot, entry.DestinationPath) ||
            !OutputPathPolicy.IsWithinDirectory(entry.OutputDirectoryRoot, entry.DestinationPath);

        return invalidOutputRoot ? Invalid("The folder output must remain beneath its saved output root.") : null;
    }

    /// <summary>Rejects malformed Unicode and normalized aliases without requiring the source to exist.</summary>
    private static bool IsCanonicalAbsolutePath(string? path)
    {
        var invalidPath = !ValidText(path) || path!.Length > MaximumPathCharacters ||
            !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal);

        if (invalidPath)
        {
            return false;
        }

        try
        {
            return PathComparer.Equals(path, Path.GetFullPath(path!));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Does not permit embedded nulls or unpaired UTF-16 surrogates in serialized path data.</summary>
    private static bool ValidText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0'))
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsSurrogate(value[index]))
            {
                continue;
            }

            if (!char.IsSurrogatePair(value, index))
            {
                return false;
            }

            index++;
        }

        return true;
    }

    /// <summary>Uses one explicit invalid-record outcome without inventing a usable partial list.</summary>
    private static QueueListFailure Invalid(string detail) => new(QueueListErrorCode.InvalidRecord, detail);
}
