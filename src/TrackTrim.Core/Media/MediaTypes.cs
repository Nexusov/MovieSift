using System.Collections.Immutable;

namespace TrackTrim.Core.Media;

public enum TrackKind { Video, Audio, Subtitles }

public sealed record SourceRevision(string FullPath, string FileIdentity, long Length, long LastWriteUtcTicks);

public sealed record BackendIdentity(string Name, string Version, int IdentificationFormatVersion);

public sealed record MediaTrack(
    int Id,
    ulong Uid,
    TrackKind Kind,
    string Codec,
    string Language,
    string? LanguageIetf,
    string Title,
    int? Channels,
    bool IsDefault,
    bool IsForced,
    bool IsEnabled,
    ImmutableDictionary<string, string> Properties);

public sealed record Attachment(int Id, ulong Uid, string Name, string ContentType, long Size);

public sealed record MediaInspection(
    SourceRevision Source,
    BackendIdentity Backend,
    ImmutableArray<MediaTrack> Tracks,
    ImmutableArray<Attachment> Attachments,
    string Title,
    bool HasChapters,
    bool HasTags);
