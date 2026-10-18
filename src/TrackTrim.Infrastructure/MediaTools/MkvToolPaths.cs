namespace TrackTrim.Infrastructure.MediaTools;

/// <summary>Identifies the pinned production tool pair or an explicit development override.</summary>
public sealed record MkvToolPaths(string MkvMerge, string MkvExtract, bool IsDevelopmentOverride = false,
    string? NativeLibraryDirectory = null)
{
    public const string SupportedVersion = "102.0";

    /// <summary>Resolves production tools from the relocated application directory.</summary>
    public static MkvToolPaths FromApplicationDirectory()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "tools");
        return FromDirectory(directory);
    }

    /// <summary>Resolves native tools directly; Linux runtime media work never invokes a shell wrapper.</summary>
    public static MkvToolPaths FromDirectory(string directory)
    {

        if (OperatingSystem.IsWindows())
        {
            return new MkvToolPaths(Path.Combine(directory, "mkvmerge.exe"), Path.Combine(directory, "mkvextract.exe"));
        }

        var applicationRoot = Path.Combine(directory, "app-root");
        return new MkvToolPaths(Path.Combine(applicationRoot, "usr", "bin", "mkvmerge"),
            Path.Combine(applicationRoot, "usr", "bin", "mkvextract"),
            NativeLibraryDirectory: Path.Combine(applicationRoot, "usr", "lib"));
    }
}
