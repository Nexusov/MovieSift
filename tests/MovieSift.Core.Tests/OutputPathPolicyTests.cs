using MovieSift.Core.Failures;
using MovieSift.Core.Media;
using MovieSift.Core.Planning;
using Xunit;

namespace MovieSift.Core.Tests;

/// <summary>Checks relative-output derivation and component-aware containment without filesystem access.</summary>
public sealed class OutputPathPolicyTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "MovieSift output Кириллица");

    /// <summary>Distinguishes identical episode names by their original relative parents.</summary>
    [Fact]
    public void DifferentSeasonsKeepTheirRelativeDirectories()
    {
        var first = OutputPathPolicy.DeriveFolderDestination(new(Root, Path.Combine("Season 1", "episode.mkv")));
        var second = OutputPathPolicy.DeriveFolderDestination(new(Root, Path.Combine("Season 2", "episode.mkv")));
        Assert.Null(first.Failure);
        Assert.Null(second.Failure);
        Assert.Equal(Path.Combine(Root, "Season 1", "episode.trimmed.mkv"), first.DestinationPath);
        Assert.Equal(Path.Combine(Root, "Season 2", "episode.trimmed.mkv"), second.DestinationPath);
    }

    /// <summary>Rejects native root syntax, traversal and invalid components on both supported host platforms.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("../episode.mkv")]
    [InlineData("Season/../episode.mkv")]
    [InlineData("/episode.mkv")]
    [InlineData("Season//episode.mkv")]
    [InlineData("Season/./episode.mkv")]
    [InlineData("episode.mov")]
    public void InvalidRelativeSourceIsRejected(string relative)
    {
        var result = OutputPathPolicy.DeriveFolderDestination(new(Root, relative));
        Assert.Equal(ErrorCode.InvalidDestination, result.Failure?.Code);
        Assert.Null(result.DestinationPath);
    }

    /// <summary>Windows roots are rejected on Windows while literal Linux filenames remain unchanged.</summary>
    [Theory]
    [InlineData("Season\\..\\episode.mkv")]
    [InlineData("\\episode.mkv")]
    [InlineData("\\\\server\\share\\episode.mkv")]
    [InlineData("C:\\episode.mkv")]
    [InlineData("C:episode.mkv")]
    public void RootAndSeparatorSyntaxFollowsTheHostFilesystem(string relative)
    {
        var result = OutputPathPolicy.DeriveFolderDestination(new(Root, relative));

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(ErrorCode.InvalidDestination, result.Failure?.Code);
            Assert.Null(result.DestinationPath);
            return;
        }

        Assert.Null(result.Failure);
        Assert.Equal(Path.Combine(Root, Path.GetFileNameWithoutExtension(relative) + ".trimmed.mkv"), result.DestinationPath);
    }

    /// <summary>Does not confuse a sibling sharing the root's text prefix with a contained path.</summary>
    [Fact]
    public void ContainmentUsesPathComponentsAndRequiresAbsolutePaths()
    {
        Assert.True(OutputPathPolicy.IsWithinDirectory(Root, Root));
        Assert.True(OutputPathPolicy.IsWithinDirectory(Root, Path.Combine(Root, "Season 1", "episode.mkv")));
        Assert.False(OutputPathPolicy.IsWithinDirectory(Root, Root + " sibling"));
        Assert.False(OutputPathPolicy.IsWithinDirectory(Root, Path.Combine(Root, "..", "elsewhere", "episode.mkv")));
        Assert.False(OutputPathPolicy.IsWithinDirectory("relative", Path.Combine(Root, "episode.mkv")));
        Assert.False(OutputPathPolicy.IsWithinDirectory(Root, "relative.mkv"));
    }

    /// <summary>Only explicit nested-output plans receive an immutable containing-root authorization.</summary>
    [Fact]
    public void PlanCannotAuthorizeDirectoryCreationOutsideItsOutputRoot()
    {
        MediaInspection inspection = new(new("source.mkv", "source", 1, 1), new("mkvmerge", "102.0", 20),
            [], [], "", false, false);
        var inside = Path.Combine(Root, "Season 1", "episode.trimmed.mkv");
        var accepted = PlanBuilder.Build(new(inspection, [], inside, false, Root));
        Assert.Equal(Root, accepted.Plan?.OutputDirectoryRoot);
        var escaped = PlanBuilder.Build(new(inspection, [], Root + " sibling.mkv", false, Root));
        Assert.Equal(ErrorCode.InvalidDestination, escaped.Failure?.Code);
        var rootItself = PlanBuilder.Build(new(inspection, [], Root, false, Root));
        Assert.Equal(ErrorCode.InvalidDestination, rootItself.Failure?.Code);
        Assert.True(PlanBuilder.Build(new(inspection, [], "ordinary-output.mkv", false)).IsSuccess);
    }
}
