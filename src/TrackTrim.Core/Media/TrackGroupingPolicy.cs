using TrackTrim.Core.Rules;

namespace TrackTrim.Core.Media;

/// <summary>Groups exact metadata while retaining unknown language occurrences as explicit ambiguities.</summary>
public static class TrackGroupingPolicy
{
    /// <summary>Reuses the rules' alias policy without changing inspected metadata or comparing local identities.</summary>
    public static TrackGroupingDescription Describe(MediaTrack track)
    {
        var language = LanguageRuleMatcher.ReadTrackLanguage(track);
        var dispositions = track.Dispositions.IsDefaultOrEmpty ? 0 : track.Dispositions
            .Distinct().Aggregate(0, (flags, disposition) => flags | (1 << (int)disposition));
        return new(new(track.Kind, language.Tag, track.Title.Trim(), track.Codec,
            track.Kind == TrackKind.Audio ? track.Channels : null, track.IsForced, dispositions), language.Issue);
    }
}
