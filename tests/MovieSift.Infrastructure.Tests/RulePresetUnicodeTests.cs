using System.Text;
using MovieSift.Application.Presets;
using MovieSift.Core.Rules;
using MovieSift.Infrastructure.Persistence;
using Xunit;

namespace MovieSift.Infrastructure.Tests;

/// <summary>Exercises escaped property-name decoding through real portable catalog files.</summary>
public sealed class RulePresetUnicodeTests
{
    public static TheoryData<string> MalformedPropertyNames => new()
    {
        """{"version":1,"presets":[],"\uD800":0}""",
        """{"version":1,"presets":[],"\uDC00":0}""",
        """{"version":1,"presets":[],"\uD800x":0}""",
        """{"version":1,"presets":[{"\uD800":0}]}""",
    };

    /// <summary>Classifies escaped unpaired surrogates as invalid data without substituting an empty catalog.</summary>
    [Theory]
    [MemberData(nameof(MalformedPropertyNames))]
    public async Task ReadRejectsMalformedPropertyNamesAndPreservesExactAsciiBytes(string json)
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Path, "rule-presets.json");
        var original = Encoding.ASCII.GetBytes(json);
        await File.WriteAllBytesAsync(path, original, TestContext.Current.CancellationToken);
        var result = await new PortableRulePresetStore(workspace.Path).ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(RulePresetErrorCode.InvalidCatalog, result.Failure?.Code);
        Assert.Null(result.Catalog);
        Assert.False(result.Committed);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(["rule-presets.json"], Directory.GetFiles(workspace.Path).Select(Path.GetFileName));
    }

    /// <summary>Rejects a mutation at the same parser boundary without publishing or leaking an owned temporary file.</summary>
    [Theory]
    [MemberData(nameof(MalformedPropertyNames))]
    public async Task MutationRejectsMalformedPropertyNamesAndPreservesExactAsciiBytes(string json)
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Path, "rule-presets.json");
        var original = Encoding.ASCII.GetBytes(json);
        await File.WriteAllBytesAsync(path, original, TestContext.Current.CancellationToken);
        var service = new RulePresetService(new PortableRulePresetStore(workspace.Path));
        var rules = new LanguageRules(new(LanguageRuleMode.Unchanged, []), new(LanguageRuleMode.Unchanged, []));
        var result = await service.SaveAsNewAsync(new("Must not publish", rules, TestContext.Current.CancellationToken));

        Assert.Equal(RulePresetErrorCode.InvalidCatalog, result.Failure?.Code);
        Assert.Null(result.Catalog);
        Assert.False(result.Committed);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(workspace.Path, ".rule-presets.*.tmp"));
        Assert.Equal(["rule-presets.json", "rule-presets.lock"],
            Directory.GetFiles(workspace.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    /// <summary>Accepts a complete escaped surrogate pair in user-authored names and literal title values.</summary>
    [Fact]
    public async Task ValidSurrogatePairRoundTripsThroughFreshStore()
    {
        using var workspace = new TestWorkspace();
        const string name = "Сериал \U0001F680";
        const string title = " Captions \U0001F680 [literal].* ";
        var rules = new LanguageRules(new(LanguageRuleMode.Unchanged, []),
            new(LanguageRuleMode.KeepMatching, ["eng"], new(true, title)), true);
        var saved = await new RulePresetService(new PortableRulePresetStore(workspace.Path))
            .SaveAsNewAsync(new(name, rules, TestContext.Current.CancellationToken));
        Assert.Null(saved.Failure);
        Assert.True(saved.Committed);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(workspace.Path, "rule-presets.json"), TestContext.Current.CancellationToken);
        Assert.Contains("\\uD83D\\uDE80", Encoding.ASCII.GetString(bytes), StringComparison.OrdinalIgnoreCase);

        var loaded = await new RulePresetService(new PortableRulePresetStore(workspace.Path))
            .LoadAsync(new(saved.AffectedPresetId!.Value, TestContext.Current.CancellationToken));
        Assert.Null(loaded.Failure);
        Assert.Equal(name, loaded.Preset!.Name);
        Assert.Equal(title, loaded.Preset.Rules.Subtitles.TitleFilter!.Text);
        Assert.True(LanguageRuleConfiguration.ContentEquals(rules, loaded.Preset.Rules));
        Assert.Empty(Directory.GetFiles(workspace.Path, ".rule-presets.*.tmp"));
    }
}
