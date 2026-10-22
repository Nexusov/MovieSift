using TrackTrim.Core.Rules;

namespace TrackTrim.Application.Presets;

/// <summary>Validates complete catalogs and applies one stable-identity intent without persistence or shared mutable state.</summary>
public static class RulePresetCatalogPolicy
{
    /// <summary>Rejects an invalid entry or identity/name collision without returning a partially usable catalog.</summary>
    public static RulePresetFailure? Validate(RulePresetCatalog? catalog)
    {
        if (catalog is null || catalog.Items.IsDefault)
        {
            return new(RulePresetErrorCode.InvalidCatalog, "The catalog or its item array is missing.");
        }

        if (catalog.Items.Length > RulePresetLimits.MaximumEntries)
        {
            return new(RulePresetErrorCode.LimitExceeded, "The preset count exceeds the catalog limit.");
        }

        var identifiers = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var preset in catalog.Items)
        {
            if (preset is null || preset.Id == Guid.Empty)
            {
                return new(RulePresetErrorCode.InvalidCatalog, "A preset or its stable identity is missing.");
            }

            var nameFailure = ValidateName(preset.Name);

            if (nameFailure is not null)
            {
                return nameFailure with { Code = RulePresetErrorCode.InvalidCatalog };
            }

            if (!string.Equals(preset.Name, preset.Name.Trim(), StringComparison.Ordinal))
            {
                return new(RulePresetErrorCode.InvalidCatalog, "A stored preset name is not trimmed.");
            }

            if (!identifiers.Add(preset.Id) || !names.Add(preset.Name))
            {
                return new(RulePresetErrorCode.InvalidCatalog, "The catalog contains a duplicate identity or name.");
            }

            var configurationFailure = ValidateConfiguration(preset.Rules);

            if (configurationFailure is not null)
            {
                return configurationFailure.Code == RulePresetErrorCode.LimitExceeded
                    ? configurationFailure : configurationFailure with { Code = RulePresetErrorCode.InvalidCatalog };
            }
        }

