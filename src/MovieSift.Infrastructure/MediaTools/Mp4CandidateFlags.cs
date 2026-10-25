using System.Globalization;
using Microsoft.Win32.SafeHandles;
using MovieSift.Application;
using MovieSift.Core.Failures;
using MovieSift.Core.Media;
using MovieSift.Infrastructure.Files;

namespace MovieSift.Infrastructure.MediaTools;

/// <summary>Restores only approved ordinary tkhd presentation flags on the newly created owned candidate after the pinned muxer's header rewriting.</summary>
internal static class Mp4CandidateFlags
{
    private const int LinuxReadWrite = 2;
    private const int LinuxNonBlocking = 0x800;
    private const int LinuxNoFollow = 0x20000;
    private const int LinuxCloseOnExec = 0x80000;
    private const uint UnixFileTypeMask = 0xF000;
    private const uint UnixRegularFile = 0x8000;
    private const uint FileTypeDisk = 1;
    private const int FlagBytes = 3;

    /// <summary>Requires a distinct single-link regular output, exact inspected mapping and the same handle identity before touching three flag bytes.</summary>
    internal static async Task RestoreAsync(CandidateFlagRequest request)
    {
        if (request.Source.FileIdentity == request.Candidate.Source.FileIdentity
            || request.Source.FullPath == request.Candidate.Source.FullPath)
        {
            throw Invalid("The MP4 flag fixup must never target the source.");
        }

        var failure = Mp4Probe.CompareTracks(new(request.Expected, request.Candidate.Tracks, IgnoreHeaderFlags: true));

        if (failure is not null)
        {
            throw Invalid("Candidate mapping must be verified before restoring MP4 track header flags. " + failure);
        }

        var path = request.Candidate.Source.FullPath;
        PathSafety.RejectReparsePoints(path);
        using var handle = OpenExclusive(path);
        ValidateIdentity(handle, request.Candidate.Source);
        await using var stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 4096, isAsync: false);
        var structure = await Mp4BoxReader.ReadAsync(stream, request.CancellationToken).ConfigureAwait(false);
        var actualTracks = request.Candidate.Tracks.Where(track => track.Properties["attached_pic"] == "0").ToArray();

        if (structure.Tracks.Count(track => !structure.ChapterTrackIds.Contains(track.Id)) != actualTracks.Length)
        {
            throw Invalid("The candidate container track count changed before MP4 flag restoration.");
        }

        foreach (var actual in actualTracks)
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            var outputIndex = request.Candidate.Tracks.IndexOf(actual);
            var expected = request.Expected[outputIndex];
            var containerId = uint.Parse(actual.Properties["container_track_id"], CultureInfo.InvariantCulture);
            var header = structure.Tracks.Single(track => track.Id == containerId);
            var flags = 0u;
            var validFlags = expected.Properties.TryGetValue("tkhd_flags", out var text)
                && uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out flags);

            if (!validFlags)
            {
                throw Invalid("The approved MP4 track header flags are missing or malformed.");
            }

            if ((flags & ~Mp4TrackHeader.SupportedFlags) != 0)
            {
                throw Invalid("The approved MP4 track header flags are outside the supported mask.");
            }

            if (flags == header.Flags)
            {
                continue;
            }

            var bytes = new byte[FlagBytes] { (byte)(flags >> 16), (byte)(flags >> 8), (byte)flags };
            stream.Position = header.FlagsOffset;
            await stream.WriteAsync(bytes, request.CancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(request.CancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Opens only a regular candidate without Linux FIFO blocking or final symlink following.</summary>
    private static SafeFileHandle OpenExclusive(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }

        var descriptor = NativeMethods.Open(path, LinuxReadWrite | LinuxNonBlocking | LinuxNoFollow | LinuxCloseOnExec);

        if (descriptor < 0)
        {
            throw Invalid("The newly created MP4 candidate could not be opened safely for flag restoration.");
        }

        return new((IntPtr)descriptor, ownsHandle: true);
    }

    /// <summary>Checks the opened object, not just a pathname, and refuses hard-linked or substituted media.</summary>
    private static void ValidateIdentity(SafeFileHandle handle, SourceRevision expected)
    {
        string identity;
        long length;

        if (OperatingSystem.IsWindows())
        {
            var information = new WindowsFileInformation();
            var valid = NativeMethods.GetFileType(handle) == FileTypeDisk
                && NativeMethods.GetFileInformationByHandle(handle, out information);

            if (!valid || information.NumberOfLinks != 1)
            {
                throw Invalid("The MP4 candidate is not an exclusively owned regular file.");
            }

            identity = string.Create(CultureInfo.InvariantCulture, $"{information.VolumeSerialNumber:X8}:{information.FileIndexHigh:X8}{information.FileIndexLow:X8}");
            length = ((long)information.FileSizeHigh << 32) | information.FileSizeLow;
        }
        else
        {
            var result = NativeMethods.FStat(handle.DangerousGetHandle().ToInt32(), out var information);
            var valid = result == 0 && (information.Mode & UnixFileTypeMask) == UnixRegularFile && information.NumberOfLinks == 1;

            if (!valid)
            {
                throw Invalid("The MP4 candidate is not an exclusively owned regular file.");
            }

            identity = string.Create(CultureInfo.InvariantCulture, $"{information.Device:X}:{information.Inode:X}");
            length = information.Size;
        }

        if (identity != expected.FileIdentity || length != expected.Length)
        {
            throw Invalid("The MP4 candidate identity changed before flag restoration.");
        }
    }

    /// <summary>Reports a failed owned-candidate fixup without authorizing publication.</summary>
    private static OperationFailureException Invalid(string detail) => new(new(ErrorCode.VerificationFailed, detail));
}

internal sealed record CandidateFlagRequest(SourceRevision Source, IReadOnlyList<MediaTrack> Expected,
    MediaInspection Candidate, CancellationToken CancellationToken);
