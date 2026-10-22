using System.Collections.Immutable;

namespace TrackTrim.Core.Media;

public enum TrackKind { Video, Audio, Subtitles }
public enum TrackDisposition { HearingImpaired, VisualImpaired, TextDescriptions, Original, Commentary }
public enum MediaContainer { Matroska, Mp4 }

public sealed record SourceRevision(string FullPath, string FileIdentity, long Length, long LastWriteUtcTicks);

public sealed record BackendIdentity(string Name, string Version, int IdentificationFormatVersion);

public sealed record MediaTrack(
    int Id,
    ulong? Uid,
    TrackKind Kind,
    string Codec,
    string Language,
    string? LanguageIetf,
    string Title,
    int? Channels,
    bool IsDefault,
    bool IsForced,
    bool IsEnabled,
    ImmutableDictionary<string, string> Properties,
    ImmutableArray<TrackDisposition> Dispositions = default);

public sealed record Attachment(int Id, ulong Uid, string Name, string ContentType, long Size, string Description = "");

public sealed record MediaInspection(
    SourceRevision Source,
    BackendIdentity Backend,
    ImmutableArray<MediaTrack> Tracks,
    ImmutableArray<Attachment> Attachments,
    string Title,
    bool HasChapters,
    bool HasTags,
    MediaContainer Container = MediaContainer.Matroska,
    ImmutableDictionary<string, string>? ContainerProperties = null);