        return null;
    }

    /// <summary>Applies a validated intent to the catalog freshly read by the store while preserving unrelated entries.</summary>
    public static RulePresetCatalogResult Apply(RulePresetCatalog? catalog, RulePresetMutation mutation)
    {
        var intentFailure = ValidateMutation(mutation);

        if (intentFailure is not null)
        {
            return new(null, intentFailure);
        }

        var catalogFailure = Validate(catalog);

        if (catalogFailure is not null)
        {
            return new(null, catalogFailure);
        }

        var items = catalog!.Items;

        if (mutation.Kind == RulePresetMutationKind.Add)
        {
            return Add(new(catalog, mutation));
        }

        var existing = items.FirstOrDefault(preset => preset.Id == mutation.Id);

        if (existing is null)
        {
            return new(null, new(RulePresetErrorCode.NotFound));
        }

        if (mutation.Kind == RulePresetMutationKind.Delete)
        {
            return new(new(items.Remove(existing)), AffectedPresetId: existing.Id);
        }

        var name = mutation.Name.Trim();

        if (items.Any(preset => preset.Id != existing.Id && string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return new(null, new(RulePresetErrorCode.DuplicateName));
        }

        if (string.Equals(existing.Name, name, StringComparison.Ordinal))
        {
            return new(catalog, AffectedPresetId: existing.Id);
        }

        return new(new(items.Replace(existing, existing with { Name = name })), AffectedPresetId: existing.Id);
    }

    /// <summary>Accepts a trimmed bounded single-line name without folding its display spelling.</summary>
    public static RulePresetFailure? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new(RulePresetErrorCode.InvalidName);
        }

        var trimmed = name.Trim();
        var invalid = trimmed.Length > RulePresetLimits.MaximumNameLength || name.Any(char.IsControl);

        if (invalid)
        {
            return new(RulePresetErrorCode.InvalidName);
        }

        return null;
    }

    /// <summary>Bounds all stored fields, including inactive text, then invokes the shared active Core validation.</summary>
    public static RulePresetFailure? ValidateConfiguration(LanguageRules? rules)
    {
        if (rules is null)
        {
            return new(RulePresetErrorCode.InvalidConfiguration, "The rule configuration is missing.");
        }

        var audioFailure = ValidateCategoryShape(rules.Audio);

        if (audioFailure is not null)
        {
            return audioFailure;
        }

        var subtitleFailure = ValidateCategoryShape(rules.Subtitles);

        if (subtitleFailure is not null)
        {
            return subtitleFailure;
        }

        var issues = LanguageRuleConfiguration.Validate(rules);
        return issues.IsEmpty ? null : new(RulePresetErrorCode.InvalidConfiguration, RuleIssues: issues);
    }

    /// <summary>Validates an intent before storage access without relying on a cached catalog for uniqueness.</summary>
    internal static RulePresetFailure? ValidateMutation(RulePresetMutation? mutation)
    {
        if (mutation is null)
        {
            return new(RulePresetErrorCode.InvalidCatalog, "The preset mutation is missing.");
        }

        if (mutation.CancellationToken.IsCancellationRequested)
        {
            return new(RulePresetErrorCode.Cancelled);
        }

        if (!Enum.IsDefined(mutation.Kind) || mutation.Id == Guid.Empty)
        {
            return new(RulePresetErrorCode.InvalidCatalog, "The mutation kind or stable identity is invalid.");
        }

        if (mutation.Kind == RulePresetMutationKind.Delete)
        {
            return null;
        }

        var nameFailure = ValidateName(mutation.Name);

        if (nameFailure is not null)
        {
            return nameFailure;
        }

        return mutation.Kind == RulePresetMutationKind.Add ? ValidateConfiguration(mutation.Rules) : null;
    }

    /// <summary>Checks bounded shape even where matching intentionally ignores inactive configuration values.</summary>
    private static RulePresetFailure? ValidateCategoryShape(TrackLanguageRule? rule)
    {
        if (rule is null || !Enum.IsDefined(rule.Mode) || rule.Languages.IsDefault)
        {
            return new(RulePresetErrorCode.InvalidConfiguration, "A category, mode or language array is invalid.");
        }

        if (rule.Languages.Length > RulePresetLimits.MaximumLanguageValues)
        {
            return new(RulePresetErrorCode.LimitExceeded, "The language value count exceeds the preset limit.");
        }

        if (rule.Languages.Any(value => value is null) || rule.TitleFilter is { Text: null })
        {
            return new(RulePresetErrorCode.InvalidConfiguration, "A stored language or title string is null.");
        }

        var languageTooLong = rule.Languages.Any(value => value.Length > RulePresetLimits.MaximumLanguageLength);
        var titleTooLong = rule.TitleFilter is { } title && title.Text.Length > RulePresetLimits.MaximumTitleLength;

        if (languageTooLong || titleTooLong)
        {
            return new(RulePresetErrorCode.LimitExceeded, "A stored language or title string exceeds the preset limit.");
        }

        return null;
    }

    /// <summary>Appends one fresh identity while retaining the exact immutable rules and all unrelated catalog rows.</summary>
    private static RulePresetCatalogResult Add(RulePresetAddition request)
    {
        var items = request.Catalog.Items;

        if (items.Length == RulePresetLimits.MaximumEntries)
        {
            return new(null, new(RulePresetErrorCode.LimitExceeded, "The preset catalog is full."));
        }

        if (items.Any(preset => preset.Id == request.Mutation.Id))
        {
            return new(null, new(RulePresetErrorCode.InvalidCatalog, "The added preset identity already exists."));
        }

        var name = request.Mutation.Name.Trim();

        if (items.Any(preset => string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return new(null, new(RulePresetErrorCode.DuplicateName));
        }

        var added = new RulePreset(request.Mutation.Id, name, request.Mutation.Rules!);
        return new(new(items.Add(added)), AffectedPresetId: added.Id);
    }
}
