namespace MovieSift.Infrastructure.MediaTools;

/// <summary>Identifies the pinned Matroska workers or an explicit development override.</summary>
public sealed record MkvToolPaths(string MkvMerge, string MkvExtract, bool IsDevelopmentOverride = false,
    string? NativeLibraryDirectory = null)
{
    public const string SupportedVersion = "102.0";

    /// <summary>Resolves the matching source-header editor beside the actual native workers, never through PATH.</summary>
    public string MkvPropEdit => Path.Combine(Path.GetDirectoryName(MkvMerge)!, OperatingSystem.IsWindows() ? "mkvpropedit.exe" : "mkvpropedit");

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
