using System.Text;

namespace MovieSift.Infrastructure.Files;

/// <summary>Recognizes the bounded portable marker without inferring data roots from a working directory or environment variable.</summary>
public static class PortableLayout
{
    public const string PayloadDirectoryName = "app";
    public const string NestedMarker = "MovieSift portable data policy version 2";
    private const int MaximumMarkerBytes = 128;

    /// <summary>The root apphost loads assemblies from app while data and installation coordination stay beside the launcher.</summary>
    public static string ApplicationRootFromBaseDirectory(string baseDirectory)
    {
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        var parent = Path.GetDirectoryName(directory);
        var isPayloadDirectory = string.Equals(Path.GetFileName(directory), PayloadDirectoryName,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        if (!isPayloadDirectory || parent is null)
        {
            return directory;
        }

        return HasNestedMarker(parent) ? parent : directory;
    }

    /// <summary>Resolves only the supported fixed payload location; legacy flat installations remain readable.</summary>
    public static string PayloadDirectory(string applicationRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationRoot));
        return HasNestedMarker(root) ? Path.Combine(root, PayloadDirectoryName) : root;
    }

    /// <summary>Reads an ordinary bounded marker, rejecting redirected or special records through the existing file boundary.</summary>
    private static bool HasNestedMarker(string root)
    {
        var marker = Path.Combine(root, "portable.flag");

        if (!File.Exists(marker))
        {
            return false;
        }

        using var stream = FileSourceAccess.OpenRegularReadStream(marker);
        var bytes = new byte[MaximumMarkerBytes + 1];
        var count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);

        if (count > MaximumMarkerBytes)
        {
            throw new IOException("The portable layout marker exceeds its size limit.");
        }

        return Encoding.UTF8.GetString(bytes, 0, count).Trim() == NestedMarker;
    }
}
