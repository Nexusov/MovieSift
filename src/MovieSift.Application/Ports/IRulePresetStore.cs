using MovieSift.Application.Presets;

namespace MovieSift.Application.Ports;

public interface IRulePresetStore
{
    Task<RulePresetCatalogResult> ReadAsync(CancellationToken cancellationToken = default);
    Task<RulePresetCatalogResult> ChangeAsync(RulePresetMutation mutation);
}
