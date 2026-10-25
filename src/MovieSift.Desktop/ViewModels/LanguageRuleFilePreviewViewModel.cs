using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MovieSift.Application.Batches;
using MovieSift.Core.Media;
using MovieSift.Desktop.Localization;

namespace MovieSift.Desktop.ViewModels;

/// <summary>Describes one source's original metadata and proposed choices without creating an approved plan.</summary>
public sealed class LanguageRuleFilePreviewViewModel(
    BatchLanguagePreviewItem item,
    LocalizationService localization,
    bool applied) : ObservableObject
{
    public BatchLanguagePreviewItem Item { get; } = item;
    public string SourcePath => Item.SourcePath;
    public string StateText => applied && Item.State == BatchLanguagePreviewState.Changed
        ? localization["RuleFileApplied"] : localization[$"RuleFile{Item.State}"];
    public string ChoicesHeading => localization[Item.State is BatchLanguagePreviewState.Changed or BatchLanguagePreviewState.Unchanged
        ? "RuleProposedChoices" : "RuleCurrentChoices"];
    public bool HasTracks => Item.Inspection is not null;
    public string IssuesText => string.Join(Environment.NewLine, Item.Issues.Select(issue =>
    {
        var identifier = issue.TrackId is { } trackId ? $" {trackId.ToString(CultureInfo.InvariantCulture)}" : string.Empty;
        var value = issue.Value is { } original ? $" ({original})" : string.Empty;
        return $"{localization[issue.Category.ToString()]}{identifier}: {localization[$"RuleIssue{issue.Reason}"]}{value}";
    }));
    public bool HasIssues => !Item.Issues.IsEmpty;
    public bool NoSubtitleMatches => Item.NoSubtitleMatches;
    public string NoSubtitleMatchesText => localization["RuleNoSubtitleMatches"];
    public string ChoicesText => DescribeChoices();

    /// <summary>Localizes labels only; inspected metadata, proposal and source identity remain unchanged.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);

    /// <summary>Lists every actual track with its file-local identity and original language fields.</summary>
    private string DescribeChoices()
    {
        var retained = Item.ProposedRetainedTrackIds.ToHashSet();
        return string.Join(Environment.NewLine, Item.Inspection?.Tracks.Select(track =>
        {
            var action = localization[retained.Contains(track.Id) ? "Keep" : "Remove"];
            var title = string.IsNullOrWhiteSpace(track.Title) ? localization["Untitled"] : track.Title;
            var language = $"{localization["RuleLegacyLanguage"]}: {track.Language}; IETF: {track.LanguageIetf ?? localization["Unknown"]}";
            var channels = track.Kind == TrackKind.Audio
                ? $"; {localization["Channels"]}: {track.Channels?.ToString(CultureInfo.InvariantCulture) ?? localization["Unknown"]}" : string.Empty;
            var dispositionFlags = track.Dispositions.IsDefaultOrEmpty ? [] : track.Dispositions.Select(disposition => localization[$"Disposition{disposition}"]);
            var flags = string.Join(", ", new[]
            {
                track.IsDefault ? localization["Default"] : null,
                track.IsForced ? localization["Forced"] : null,
                localization[track.IsEnabled ? "Enabled" : "Disabled"]
            }.Where(flag => flag is not null).Concat(dispositionFlags));
            return $"{action}: {localization[track.Kind.ToString()]} {track.Id.ToString(CultureInfo.InvariantCulture)} — {title} ({track.Codec}; {language}{channels}; {flags})";
        }) ?? []);
    }
}
