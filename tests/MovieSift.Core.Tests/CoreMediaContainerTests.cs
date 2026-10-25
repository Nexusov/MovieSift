using MovieSift.Core.Failures;
using MovieSift.Core.Planning;
using Xunit;

namespace MovieSift.Core.Tests;

/// <summary>Checks format-preserving output naming and containment for mixed-container folders.</summary>
public sealed class CoreMediaContainerTests
{
    /// <summary>Sources sharing a stem keep their container and relative season without artificial renaming.</summary>
    [Fact]
    public void MixedContainerNamesRetainRelativeParentsAndDistinctSuffixes()
    {
        var root = Path.Combine(Path.GetTempPath(), "MovieSift mixed outputs Кириллица");
        var matroska = OutputPathPolicy.DeriveFolderDestination(new(root, Path.Combine("Season 1", "episode.mkv")));
        var mp4 = OutputPathPolicy.DeriveFolderDestination(new(root, Path.Combine("Season 1", "episode.MP4")));
        var otherSeason = OutputPathPolicy.DeriveFolderDestination(new(root, Path.Combine("Season 2", "episode.mp4")));
        var flat = OutputPathPolicy.DeriveFolderDestination(new(root, "episode.mp4"));

        Assert.Null(matroska.Failure);
        Assert.Null(mp4.Failure);
        Assert.Null(otherSeason.Failure);
        Assert.Null(flat.Failure);
        Assert.Equal(Path.Combine(root, "Season 1", "episode.trimmed.mkv"), matroska.DestinationPath);
        Assert.Equal(Path.Combine(root, "Season 1", "episode.trimmed.mp4"), mp4.DestinationPath);
        Assert.Equal(Path.Combine(root, "Season 2", "episode.trimmed.mp4"), otherSeason.DestinationPath);
        Assert.Equal(Path.Combine(root, "episode.trimmed.mp4"), flat.DestinationPath);
        Assert.Equal(4, new[] { matroska, mp4, otherSeason, flat }.Select(result => result.DestinationPath).Distinct().Count());
    }

    /// <summary>Adding MP4 does not permit traversal, rooted paths or other containers in folder provenance.</summary>
    [Theory]
    [InlineData("../episode.mp4")]
    [InlineData("Season/../episode.mp4")]
    [InlineData("/episode.mp4")]
    [InlineData("Season//episode.mp4")]
    [InlineData("episode.mov")]
    [InlineData("episode.m4v")]
    public void InvalidMp4RelativePathsCannotEscapeTheOutputRoot(string relativePath)
    {
        var root = Path.Combine(Path.GetTempPath(), "MovieSift bounded MP4 outputs");
        var result = OutputPathPolicy.DeriveFolderDestination(new(root, relativePath));

        Assert.Equal(ErrorCode.InvalidDestination, result.Failure?.Code);
        Assert.Null(result.DestinationPath);
    }
}
