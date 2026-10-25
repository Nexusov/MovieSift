using System.Collections.Immutable;
using System.Text.Json.Serialization;
using MovieSift.Application.Presets;
using MovieSift.Core.Rules;

namespace MovieSift.Infrastructure.Persistence;

internal sealed record RulePresetDocument(
    [property: JsonRequired] int Version,
    [property: JsonRequired] ImmutableArray<RulePresetRecord> Presets);

internal sealed record RulePresetRecord(
    [property: JsonRequired] Guid Id,
    [property: JsonRequired] string Name,
    [property: JsonRequired] RulePresetConfiguration Rules);

internal sealed record RulePresetConfiguration(
    [property: JsonRequired] RulePresetTrackRule Audio,
    [property: JsonRequired] RulePresetTrackRule Subtitles,
    [property: JsonRequired] bool KeepOnlyForcedSubtitles);

internal sealed record RulePresetTrackRule(
    [property: JsonRequired] LanguageRuleMode Mode,
    [property: JsonRequired] ImmutableArray<string> Languages,
    [property: JsonRequired] RulePresetTitleFilter TitleFilter);

internal sealed record RulePresetTitleFilter(
    [property: JsonRequired] bool Enabled,
    [property: JsonRequired] string Text);

internal sealed record RulePresetFileActions(Action<string, string> Publish, Action<string> DeleteTemporary);

internal sealed class RulePresetWriteState
{
    internal string? TemporaryPath { get; set; }
}

internal sealed class RulePresetStorageException(RulePresetFailure failure) : Exception(failure.Detail)
{
    internal RulePresetFailure Failure { get; } = failure;
}
