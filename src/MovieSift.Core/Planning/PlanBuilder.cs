using System.Collections.Immutable;
using MovieSift.Core.Failures;
using MovieSift.Core.Media;

namespace MovieSift.Core.Planning;

/// <summary>Creates a snapshot of the approved selections without performing I/O.</summary>
public static class PlanBuilder
{
    /// <summary>Validates a selection and preserves inspected stream order and metadata.</summary>
    public static PlanResult Build(PlanRequest request)
    {
        var inspection = request.Inspection;
        var retainedIdentifiers = request.RetainedTrackIds.ToHashSet();
        var availableIdentifiers = inspection.Tracks.Select(track => track.Id).ToHashSet();
        var hasInvalidIdentifiers = retainedIdentifiers.Count != request.RetainedTrackIds.Count ||
            retainedIdentifiers.Any(identifier => !availableIdentifiers.Contains(identifier));

        if (hasInvalidIdentifiers)
        {
            return new(null, new(ErrorCode.InvalidSelection));
        }

        var removesVideo = inspection.Tracks.Any(track =>
            track.Kind == TrackKind.Video && !retainedIdentifiers.Contains(track.Id));

        if (removesVideo)
        {
            return new(null, new(ErrorCode.InvalidSelection, "Every video track must be retained."));
        }

        var retainedTracks = inspection.Tracks.Where(track => retainedIdentifiers.Contains(track.Id)).ToImmutableArray();
        var removesAllAudio = inspection.Tracks.Any(track => track.Kind == TrackKind.Audio) &&
            !retainedTracks.Any(track => track.Kind == TrackKind.Audio);

        if (removesAllAudio && !request.ZeroAudioConfirmed)
        {
            return new(null, new(ErrorCode.ZeroAudioConfirmationRequired));
        }

        var invalidContainerDestination = !Enum.IsDefined(inspection.Container) ||
            string.IsNullOrWhiteSpace(request.DestinationPath) ||
            MediaContainerPolicy.FromExtension(request.DestinationPath) != inspection.Container;

        if (invalidContainerDestination)
        {
            return new(null, new(ErrorCode.InvalidDestination));
        }

        var hasInvalidOutputRoot = request.OutputDirectoryRoot is not null &&
            (!OutputPathPolicy.IsWithinDirectory(request.OutputDirectoryRoot, request.DestinationPath) ||
                Path.GetRelativePath(request.OutputDirectoryRoot, request.DestinationPath) == ".");

        if (hasInvalidOutputRoot)
        {
            return new(null, new(ErrorCode.InvalidDestination));
        }

        var plan = new RemuxPlan(Guid.NewGuid(), inspection, retainedTracks, request.DestinationPath,
            request.ZeroAudioConfirmed, request.OutputDirectoryRoot);
        return new(plan, null);
    }
}
