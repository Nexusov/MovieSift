using MovieSift.Application.Presets;
using MovieSift.Core.Rules;
using Xunit;

namespace MovieSift.Application.Tests.Presets;

/// <summary>Checks explicit preset commands, fresh reads, cancellation and preservation of commit evidence.</summary>
public sealed class RulePresetServiceTests
{
    private const int TestTimeoutSeconds = 10;
    private static LanguageRules Rules => new(
        new(LanguageRuleMode.Unchanged, ["fra", "deu"], new(false, "  Dormant [literal].*  ")),
        new(LanguageRuleMode.KeepMatching, ["eng"], new(true, "  Captions  ")), true);

    /// <summary>Sends stable-identity intents without overwriting another instance's intervening catalog additions.</summary>
    [Fact]
    public async Task SaveRenameAndDeletePreserveTheStoresLatestUnrelatedEntries()
    {
        var original = new RulePreset(Guid.NewGuid(), "Original", Rules);
        var store = new RulePresetTestStore { Catalog = new([original]) };
        var service = new RulePresetService(store);
        var captured = Rules;

        var saved = await service.SaveAsNewAsync(new("  Saved  ", captured, TestContext.Current.CancellationToken));

        Assert.True(saved.Committed);
        Assert.Null(saved.Failure);
        var identifier = Assert.IsType<Guid>(saved.AffectedPresetId);
        Assert.NotEqual(Guid.Empty, identifier);
        var preset = Assert.Single(saved.Catalog!.Items, item => item.Id == identifier);
        Assert.Equal("Saved", preset.Name);
        Assert.Same(captured, preset.Rules);
        Assert.Same(original, saved.Catalog.Items[0]);
        var otherInstance = new RulePreset(Guid.NewGuid(), "Other instance", Rules);
        store.Catalog = new(store.Catalog.Items.Add(otherInstance));

        var renamed = await service.RenameAsync(new(identifier, " Renamed ", TestContext.Current.CancellationToken));

        Assert.True(renamed.Committed);
        Assert.Equal("Renamed", Assert.Single(renamed.Catalog!.Items, item => item.Id == identifier).Name);
        Assert.Contains(otherInstance, renamed.Catalog.Items);
        Assert.Same(captured, Assert.Single(renamed.Catalog.Items, item => item.Id == identifier).Rules);
        var loaded = await service.LoadAsync(new(identifier, TestContext.Current.CancellationToken));
        Assert.Null(loaded.Failure);
        Assert.Equal("Renamed", loaded.Preset!.Name);
        Assert.Same(captured, loaded.Preset.Rules);

        var deleted = await service.DeleteAsync(new(identifier, TestContext.Current.CancellationToken));

        Assert.True(deleted.Committed);
        Assert.Equal([original, otherInstance], deleted.Catalog!.Items);
        Assert.Equal("Renamed", loaded.Preset.Name);
        Assert.Same(captured, loaded.Preset.Rules);
        Assert.Equal([RulePresetMutationKind.Add, RulePresetMutationKind.Rename, RulePresetMutationKind.Delete],
            store.Mutations.Select(mutation => mutation.Kind));
        Assert.All(store.Mutations, mutation => Assert.Equal(identifier, mutation.Id));
        Assert.Null(store.Mutations[1].Rules);
        Assert.Null(store.Mutations[2].Rules);
        Assert.Equal(1, store.ReadCalls);
    }

    /// <summary>Re-reads before each load and rejects the target too when an unrelated catalog entry is invalid.</summary>
    [Fact]
    public async Task LoadUsesFreshCompleteCatalogAndNeverAcceptsAValidSubset()
    {
        var preset = new RulePreset(Guid.NewGuid(), "Requested", Rules);
        var store = new RulePresetTestStore { Catalog = new([preset]) };
        var service = new RulePresetService(store);
        var first = await service.LoadAsync(new(preset.Id, TestContext.Current.CancellationToken));
        Assert.Same(preset, first.Preset);
        var replacement = preset with { Rules = preset.Rules with { KeepOnlyForcedSubtitles = false } };
        store.Catalog = new([replacement]);

        var fresh = await service.LoadAsync(new(preset.Id, TestContext.Current.CancellationToken));

        Assert.Same(replacement, fresh.Preset);
        Assert.False(fresh.Preset!.Rules.KeepOnlyForcedSubtitles);
        var invalid = new RulePreset(Guid.NewGuid(), "Invalid sibling", Rules with
        {
            Subtitles = new(LanguageRuleMode.KeepMatching, [])
        });
        store.Catalog = new([replacement, invalid]);

        var rejected = await service.LoadAsync(new(preset.Id, TestContext.Current.CancellationToken));

        Assert.Null(rejected.Preset);
        Assert.Equal(RulePresetErrorCode.InvalidCatalog, rejected.Failure!.Code);
        Assert.Equal(3, store.ReadCalls);
        Assert.Equal(0, store.ChangeCalls);
        Assert.Equal(2, store.Catalog.Items.Length);
    }

