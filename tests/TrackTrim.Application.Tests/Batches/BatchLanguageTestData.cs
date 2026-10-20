using TrackTrim.Core.Rules;

namespace TrackTrim.Application.Tests.Batches;

/// <summary>Supplies immutable rule inputs and deliberately different file-local language mappings.</summary>
internal static class BatchLanguageTestData
{
    public static LanguageRules Unchanged { get; } = new(
        new(LanguageRuleMode.Unchanged, []), new(LanguageRuleMode.Unchanged, []));
    public static LanguageRules KeepRussianSubtitles { get; } = new(
        new(LanguageRuleMode.Unchanged, []), new(LanguageRuleMode.KeepMatching, ["rus"]));
    public static LanguageRules KeepEnglishAudio { get; } = new(
        new(LanguageRuleMode.KeepMatching, ["eng"]), new(LanguageRuleMode.Unchanged, []));

    /// <summary>Uses different audio positions and local identifiers while retaining original immutable track metadata.</summary>
    public static void ConfigureDistinctLanguages(BatchTestHarness harness)
    {
        var paths = harness.Inspections.Keys.ToArray();
        var first = harness.Inspections[paths[0]];
        var second = harness.Inspections[paths[1]];
        harness.Inspections[paths[0]] = first with
        {
            Tracks = [first.Tracks[0], first.Tracks[1],
                first.Tracks[1] with { Id = 7, Uid = 700, Language = "fra", LanguageIetf = "fr" }, first.Tracks[2]]
        };
        harness.Inspections[paths[1]] = second with
        {
            Tracks = [second.Tracks[2] with { Language = "rus", LanguageIetf = "ru" }, second.Tracks[0],
                second.Tracks[1] with { Id = 44, Uid = 4400 },
                second.Tracks[1] with { Language = "fre", LanguageIetf = "fr" }]
        };
    }
}
