using TrackTrim.Application.Ports;
using TrackTrim.Application.Presets;

namespace TrackTrim.Desktop.Tests;

/// <summary>Controls persistence completion independently of the actual headless editor and batch owner.</summary>
internal sealed class BatchUiPresetStore : IRulePresetStore
{
    public RulePresetCatalog Catalog { get; set; } = new([]);
    public RulePresetFailure? ReadFailure { get; set; }
    public Func<Task>? OnRead { get; set; }
    public int WriteCount { get; private set; }

    /// <summary>Provides a deterministic delayed-read boundary for stale-load tests.</summary>
    public async Task<RulePresetCatalogResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (OnRead is not null)
        {
            await OnRead();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ReadFailure is null ? new(Catalog) : new(null, ReadFailure);
    }

    /// <summary>Uses the production catalog policy while replacing only the synthetic persistence side.</summary>
    public Task<RulePresetCatalogResult> ChangeAsync(RulePresetMutation request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        var result = RulePresetCatalogPolicy.Apply(Catalog, request);

        if (result.Catalog is { } catalog && result.Failure is null)
        {
            Catalog = catalog;
            WriteCount++;
            result = result with { Committed = true };
        }

        return Task.FromResult(result);
    }
}