    /// <summary>Rejects invalid save snapshots before contacting the storage boundary.</summary>
    [Theory]
    [InlineData("name", RulePresetErrorCode.InvalidName)]
    [InlineData("active", RulePresetErrorCode.InvalidConfiguration)]
    [InlineData("missing", RulePresetErrorCode.InvalidConfiguration)]
    [InlineData("inactive-bound", RulePresetErrorCode.LimitExceeded)]
    public async Task InvalidSaveDoesNotReadOrChangeTheStore(string invalid, RulePresetErrorCode expected)
    {
        var store = new RulePresetTestStore();
        var before = store.Catalog;
        var service = new RulePresetService(store);
        var rules = invalid switch
        {
            "active" => Rules with { Subtitles = new(LanguageRuleMode.KeepMatching, []) },
            "missing" => null!,
            "inactive-bound" => Rules with
            {
                Audio = Rules.Audio with { TitleFilter = new(false, new string('x', RulePresetLimits.MaximumTitleLength + 1)) }
            },
            _ => Rules
        };
        var name = invalid == "name" ? " " : "Valid";

        var result = await service.SaveAsNewAsync(new(name, rules, TestContext.Current.CancellationToken));

        Assert.Equal(expected, result.Failure!.Code);
        Assert.False(result.Committed);
        Assert.Same(before, store.Catalog);
        Assert.Equal(0, store.ReadCalls);
        Assert.Equal(0, store.ChangeCalls);
    }

    /// <summary>Rejects all concurrent commands nonblockingly while a controlled catalog read owns the service.</summary>
    [Fact]
    public async Task ActiveReadRejectsConcurrentCrudAndLoadWithoutAdditionalIo()
    {
        var entered = Signal();
        var release = Signal();
        var store = new RulePresetTestStore
        {
            OnRead = async cancellationToken =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
        };
        var service = new RulePresetService(store);
        var active = service.ReadAsync(TestContext.Current.CancellationToken);

        try
        {
            await Await(entered.Task);
            var identifier = Guid.NewGuid();
            Assert.Equal(RulePresetErrorCode.Busy, (await service.ReadAsync(TestContext.Current.CancellationToken)).Failure!.Code);
            Assert.Equal(RulePresetErrorCode.Busy, (await service.LoadAsync(new(identifier, TestContext.Current.CancellationToken))).Failure!.Code);
            Assert.Equal(RulePresetErrorCode.Busy, (await service.SaveAsNewAsync(new("Valid", Rules, TestContext.Current.CancellationToken))).Failure!.Code);
            Assert.Equal(RulePresetErrorCode.Busy, (await service.RenameAsync(new(identifier, "Valid", TestContext.Current.CancellationToken))).Failure!.Code);
            Assert.Equal(RulePresetErrorCode.Busy, (await service.DeleteAsync(new(identifier, TestContext.Current.CancellationToken))).Failure!.Code);
            Assert.Equal(1, store.ReadCalls);
            Assert.Equal(0, store.ChangeCalls);
        }
        finally
        {
            release.TrySetResult();
            await Await(active);
        }
    }

    /// <summary>Handles already-cancelled commands before any read or mutation crosses the boundary.</summary>
    [Fact]
    public async Task CancelledCommandsDoNotTouchTheStore()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var store = new RulePresetTestStore();
        var service = new RulePresetService(store);
        var identifier = Guid.NewGuid();

