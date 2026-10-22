namespace TrackTrim.Core.Media;

/// <summary>Defines the two admitted filename conventions without claiming that a suffix proves the container.</summary>
public static class MediaContainerPolicy
{
    /// <summary>Returns an input candidate's declared container; the backend must still inspect its actual contents.</summary>
    public static MediaContainer? FromExtension(string path)
    {
        var extension = Path.GetExtension(path);

        if (string.Equals(extension, ".mkv", StringComparison.OrdinalIgnoreCase))
        {
            return MediaContainer.Matroska;
        }

        if (string.Equals(extension, ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            return MediaContainer.Mp4;
        }

        return null;
    }

    /// <summary>Restricts output and candidate extensions to explicit supported containers.</summary>
    public static string Extension(MediaContainer container) => container switch
    {
        MediaContainer.Matroska => ".mkv",
        MediaContainer.Mp4 => ".mp4",
        _ => throw new ArgumentOutOfRangeException(nameof(container)),
    };

    /// <summary>Preserves the candidate's container while applying the established separate-output convention.</summary>
    public static string TrimmedFileName(string sourcePath)
    {
        var container = FromExtension(sourcePath) ?? throw new ArgumentException("A supported media suffix is required.", nameof(sourcePath));
        return Path.GetFileNameWithoutExtension(sourcePath) + ".trimmed" + Extension(container);
    }
}
