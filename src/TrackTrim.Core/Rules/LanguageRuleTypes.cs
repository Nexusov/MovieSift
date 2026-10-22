using System.Collections.Immutable;
using TrackTrim.Core.Media;

namespace TrackTrim.Core.Rules;

public enum LanguageRuleMode { Unchanged, KeepMatching }
public enum LanguageRuleIssueReason
{
    EmptyLanguageSelection,
    InvalidRuleLanguage,
    UnknownLanguage,
    MalformedLanguage,
    ConflictingLanguage,
    NoAudioMatches,
    InvalidCurrentSelection
}

public sealed record TrackLanguageRule(LanguageRuleMode Mode, ImmutableArray<string> Languages);
public sealed record LanguageRules(TrackLanguageRule Audio, TrackLanguageRule Subtitles);
public sealed record LanguageSelectionRequest(
    MediaInspection Inspection,
    ImmutableArray<int> CurrentRetainedTrackIds,
    LanguageRules Rules);
public sealed record LanguageSelectionProposal(
    ImmutableArray<int> RetainedTrackIds,
    ImmutableArray<LanguageRuleIssue> Issues,
    bool HasChanges,
    bool NoSubtitleMatches);
public sealed record LanguageRuleIssue(
    TrackKind Category,
    int? TrackId,
    LanguageRuleIssueReason Reason,
    string? Value = null);

internal sealed record ParsedLanguageTag(string? Primary, string? Tag, LanguageRuleIssueReason? Issue, string? Value = null);
internal sealed record CategoryLanguageSelection(
    TrackKind Category,
    TrackLanguageRule? Rule,
    ImmutableArray<MediaTrack> Tracks,
    HashSet<int> RetainedIdentifiers,
    ImmutableArray<LanguageRuleIssue>.Builder Issues);