        Assert.Equal(RulePresetErrorCode.Cancelled, (await service.ReadAsync(cancellation.Token)).Failure!.Code);
        Assert.Equal(RulePresetErrorCode.Cancelled, (await service.LoadAsync(new(identifier, cancellation.Token))).Failure!.Code);
        Assert.Equal(RulePresetErrorCode.Cancelled, (await service.SaveAsNewAsync(new("Valid", Rules, cancellation.Token))).Failure!.Code);
        Assert.Equal(RulePresetErrorCode.Cancelled, (await service.RenameAsync(new(identifier, "Valid", cancellation.Token))).Failure!.Code);
        Assert.Equal(RulePresetErrorCode.Cancelled, (await service.DeleteAsync(new(identifier, cancellation.Token))).Failure!.Code);
        Assert.Equal(0, store.ReadCalls);
        Assert.Equal(0, store.ChangeCalls);
    }

    /// <summary>Observes cancellation before a gated write without committing any proposed catalog.</summary>
    [Fact]
    public async Task CancellationBeforeCommitPreservesExactCatalog()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var entered = Signal();
        var store = new RulePresetTestStore
        {
            OnChange = async mutation =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, mutation.CancellationToken);
            }
        };
        var before = store.Catalog;
        var service = new RulePresetService(store);
        var operation = service.SaveAsNewAsync(new("Valid", Rules, cancellation.Token));
        await Await(entered.Task);
        cancellation.Cancel();

        var result = await operation;

        Assert.Equal(RulePresetErrorCode.Cancelled, result.Failure!.Code);
        Assert.False(result.Committed);
        Assert.Same(before, store.Catalog);
        Assert.Empty(store.Catalog.Items);
    }

    /// <summary>Does not relabel a completed catalog replacement as cancellation or discard its independent cleanup warning.</summary>
    [Fact]
    public async Task LateCancellationRetainsCommittedCatalogIdentityAndCleanupEvidence()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var cleanup = new RulePresetFailure(RulePresetErrorCode.PersistenceUnavailable, "Controlled temporary cleanup issue.");
        var store = new RulePresetTestStore { OnCommitted = cancellation.Cancel, CleanupIssues = [cleanup] };
        var service = new RulePresetService(store);

        var result = await service.SaveAsNewAsync(new("Saved", Rules, cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(result.Committed);
        Assert.Null(result.Failure);
        Assert.Same(store.Catalog, result.Catalog);
        Assert.Equal(Assert.Single(store.Catalog.Items).Id, result.AffectedPresetId);
        Assert.Same(cleanup, Assert.Single(result.CleanupIssues));
        Assert.Equal(1, store.ChangeCalls);
        Assert.Equal(0, store.ReadCalls);
    }

    /// <summary>Keeps explicit storage failures distinct from a usable empty catalog and never loads a stale preset.</summary>
    [Fact]
    public async Task UnsupportedCatalogResultRemainsAnExplicitReadAndLoadFailure()
    {
        var failure = new RulePresetFailure(RulePresetErrorCode.UnsupportedVersion, "Controlled future version.");
        var store = new RulePresetTestStore { ReadFailure = failure };
        var service = new RulePresetService(store);

        var read = await service.ReadAsync(TestContext.Current.CancellationToken);
        var loaded = await service.LoadAsync(new(Guid.NewGuid(), TestContext.Current.CancellationToken));

        Assert.Null(read.Catalog);
        Assert.Same(failure, read.Failure);
        Assert.Null(loaded.Preset);
        Assert.Same(failure, loaded.Failure);
        Assert.Equal(2, store.ReadCalls);
        Assert.Equal(0, store.ChangeCalls);
    }

    /// <summary>Converts expected storage exceptions while releasing the local operation gate for later commands.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpectedReadAndWriteExceptionsBecomePersistenceFailures(bool write)
    {
        var store = new RulePresetTestStore();
        var exception = new IOException("Controlled I/O failure.");

        if (write)
        {
            store.ChangeException = exception;
        }

        if (!write)
        {
            store.ReadException = exception;
        }

        var service = new RulePresetService(store);
        var result = write
            ? await service.SaveAsNewAsync(new("Valid", Rules, TestContext.Current.CancellationToken))
            : await service.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(RulePresetErrorCode.PersistenceUnavailable, result.Failure!.Code);
        Assert.Equal(exception.Message, result.Failure.Detail);
        Assert.Null(result.Catalog);
        Assert.False(result.Committed);
        store.ChangeException = null;
        store.ReadException = null;
        Assert.Null((await service.ReadAsync(TestContext.Current.CancellationToken)).Failure);
    }

    /// <summary>Does not disguise programming faults as persistence failures and still releases the service gate.</summary>
    [Fact]
    public async Task ProgrammingFailurePropagatesWithoutPoisoningTheOperationGate()
    {
        var store = new RulePresetTestStore { ChangeException = new InvalidOperationException("Controlled programming fault.") };
        var service = new RulePresetService(store);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsNewAsync(new("Valid", Rules, TestContext.Current.CancellationToken)));

        store.ChangeException = null;
        Assert.Null((await service.ReadAsync(TestContext.Current.CancellationToken)).Failure);
        Assert.Empty(store.Catalog.Items);
    }

    /// <summary>Creates a deterministic asynchronous signal without relying on scheduler timing.</summary>
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Bounds a failed synchronization gate with the test runner's cancellation token.</summary>
    private static Task Await(Task task) => task.WaitAsync(TimeSpan.FromSeconds(TestTimeoutSeconds), TestContext.Current.CancellationToken);
}
