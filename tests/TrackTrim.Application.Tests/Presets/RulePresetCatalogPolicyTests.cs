using System.Collections.Immutable;
using TrackTrim.Application.Presets;
using TrackTrim.Core.Rules;
using Xunit;

namespace TrackTrim.Application.Tests.Presets;

/// <summary>Checks complete-catalog rejection, bounded stored shape and immutable stable-identity CRUD policy.</summary>
public sealed class RulePresetCatalogPolicyTests
{
    private static LanguageRules Rules => new(
        new(LanguageRuleMode.Unchanged, ["fra", "deu"], new(false, "  Dormant [literal].*  ")),
        new(LanguageRuleMode.KeepMatching, ["eng", "en"], new(true, " Captions ")), true);

    /// <summary>Trims names while retaining raw ordered rules and existing entry identities across add, rename and delete.</summary>
    [Fact]
    public void CrudPreservesUnrelatedEntriesAndNeverMutatesInputCatalog()
    {
        var original = new RulePreset(Guid.NewGuid(), "Existing", Rules);
        var initial = new RulePresetCatalog([original]);
        var identifier = Guid.NewGuid();
        var snapshot = Rules;

        var added = RulePresetCatalogPolicy.Apply(initial, new(RulePresetMutationKind.Add, identifier, "  Movie rules  ", snapshot));

        Assert.Null(added.Failure);
        Assert.False(added.Committed);
        Assert.Equal(identifier, added.AffectedPresetId);
        Assert.Single(initial.Items);
        Assert.Same(original, added.Catalog!.Items[0]);
        var saved = added.Catalog.Items[1];
        Assert.Equal(identifier, saved.Id);
        Assert.Equal("Movie rules", saved.Name);
        Assert.Same(snapshot, saved.Rules);
        Assert.Equal(["fra", "deu"], saved.Rules.Audio.Languages);
        Assert.Equal("  Dormant [literal].*  ", saved.Rules.Audio.TitleFilter!.Text);

        var renamed = RulePresetCatalogPolicy.Apply(added.Catalog, new(RulePresetMutationKind.Rename, identifier, " New name "));
        Assert.Null(renamed.Failure);
        Assert.Equal(identifier, renamed.Catalog!.Items[1].Id);
        Assert.Equal("New name", renamed.Catalog.Items[1].Name);
        Assert.Same(snapshot, renamed.Catalog.Items[1].Rules);
        Assert.Same(original, renamed.Catalog.Items[0]);
        Assert.Equal("Movie rules", added.Catalog.Items[1].Name);

        var deleted = RulePresetCatalogPolicy.Apply(renamed.Catalog, new(RulePresetMutationKind.Delete, identifier));
        Assert.Null(deleted.Failure);
        Assert.Same(original, Assert.Single(deleted.Catalog!.Items));
        Assert.Equal(2, renamed.Catalog.Items.Length);
    }

    /// <summary>Rejects case-insensitive name collisions after trimming without replacing the existing preset.</summary>
    [Fact]
    public void AddAndRenameRejectOrdinalIgnoreCaseDuplicateNames()
    {
        var first = new RulePreset(Guid.NewGuid(), "Preset I", Rules);
        var second = new RulePreset(Guid.NewGuid(), "Other", Rules);
        var catalog = new RulePresetCatalog([first, second]);

        var added = RulePresetCatalogPolicy.Apply(catalog, new(RulePresetMutationKind.Add, Guid.NewGuid(), " preset i ", Rules));
        var renamed = RulePresetCatalogPolicy.Apply(catalog, new(RulePresetMutationKind.Rename, second.Id, " PRESET I "));

        Assert.Equal(RulePresetErrorCode.DuplicateName, added.Failure!.Code);
        Assert.Equal(RulePresetErrorCode.DuplicateName, renamed.Failure!.Code);
        Assert.Null(added.Catalog);
        Assert.Null(renamed.Catalog);
        Assert.Same(first, catalog.Items[0]);
        Assert.Same(second, catalog.Items[1]);
    }

