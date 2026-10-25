using MovieSift.Core.Failures;
using MovieSift.Core.Media;

namespace MovieSift.Core.Planning;

/// <summary>Derives folder outputs and checks lexical containment without accessing the filesystem.</summary>
public static class OutputPathPolicy
{
    private static readonly char[] RelativeSeparators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    /// <summary>Includes equality while rejecting sibling-prefix and parent-traversal escapes.</summary>
    public static bool IsWithinDirectory(string root, string path)
    {
        try
        {
            var invalidPaths = string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path) ||
                !Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(path);

            if (invalidPaths)
            {
                return false;
            }

            var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
            return !Path.IsPathRooted(relative) && !relative.Split(RelativeSeparators).Contains("..");
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Preserves validated relative parents and the existing trimmed-output filename convention.</summary>
    public static FolderDestinationResult DeriveFolderDestination(FolderDestinationRequest request)
    {
        try
        {
            var relative = request.RelativeSourcePath;
            var invalidShape = string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative);

            if (invalidShape)
            {
                return InvalidDestination();
            }

            var components = relative.Split(RelativeSeparators);
            var invalidComponents = components.Any(component => component.Length == 0 ||
                component is "." or ".." || component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0);

            if (invalidComponents || MediaContainerPolicy.FromExtension(components[^1]) is null)
            {
                return InvalidDestination();
            }

            var outputName = MediaContainerPolicy.TrimmedFileName(components[^1]);
            var relativeOutput = Path.Combine([.. components[..^1], outputName]);
            var destination = Path.GetFullPath(Path.Combine(request.OutputRoot, relativeOutput));

            if (!IsWithinDirectory(request.OutputRoot, destination))
            {
                return InvalidDestination();
            }

            return new(destination, null);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return InvalidDestination();
        }
    }

    /// <summary>Returns one explicit invalid-path result without embedding untrusted path details.</summary>
    private static FolderDestinationResult InvalidDestination() => new(null, new(ErrorCode.InvalidDestination));
}
