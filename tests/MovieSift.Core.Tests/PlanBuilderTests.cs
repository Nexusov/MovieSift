using System.Collections.Immutable;
using MovieSift.Core.Failures;
using MovieSift.Core.Media;
using MovieSift.Core.Planning;
using Xunit;

namespace MovieSift.Core.Tests;

/// <summary>Exercises immutable retention and review decisions without filesystem access.</summary>
public sealed class PlanBuilderTests
{
    private static readonly MediaInspection Inspection = new(
        new("source.mkv", "identity", 100, 200), new("mkvmerge", "102.0", 20),
        [Track(new(0, TrackKind.Video)), Track(new(1, TrackKind.Audio, true)),
            Track(new(2, TrackKind.Audio)), Track(new(3, TrackKind.Subtitles))], [], "Title", false, false);

    /// <summary>Snapshots mutable selections and retains the inspected order.</summary>
    [Fact]
    public void SelectionIsSnapshottedInSourceOrder()
    {
        var identifiers = new List<int> { 3, 2, 0 };
        var result = PlanBuilder.Build(new(Inspection, identifiers, "output.mkv", false));
        identifiers.Clear();
        var plan = Assert.IsType<RemuxPlan>(result.Plan);
        Assert.Equal([0, 2, 3], plan.RetainedTracks.Select(track => track.Id));
        Assert.True(plan.RemovesDefaultAudio);
        Assert.False(plan.HasDefaultAudio);
        Assert.False(plan.RetainedTracks.Single(track => track.Kind == TrackKind.Audio).IsDefault);
    }

    /// <summary>Requires explicit review before removing every existing audio track.</summary>
    [Fact]
    public void AllAudioRemovalRequiresConfirmation()
    {
        var result = PlanBuilder.Build(new(Inspection, [0], "output.mkv", false));
        Assert.Equal(ErrorCode.ZeroAudioConfirmationRequired, result.Failure?.Code);
        Assert.Null(result.Plan);
    }

    /// <summary>Allows a confirmed silent output with no subtitles.</summary>
    [Fact]
    public void ConfirmedZeroAudioAndNoSubtitlesIsValid()
    {
        var result = PlanBuilder.Build(new(Inspection, [0], "output.mkv", true));
        Assert.True(result.IsSuccess);
        Assert.Single(result.Plan!.RetainedTracks);
    }

    /// <summary>Does not request audio confirmation for an already silent input.</summary>
    [Fact]
    public void AlreadySilentInputIsValid()
    {
        var silent = Inspection with { Tracks = [Inspection.Tracks[0], Inspection.Tracks[3]] };
        var result = PlanBuilder.Build(new(silent, [0], "output.mkv", false));
        Assert.True(result.IsSuccess);
    }

    /// <summary>Rejects video removal, unknown identifiers and duplicate identifiers.</summary>
    [Theory]
    [InlineData(new[] { 1, 2, 3 })]
    [InlineData(new[] { 0, 1, 99 })]
    [InlineData(new[] { 0, 1, 1 })]
    public void InvalidSelectionIsRejected(int[] identifiers)
    {
        var result = PlanBuilder.Build(new(Inspection, identifiers, "output.mkv", false));
        Assert.Equal(ErrorCode.InvalidSelection, result.Failure?.Code);
    }

    /// <summary>Identifies a no-change selection without depending on selected-row ordering.</summary>
    [Fact]
    public void AllTracksProducesNoChangePlan()
    {
        var result = PlanBuilder.Build(new(Inspection, [3, 2, 1, 0], "output.mkv", false));
        Assert.True(result.Plan?.IsNoChange);
    }

    /// <summary>A no-copy selection has no output path, extension or directory-root requirement.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("unused.txt")]
    public void NoChangeDoesNotValidateUnusedDestination(string destination)
    {
        var result = PlanBuilder.Build(new(Inspection, [0, 1, 2, 3], destination, false, "unrelated-root"));

        Assert.Null(result.Failure);
        Assert.True(result.Plan?.IsNoChange);
    }

    /// <summary>Freezes the actual inspected container and permits no implicit container conversion.</summary>
    [Theory]
    [InlineData(MediaContainer.Matroska, "output.mkv", true)]
    [InlineData(MediaContainer.Matroska, "output.mp4", false)]
    [InlineData(MediaContainer.Mp4, "output.mp4", true)]
    [InlineData(MediaContainer.Mp4, "output.mkv", false)]
    [InlineData((MediaContainer)99, "output.mp4", false)]
    public void DestinationMustMatchTheInspectedContainer(MediaContainer container, string destination, bool expectedSuccess)
    {
        var inspection = Inspection with { Container = container };
        var result = PlanBuilder.Build(new(inspection, [0, 1], destination, false));

        Assert.Equal(expectedSuccess, result.IsSuccess);

        if (expectedSuccess)
        {
            Assert.Equal(container, result.Plan!.Inspection.Container);
            return;
        }

        Assert.Equal(ErrorCode.InvalidDestination, result.Failure?.Code);
    }

    /// <summary>Creates a representative track with immutable original properties.</summary>
    private static MediaTrack Track(TrackRequest request) => new(
        request.Id, (ulong)request.Id + 1, request.Kind, "codec", "eng", "en", "title", null,
        request.IsDefault, false, true, ImmutableDictionary<string, string>.Empty);

    private sealed record TrackRequest(int Id, TrackKind Kind, bool IsDefault = false);
}
