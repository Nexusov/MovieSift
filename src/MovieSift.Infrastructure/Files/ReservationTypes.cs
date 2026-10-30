using MovieSift.Core.Media;

namespace MovieSift.Infrastructure.Files;

internal enum ReservationBoundary { DirectoryCreated, MarkerCreated, MarkerWritten, ProjectionCreated, JournalPublished }

/// <summary>Tracks only this operation's resources before they can be returned to the job owner.</summary>
internal sealed class PartialReservation : IDisposable
{
    internal ReservationDirectory? Directory { get; set; }
    internal SourceRevision? Marker { get; set; }
    internal bool MarkerComplete { get; set; }
    internal SourceRevision? Journal { get; set; }
    internal string? ProjectionPath { get; set; }
    internal SourceRevision? Projection { get; set; }

    /// <summary>Releases metadata handles without granting path deletion authority.</summary>
    public void Dispose() => Directory?.Dispose();
}
