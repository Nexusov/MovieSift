using MovieSift.Application.Ports;

namespace MovieSift.Application.Presets;

/// <summary>Coordinates explicit preset commands without owning media state, selecting a preset, or caching catalog writes.</summary>
public sealed class RulePresetService(IRulePresetStore store)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>Reads and validates the complete current catalog without selecting or loading any configuration.</summary>
    public async Task<RulePresetCatalogResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        var failure = TryEnter(cancellationToken);

        if (failure is not null)
        {
            return new(null, failure);
        }

        try
        {
            return await ReadValidatedAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Saves an immutable configuration under a fresh identity through a locked store mutation intent.</summary>
    public Task<RulePresetCatalogResult> SaveAsNewAsync(SaveRulePresetRequest request) =>
        ChangeAsync(new(RulePresetMutationKind.Add, Guid.NewGuid(), request.Name, request.Rules, request.CancellationToken));

    /// <summary>Loads only the requested identity from a fresh complete catalog and leaves presentation choices to its caller.</summary>
    public async Task<RulePresetLoadResult> LoadAsync(LoadRulePresetRequest request)
    {
        var failure = TryEnter(request.CancellationToken);

        if (failure is not null)
        {
            return new(null, failure);
        }

        try
        {
            var read = await ReadValidatedAsync(request.CancellationToken);

            if (read.Failure is not null)
            {
                return new(null, read.Failure);
            }

            var preset = read.Catalog!.Items.FirstOrDefault(item => item.Id == request.Id);
            return preset is null ? new(null, new(RulePresetErrorCode.NotFound)) : new(preset);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Renames one stable identity without replacing its rule configuration or unrelated catalog entries.</summary>
    public Task<RulePresetCatalogResult> RenameAsync(RenameRulePresetRequest request) =>
        ChangeAsync(new(RulePresetMutationKind.Rename, request.Id, request.Name, CancellationToken: request.CancellationToken));

    /// <summary>Deletes only the named catalog identity without performing media or ownership-record cleanup.</summary>
    public Task<RulePresetCatalogResult> DeleteAsync(DeleteRulePresetRequest request) =>
        ChangeAsync(new(RulePresetMutationKind.Delete, request.Id, CancellationToken: request.CancellationToken));

    /// <summary>Serializes commands locally while requiring the store to re-read and apply the intent under its interprocess lock.</summary>
    private async Task<RulePresetCatalogResult> ChangeAsync(RulePresetMutation mutation)
    {
        var failure = TryEnter(mutation.CancellationToken);

        if (failure is not null)
        {
            return new(null, failure);
        }

        try
        {
            var invalid = RulePresetCatalogPolicy.ValidateMutation(mutation);

            if (invalid is not null)
            {
                return new(null, invalid);
            }

            var result = ValidateStoreResult(await store.ChangeAsync(mutation));
            var accepted = result.Committed || result.Failure is null && result.Catalog is not null;
            return accepted ? result with { AffectedPresetId = result.AffectedPresetId ?? mutation.Id } : result;
        }
        catch (OperationCanceledException)
        {
            return new(null, new(RulePresetErrorCode.Cancelled));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new(null, new(RulePresetErrorCode.PersistenceUnavailable, exception.Message));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Converts storage failures into explicit outcomes and never exposes an unvalidated successful catalog.</summary>
    private async Task<RulePresetCatalogResult> ReadValidatedAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await store.ReadAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return ValidateStoreResult(result);
        }
        catch (OperationCanceledException)
        {
            return new(null, new(RulePresetErrorCode.Cancelled));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new(null, new(RulePresetErrorCode.PersistenceUnavailable, exception.Message));
        }
    }

    /// <summary>Retains committed and cleanup facts independently from a malformed response or primary persistence failure.</summary>
    private static RulePresetCatalogResult ValidateStoreResult(RulePresetCatalogResult? result)
    {
        if (result is null)
        {
            return new(null, new(RulePresetErrorCode.InvalidCatalog, "The store returned no outcome."));
        }

        if (result.Catalog is null && result.Failure is not null)
        {
            return result;
        }

        var failure = RulePresetCatalogPolicy.Validate(result.Catalog);
        return failure is null ? result : result with { Catalog = null, Failure = result.Failure ?? failure };
    }

    /// <summary>Rejects cancelled or concurrent commands without waiting on an active preset operation.</summary>
    private RulePresetFailure? TryEnter(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new(RulePresetErrorCode.Cancelled);
        }

        if (!gate.Wait(0))
        {
            return new(RulePresetErrorCode.Busy);
        }

        return null;
    }
}
