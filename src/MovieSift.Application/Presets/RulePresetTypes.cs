using System.Collections.Immutable;
using MovieSift.Core.Rules;

namespace MovieSift.Application.Presets;

public enum RulePresetErrorCode
{
    Busy, InvalidName, DuplicateName, InvalidConfiguration, InvalidCatalog, UnsupportedVersion,
    LimitExceeded, NotFound, PersistenceUnavailable, Cancelled
}

public enum RulePresetMutationKind { Add, Rename, Delete }

public sealed record RulePreset(Guid Id, string Name, LanguageRules Rules);
public sealed record RulePresetCatalog(ImmutableArray<RulePreset> Items);
public sealed record RulePresetFailure(
    RulePresetErrorCode Code,
    string Detail = "",
    ImmutableArray<LanguageRuleIssue> RuleIssues = default);
public sealed record RulePresetCatalogResult(
    RulePresetCatalog? Catalog,
    RulePresetFailure? Failure = null,
    bool Committed = false,
    ImmutableArray<RulePresetFailure> CleanupIssues = default,
    Guid? AffectedPresetId = null);
public sealed record RulePresetLoadResult(RulePreset? Preset, RulePresetFailure? Failure = null);

public sealed record RulePresetMutation(
    RulePresetMutationKind Kind,
    Guid Id,
    string Name = "",
    LanguageRules? Rules = null,
    CancellationToken CancellationToken = default);
public sealed record SaveRulePresetRequest(string Name, LanguageRules Rules, CancellationToken CancellationToken = default);
public sealed record LoadRulePresetRequest(Guid Id, CancellationToken CancellationToken = default);
public sealed record RenameRulePresetRequest(Guid Id, string Name, CancellationToken CancellationToken = default);
public sealed record DeleteRulePresetRequest(Guid Id, CancellationToken CancellationToken = default);

internal sealed record RulePresetAddition(RulePresetCatalog Catalog, RulePresetMutation Mutation);
