using TrackTrim.Application.Presets;

namespace TrackTrim.Application.Ports;

public interface IRulePresetStore
{
    Task<RulePresetCatalogResult> ReadAsync(CancellationToken cancellationToken = default);
    Task<RulePresetCatalogResult> ChangeAsync(RulePresetMutation mutation);
}
