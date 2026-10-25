using MovieSift.Application;
using MovieSift.Core.Failures;

namespace MovieSift.Infrastructure.Files;

/// <summary>Restricts writes and cleanup to paths without symlink or junction redirection.</summary>
internal static class PathSafety
{
    /// <summary>Rejects every existing reparse-point component of a path.</summary>
    internal static void RejectReparsePoints(string path)
    {
        var current = Path.GetFullPath(path);

        while (!string.IsNullOrEmpty(current))
        {
            var attributes = File.GetAttributes(current);

            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new OperationFailureException(new(ErrorCode.InvalidDestination, "Symlink and junction paths are not supported.", ErrorReason.RedirectedPath));
            }

            current = Path.GetDirectoryName(current);
        }
    }
}
