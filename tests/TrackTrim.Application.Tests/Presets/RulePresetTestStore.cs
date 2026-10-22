using System.Collections.Immutable;
using TrackTrim.Application.Ports;
using TrackTrim.Application.Presets;

namespace TrackTrim.Application.Tests.Presets;

/// <summary>Models store intent application against its latest catalog without claiming filesystem durability.</summary>
internal sealed class RulePresetTestStore : IRulePresetStore
{
    public RulePresetCatalog Catalog { get; set; } = new([]);
    public int ReadCalls { get; private set; }
    public int ChangeCalls { get; private set; }
    public List<RulePresetMutation> Mutations { get; } = [];
    public Func<CancellationToken, Task>? OnRead { get; set; }
    public Func<RulePresetMutation, Task>? OnChange { get; set; }
    public Action? OnCommitted { get; set; }
    public RulePresetFailure? ReadFailure { get; set; }
    public RulePresetFailure? ChangeFailure { get; set; }
    public Exception? ReadException { get; set; }
    public Exception? ChangeException { get; set; }
    public ImmutableArray<RulePresetFailure> CleanupIssues { get; set; } = [];

    /// <summary>Returns the current complete catalog after an optional deterministic operation gate.</summary>
    public async Task<RulePresetCatalogResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        ReadCalls++;

        if (OnRead is not null)
        {
            await OnRead(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (ReadException is not null)
        {
            throw ReadException;
        }

        return ReadFailure is { } failure ? new(null, failure) : new(Catalog);
    }

    /// <summary>Applies one intent to the latest state and preserves a committed fact across late cancellation.</summary>
    public async Task<RulePresetCatalogResult> ChangeAsync(RulePresetMutation mutation)
    {
        ChangeCalls++;
        Mutations.Add(mutation);

        if (OnChange is not null)
        {
            await OnChange(mutation);
        }

        mutation.CancellationToken.ThrowIfCancellationRequested();

        if (ChangeException is not null)
        {
            throw ChangeException;
        }

        if (ChangeFailure is { } failure)
        {
            return new(null, failure);
        }

        var applied = RulePresetCatalogPolicy.Apply(Catalog, mutation);

        if (applied.Failure is not null)
        {
            return applied;
        }

        var committed = !ReferenceEquals(Catalog, applied.Catalog);
        Catalog = applied.Catalog!;

        if (committed)
        {
            OnCommitted?.Invoke();
        }

        return applied with { Committed = committed, CleanupIssues = CleanupIssues };
    }
}
