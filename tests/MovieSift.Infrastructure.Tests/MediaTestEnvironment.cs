using System.Runtime.CompilerServices;
using MovieSift.Infrastructure.Files;
using MovieSift.Infrastructure.MediaTools;
using Xunit;

namespace MovieSift.Infrastructure.Tests;

/// <summary>Skips optional local media checks while required CI checks fail on missing provisioning.</summary>
public sealed class MediaFactAttribute : FactAttribute
{
    /// <summary>Preserves xUnit source information and applies explicit local provisioning policy.</summary>
    public MediaFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {

        if (!MediaTestEnvironment.IsRequired && !MediaTestEnvironment.IsProvisioned)
        {
            Skip = "Pinned MKVToolNix tools/fixtures were not provisioned; media acceptance remains unverified.";
        }
    }
}

/// <summary>Applies the same explicit provisioning policy to data-driven real media checks.</summary>
public sealed class MediaTheoryAttribute : TheoryAttribute
{
    /// <summary>Preserves xUnit source information and applies explicit local provisioning policy.</summary>
    public MediaTheoryAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {

        if (!MediaTestEnvironment.IsRequired && !MediaTestEnvironment.IsProvisioned)
        {
            Skip = "Pinned MKVToolNix tools/fixtures were not provisioned; media acceptance remains unverified.";
        }
    }
}

/// <summary>Resolves only explicit test tools and original generated fixtures.</summary>
internal static class MediaTestEnvironment
{
    internal static bool IsRequired => Environment.GetEnvironmentVariable("MOVIESIFT_REQUIRE_MEDIA_TOOLS") == "1";
    internal static string ToolDirectory => Environment.GetEnvironmentVariable("MOVIESIFT_TOOL_DIRECTORY") ?? string.Empty;
    internal static string FixtureDirectory => Environment.GetEnvironmentVariable("MOVIESIFT_FIXTURE_DIRECTORY")
        ?? Path.Combine(RepositoryDirectory, "artifacts", "fixtures");
    internal static bool IsProvisioned => ToolDirectory.Length != 0 && File.Exists(Path.Combine(FixtureDirectory, "ordinary.mkv"));
    internal static string RepositoryDirectory
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null)
            {

                if (File.Exists(Path.Combine(directory.FullName, "build", "generate-fixtures.py")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("The fixture generator repository root could not be located.");
        }
    }

    /// <summary>Constructs the same backend as production with an explicit development-only tool override.</summary>
    internal static MkvBackend CreateBackend(string scratchDirectory)
    {
        Assert.True(IsProvisioned, "Required media tools and generated fixtures must be provisioned before this test.");
        var toolPaths = MkvToolPaths.FromDirectory(ToolDirectory) with { IsDevelopmentOverride = true };
        return new MkvBackend(new MkvBackendOptions(new FileSourceAccess(), toolPaths, scratchDirectory));
    }
}