    /// <summary>Preserves exact catalog identity for an unchanged rename and reports absent identities explicitly.</summary>
    [Fact]
    public void RenameNoOpAndUnknownIdentityDoNotProduceAReplacementCatalog()
    {
        var preset = new RulePreset(Guid.NewGuid(), "Original", Rules);
        var catalog = new RulePresetCatalog([preset]);

        var same = RulePresetCatalogPolicy.Apply(catalog, new(RulePresetMutationKind.Rename, preset.Id, " Original "));
        var missingRename = RulePresetCatalogPolicy.Apply(catalog, new(RulePresetMutationKind.Rename, Guid.NewGuid(), "Valid"));
        var missingDelete = RulePresetCatalogPolicy.Apply(catalog, new(RulePresetMutationKind.Delete, Guid.NewGuid()));

        Assert.Same(catalog, same.Catalog);
        Assert.False(same.Committed);
        Assert.Equal(preset.Id, same.AffectedPresetId);
        Assert.Equal(RulePresetErrorCode.NotFound, missingRename.Failure!.Code);
        Assert.Equal(RulePresetErrorCode.NotFound, missingDelete.Failure!.Code);
    }

    /// <summary>Rejects invalid names without inventing a filename or normalizing internal display spelling.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    [InlineData("\nName")]
    [InlineData("Name\t")]
    [InlineData("\nName\t")]
    [InlineData("Two\nlines")]
    [InlineData("Control\0character")]
    public void EmptyOrControlCharacterNamesAreInvalid(string? name)
    {
        Assert.Equal(RulePresetErrorCode.InvalidName, RulePresetCatalogPolicy.ValidateName(name)!.Code);
    }

    /// <summary>Rejects the whole catalog instead of exposing valid siblings from malformed stored entries.</summary>
    [Theory]
    [InlineData("default")]
    [InlineData("null-row")]
    [InlineData("empty-id")]
    [InlineData("duplicate-id")]
    [InlineData("duplicate-name")]
    [InlineData("untrimmed")]
    [InlineData("invalid-rules")]
    public void InvalidCatalogCannotBeReadOrRepairedByAnUnrelatedMutation(string invalid)
    {
        var valid = new RulePreset(Guid.NewGuid(), "Valid", Rules);
        var second = new RulePreset(Guid.NewGuid(), "Second", Rules);
        var catalog = invalid switch
        {
            "default" => new RulePresetCatalog(default),
            "null-row" => new RulePresetCatalog([valid, null!]),
            "empty-id" => new RulePresetCatalog([valid, second with { Id = Guid.Empty }]),
            "duplicate-id" => new RulePresetCatalog([valid, second with { Id = valid.Id }]),
            "duplicate-name" => new RulePresetCatalog([valid, second with { Name = "VALID" }]),
            "untrimmed" => new RulePresetCatalog([valid, second with { Name = " Second " }]),
            _ => new RulePresetCatalog([valid, second with
            {
                Rules = Rules with { Subtitles = new(LanguageRuleMode.KeepMatching, []) }
            }])
        };

        var invalidCatalog = RulePresetCatalogPolicy.Validate(catalog);
        var attempted = RulePresetCatalogPolicy.Apply(catalog, new(RulePresetMutationKind.Delete, valid.Id));

        Assert.Equal(RulePresetErrorCode.InvalidCatalog, invalidCatalog!.Code);
        Assert.Equal(RulePresetErrorCode.InvalidCatalog, attempted.Failure!.Code);
        Assert.Null(attempted.Catalog);
        Assert.False(attempted.Committed);
        Assert.Equal("Valid", valid.Name);
    }

