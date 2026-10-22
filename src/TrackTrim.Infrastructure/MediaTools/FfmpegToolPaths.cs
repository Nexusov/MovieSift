namespace TrackTrim.Infrastructure.MediaTools;

/// <summary>Identifies the pinned native FFmpeg/ffprobe pair used only by the MP4 adapter.</summary>
public sealed record FfmpegToolPaths(string Ffmpeg, string Ffprobe, bool IsDevelopmentOverride = false)
{
    public const string SupportedVersion = "n9.0.2-17-g2a571b6068-20260930";

    /// <summary>Resolves both executables from the relocated portable application.</summary>
    public static FfmpegToolPaths FromApplicationDirectory() => FromDirectory(Path.Combine(AppContext.BaseDirectory, "tools"));

    /// <summary>Uses direct absolute executable paths without PATH lookup or a shell wrapper.</summary>
    public static FfmpegToolPaths FromDirectory(string directory)
    {
        var suffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        return new(Path.Combine(directory, "ffmpeg" + suffix), Path.Combine(directory, "ffprobe" + suffix));
    }
}