    /// <summary>Bounds inactive fields structurally without interpreting their unused language or title syntax.</summary>
    [Theory]
    [InlineData("count", RulePresetErrorCode.LimitExceeded)]
    [InlineData("language-length", RulePresetErrorCode.LimitExceeded)]
    [InlineData("title-length", RulePresetErrorCode.LimitExceeded)]
    [InlineData("default-array", RulePresetErrorCode.InvalidConfiguration)]
    [InlineData("null-language", RulePresetErrorCode.InvalidConfiguration)]
    [InlineData("null-title", RulePresetErrorCode.InvalidConfiguration)]
    [InlineData("unknown-mode", RulePresetErrorCode.InvalidConfiguration)]
    public void InactiveConfigurationStillRequiresBoundedCompleteShape(string invalid, RulePresetErrorCode expected)
    {
        var inactive = Rules.Audio;
        var invalidCategory = invalid switch
        {
            "count" => inactive with { Languages = Enumerable.Repeat("arbitrary", RulePresetLimits.MaximumLanguageValues + 1).ToImmutableArray() },
            "language-length" => inactive with { Languages = [new string('x', RulePresetLimits.MaximumLanguageLength + 1)] },
            "title-length" => inactive with { TitleFilter = new(false, new string('x', RulePresetLimits.MaximumTitleLength + 1)) },
            "default-array" => inactive with { Languages = default },
            "null-language" => inactive with { Languages = [null!] },
            "null-title" => inactive with { TitleFilter = new(false, null!) },
            _ => inactive with { Mode = (LanguageRuleMode)int.MaxValue }
        };

        var failure = RulePresetCatalogPolicy.ValidateConfiguration(Rules with { Audio = invalidCategory });

        Assert.Equal(expected, failure!.Code);
    }

    /// <summary>Accepts exactly bounded inactive values verbatim while still rejecting invalid active matching settings.</summary>
    [Fact]
    public void LimitsAreInclusiveAndActiveConfigurationUsesCoreIssues()
    {
        var text = new string('x', RulePresetLimits.MaximumTitleLength);
        var values = Enumerable.Repeat(new string('x', RulePresetLimits.MaximumLanguageLength),
            RulePresetLimits.MaximumLanguageValues).ToImmutableArray();
        var bounded = Rules with { Audio = new(LanguageRuleMode.Unchanged, values, new(false, text)) };
        var name = new string('N', RulePresetLimits.MaximumNameLength);
        var applied = RulePresetCatalogPolicy.Apply(new([]), new(RulePresetMutationKind.Add, Guid.NewGuid(), name, bounded));

        Assert.Null(applied.Failure);
        Assert.Same(bounded, Assert.Single(applied.Catalog!.Items).Rules);
        Assert.Null(RulePresetCatalogPolicy.ValidateName(name));
        Assert.Equal(RulePresetErrorCode.InvalidName, RulePresetCatalogPolicy.ValidateName(name + "N")!.Code);
        var invalidActive = Rules with { Subtitles = new(LanguageRuleMode.KeepMatching, ["qaa"]) };
        var failure = RulePresetCatalogPolicy.ValidateConfiguration(invalidActive)!;
        Assert.Equal(RulePresetErrorCode.InvalidConfiguration, failure.Code);
        Assert.Equal(LanguageRuleIssueReason.InvalidRuleLanguage, Assert.Single(failure.RuleIssues).Reason);
    }

    /// <summary>Rejects an overflowing add atomically while permitting rename and delete in a full catalog.</summary>
    [Fact]
    public void FullCatalogCanBeEditedWithoutTruncatingOrAddingAnOverflowingSubset()
    {
        var catalog = new RulePresetCatalog(Enumerable.Range(0, RulePresetLimits.MaximumEntries)
            .Select(index => new RulePreset(Guid.NewGuid(), $"Preset {index}", Rules)).ToImmutableArray());

        var rejected = RulePresetCatalogPolicy.Apply(catalog, new(RulePresetMutationKind.Add, Guid.NewGuid(), "Overflow", Rules));
        var renamed = RulePresetCatalogPolicy.Apply(catalog, new(RulePresetMutationKind.Rename, catalog.Items[0].Id, "Renamed"));
        var deleted = RulePresetCatalogPolicy.Apply(catalog, new(RulePresetMutationKind.Delete, catalog.Items[0].Id));

        Assert.Equal(RulePresetErrorCode.LimitExceeded, rejected.Failure!.Code);
        Assert.Null(rejected.Catalog);
        Assert.Equal(RulePresetLimits.MaximumEntries, catalog.Items.Length);
        Assert.Null(renamed.Failure);
        Assert.Equal(RulePresetLimits.MaximumEntries, renamed.Catalog!.Items.Length);
        Assert.Null(deleted.Failure);
        Assert.Equal(RulePresetLimits.MaximumEntries - 1, deleted.Catalog!.Items.Length);
    }
}
