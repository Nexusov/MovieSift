using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using TrackTrim.Application.Presets;
using TrackTrim.Core.Rules;
using TrackTrim.Infrastructure.Persistence;
using Xunit;

namespace TrackTrim.Infrastructure.Tests;

/// <summary>Exercises the actual portable catalog boundary independently from media and UI workflows.</summary>
public sealed class PortableRulePresetStoreTests
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(20);
    private static readonly Guid PresetId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");

    /// <summary>A missing catalog is empty without creating portable state during an explicit read.</summary>
    [Fact]
    public async Task MissingCatalogReturnsEmptyWithoutCreatingDataDirectory()
    {
        using var workspace = new TestWorkspace();
        var dataPath = Path.Combine(workspace.Path, "data");
        var result = await new PortableRulePresetStore(dataPath).ReadAsync(TestContext.Current.CancellationToken);
        Assert.Null(result.Failure);
        Assert.Empty(Assert.IsType<RulePresetCatalog>(result.Catalog).Items);
        Assert.False(result.Committed);
        Assert.False(Directory.Exists(dataPath));
    }

    /// <summary>Round-trips raw aliases, order, inactive fields, title text and forced configuration through a fresh instance.</summary>
    [Fact]
    public async Task FreshStoreRoundTripsEveryConfigurationFieldWithoutNormalization()
    {
        using var workspace = new TestWorkspace();
        var original = Rules();
        var saved = await Store(workspace).ChangeAsync(new(RulePresetMutationKind.Add, PresetId, "  Русский preset  ",
            original, TestContext.Current.CancellationToken));
        Assert.Null(saved.Failure);
        Assert.True(saved.Committed);
        Assert.Equal(PresetId, saved.AffectedPresetId);
        var loaded = await Store(workspace).ReadAsync(TestContext.Current.CancellationToken);
        var preset = Assert.Single(Assert.IsType<RulePresetCatalog>(loaded.Catalog).Items);
        Assert.Equal(PresetId, preset.Id);
        Assert.Equal("Русский preset", preset.Name);
        AssertRulesEqual(original, preset.Rules);
        Assert.NotSame(original, preset.Rules);
        Assert.True(File.Exists(Path.Combine(workspace.Path, PortableRulePresetStore.LockFileName)));
        AssertNoTemporaryFiles(workspace.Path);
    }

    /// <summary>The optional in-memory default is stored as required enabled/text fields with identical configuration content.</summary>
    [Fact]
    public async Task OptionalModelFiltersAreWrittenAsExplicitDisabledEmptySchemaValues()
    {
        using var workspace = new TestWorkspace();
        var rules = new LanguageRules(new(LanguageRuleMode.Unchanged, []), new(LanguageRuleMode.KeepMatching, ["en"]));
        var saved = await Store(workspace).ChangeAsync(new(RulePresetMutationKind.Add, PresetId, "Defaults", rules,
            TestContext.Current.CancellationToken));
        Assert.True(saved.Committed);
        var json = JsonNode.Parse(await ReadBytesAsync(workspace))!;
        var audioFilter = json["presets"]![0]!["rules"]!["audio"]!["titleFilter"]!;
        var subtitleFilter = json["presets"]![0]!["rules"]!["subtitles"]!["titleFilter"]!;
        Assert.False(audioFilter["enabled"]!.GetValue<bool>());
        Assert.Equal(string.Empty, audioFilter["text"]!.GetValue<string>());
        Assert.False(subtitleFilter["enabled"]!.GetValue<bool>());
        Assert.Equal(string.Empty, subtitleFilter["text"]!.GetValue<string>());
        var loaded = Assert.Single((await Store(workspace).ReadAsync(TestContext.Current.CancellationToken)).Catalog!.Items);
        Assert.True(LanguageRuleConfiguration.ContentEquals(rules, loaded.Rules));
    }

    /// <summary>Renaming preserves identity and rules; deletion affects only the requested ID.</summary>
    [Fact]
    public async Task RenameAndDeletePreserveUnrelatedEntriesAndImmutableIdentity()
    {
        using var workspace = new TestWorkspace();
        var store = Store(workspace);
        var otherId = Guid.NewGuid();
        await AddAsync(store, PresetId, "First");
        await AddAsync(store, otherId, "Other");
        var rename = await store.ChangeAsync(new(RulePresetMutationKind.Rename, PresetId, " Renamed ",
            CancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(rename.Failure);
        Assert.True(rename.Committed);
        var renamed = Assert.Single(rename.Catalog!.Items.Where(item => item.Id == PresetId));
        Assert.Equal("Renamed", renamed.Name);
        AssertRulesEqual(Rules(), renamed.Rules);
        var deleted = await store.ChangeAsync(new(RulePresetMutationKind.Delete, PresetId,
            CancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(deleted.Failure);
        Assert.Equal(otherId, Assert.Single(deleted.Catalog!.Items).Id);
        AssertNoTemporaryFiles(workspace.Path);
    }

    /// <summary>Each intent rereads the locked latest catalog rather than overwriting a previously read snapshot.</summary>
    [Fact]
    public async Task IndependentInstancesMergeIntentsAgainstTheLatestCompleteCatalog()
    {
        using var workspace = new TestWorkspace();
        var first = Store(workspace);
        var second = Store(workspace);
        Assert.Empty((await first.ReadAsync(TestContext.Current.CancellationToken)).Catalog!.Items);
        var otherId = Guid.NewGuid();
        await AddAsync(second, otherId, "Second process intent");
        var result = await AddAsync(first, PresetId, "First process intent");
        Assert.Equal(new[] { otherId, PresetId }, result.Catalog!.Items.Select(item => item.Id));
        AssertNoTemporaryFiles(workspace.Path);
    }

    /// <summary>Invalid mutations never modify the last complete catalog.</summary>
    [Fact]
    public async Task DuplicateNameLeavesOriginalBytesUntouched()
    {
        using var workspace = new TestWorkspace();
        var store = Store(workspace);
        await AddAsync(store, PresetId, "Existing");
        var before = await ReadBytesAsync(workspace);
        var result = await store.ChangeAsync(new(RulePresetMutationKind.Add, Guid.NewGuid(), " existing ", Rules(),
            TestContext.Current.CancellationToken));
        Assert.Equal(RulePresetErrorCode.DuplicateName, result.Failure?.Code);
        Assert.False(result.Committed);
        Assert.Equal(before, await ReadBytesAsync(workspace));
        AssertNoTemporaryFiles(workspace.Path);
    }

    /// <summary>An unchanged display name does not replace the catalog or report a new commit.</summary>
    [Fact]
    public async Task NoopRenameRetainsExistingBytesWithoutPublication()
    {
        using var workspace = new TestWorkspace();
        var store = Store(workspace);
        await AddAsync(store, PresetId, "Original");
        var before = await ReadBytesAsync(workspace);
        var actions = new RulePresetFileActions((_, _) => throw new IOException("A no-op must not publish."), File.Delete);
        var result = await new PortableRulePresetStore(workspace.Path, actions).ChangeAsync(new(
            RulePresetMutationKind.Rename, PresetId, " Original ", CancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(result.Failure);
        Assert.False(result.Committed);
        Assert.Equal(before, await ReadBytesAsync(workspace));
        AssertNoTemporaryFiles(workspace.Path);
    }

    /// <summary>Corruption, missing required members and surplus data are visible and preserved on both read and change.</summary>
    [Theory]
    [InlineData("root-null")]
    [InlineData("root-array")]
    [InlineData("malformed")]
    [InlineData("missing-version")]
    [InlineData("null-version")]
    [InlineData("string-version")]
    [InlineData("missing-presets")]
    [InlineData("null-presets")]
    [InlineData("null-entry")]
    [InlineData("missing-id")]
    [InlineData("empty-id")]
    [InlineData("null-name")]
    [InlineData("untrimmed-name")]
    [InlineData("null-rules")]
    [InlineData("missing-audio")]
    [InlineData("null-audio")]
    [InlineData("null-subtitles")]
    [InlineData("missing-forced")]
    [InlineData("null-forced")]
    [InlineData("missing-mode")]
    [InlineData("numeric-mode")]
    [InlineData("unknown-mode")]
    [InlineData("wrong-case-mode")]
    [InlineData("missing-languages")]
    [InlineData("null-languages")]
    [InlineData("null-language")]
    [InlineData("missing-title-filter")]
    [InlineData("null-audio-title-filter")]
    [InlineData("null-subtitle-title-filter")]
    [InlineData("missing-title-enabled")]
    [InlineData("null-title-text")]
    [InlineData("unknown-root-field")]
    [InlineData("unknown-preset-field")]
    [InlineData("unknown-rule-field")]
    [InlineData("unknown-track-field")]
    [InlineData("unknown-title-field")]
    [InlineData("duplicate-id")]
    [InlineData("duplicate-name")]
    public async Task InvalidCatalogIsNotPartiallyLoadedOrOverwritten(string corruption)
    {
        using var workspace = new TestWorkspace();
        var bytes = CorruptDocument(corruption);
        await File.WriteAllBytesAsync(CatalogPath(workspace), bytes, TestContext.Current.CancellationToken);
        var store = Store(workspace);
        AssertRejected(await store.ReadAsync(TestContext.Current.CancellationToken));
        AssertRejected(await AddAsync(store, Guid.NewGuid(), "New"));
        Assert.Equal(bytes, await ReadBytesAsync(workspace));
        AssertNoTemporaryFiles(workspace.Path);
    }

    /// <summary>Unsupported catalog versions remain intact instead of being interpreted as an empty catalog.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task UnknownVersionIsExplicitAndNeverOverwritten(int version)
    {
        using var workspace = new TestWorkspace();
        var node = ValidDocument();
        node["version"] = version;
        var bytes = Encoding.UTF8.GetBytes(node.ToJsonString());
        await File.WriteAllBytesAsync(CatalogPath(workspace), bytes, TestContext.Current.CancellationToken);
        var read = await Store(workspace).ReadAsync(TestContext.Current.CancellationToken);
        var write = await AddAsync(Store(workspace), Guid.NewGuid(), "New");
        Assert.Equal(RulePresetErrorCode.UnsupportedVersion, read.Failure?.Code);
        Assert.Equal(RulePresetErrorCode.UnsupportedVersion, write.Failure?.Code);
        Assert.Null(read.Catalog);
        Assert.False(write.Committed);
        Assert.Equal(bytes, await ReadBytesAsync(workspace));
    }

    /// <summary>Duplicate JSON keys are rejected at every meaningful schema level.</summary>
    [Theory]
    [InlineData("\"version\":1", "\"version\":1,\"version\":1")]
    [InlineData("\"name\":\"Preset\"", "\"name\":\"Preset\",\"name\":\"Other\"")]
    [InlineData("\"mode\":\"Unchanged\"", "\"mode\":\"Unchanged\",\"mode\":\"KeepMatching\"")]
    [InlineData("\"enabled\":false", "\"enabled\":false,\"enabled\":true")]
    public async Task DuplicatePropertiesDoNotUseTheLastValue(string original, string replacement)
    {
        using var workspace = new TestWorkspace();
        var raw = ValidDocument().ToJsonString().Replace(original, replacement, StringComparison.Ordinal);
        Assert.NotEqual(ValidDocument().ToJsonString(), raw);
        var bytes = Encoding.UTF8.GetBytes(raw);
        await File.WriteAllBytesAsync(CatalogPath(workspace), bytes, TestContext.Current.CancellationToken);
        AssertRejected(await Store(workspace).ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(bytes, await ReadBytesAsync(workspace));
    }

    /// <summary>The maximum is the actual UTF-8 byte count, including whitespace, rather than a character estimate.</summary>
    [Fact]
    public async Task AcceptsExactReadByteBoundaryAndRejectsTheNextByte()
    {
        using var workspace = new TestWorkspace();
        var valid = Encoding.UTF8.GetBytes("{\"version\":1,\"presets\":[]}");
        var bytes = Enumerable.Repeat((byte)' ', RulePresetJson.MaximumBytes).ToArray();
        valid.CopyTo(bytes, 0);
        await File.WriteAllBytesAsync(CatalogPath(workspace), bytes, TestContext.Current.CancellationToken);
        Assert.Null((await Store(workspace).ReadAsync(TestContext.Current.CancellationToken)).Failure);
        byte[] oversized = [.. bytes, (byte)' '];
        await File.WriteAllBytesAsync(CatalogPath(workspace), oversized, TestContext.Current.CancellationToken);
        var result = await Store(workspace).ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(RulePresetErrorCode.LimitExceeded, result.Failure?.Code);
        Assert.Null(result.Catalog);
        Assert.Equal(oversized, await ReadBytesAsync(workspace));
    }

    /// <summary>Real serialization expansion can reject an addition before the logical item-count limit.</summary>
    [Fact]
    public async Task ActualSerializedByteLimitPreservesThePriorCompleteCatalog()
    {
        using var workspace = new TestWorkspace();
        var store = Store(workspace);
        var rule = new TrackLanguageRule(LanguageRuleMode.Unchanged, [], new(false, new string('Ж', RulePresetLimits.MaximumTitleLength)));
        var rules = new LanguageRules(rule, rule, true);
        var reachedByteLimit = false;

        for (var entryNumber = 0; entryNumber < RulePresetLimits.MaximumEntries; entryNumber++)
        {
            var before = File.Exists(CatalogPath(workspace)) ? await ReadBytesAsync(workspace) : [];
            var result = await store.ChangeAsync(new(RulePresetMutationKind.Add, Guid.NewGuid(), $"Preset {entryNumber}", rules,
                TestContext.Current.CancellationToken));

            if (result.Failure?.Code == RulePresetErrorCode.LimitExceeded)
            {
                Assert.False(result.Committed);
                Assert.Equal(before, await ReadBytesAsync(workspace));
                reachedByteLimit = true;
                break;
            }

            Assert.Null(result.Failure);
            Assert.True(result.Committed);
            Assert.True((await ReadBytesAsync(workspace)).Length <= RulePresetJson.MaximumBytes);
        }

        Assert.True(reachedByteLimit);
        AssertNoTemporaryFiles(workspace.Path);
    }

    /// <summary>All stored fields are bounded even when a rule or title filter is inactive.</summary>
    [Theory]
    [InlineData("entries")]
    [InlineData("languages")]
    [InlineData("language-text")]
    [InlineData("title-text")]
    public async Task StoredLogicalLimitsDoNotDependOnWhetherMatchingUsesTheField(string limit)
    {
        using var workspace = new TestWorkspace();
        var root = ValidDocument();
        var presets = root["presets"]!.AsArray();
        var audio = presets[0]!["rules"]!["audio"]!;

        if (limit == "entries")
        {
            var template = presets[0]!.DeepClone();
            presets.Clear();

            for (var entryNumber = 0; entryNumber <= RulePresetLimits.MaximumEntries; entryNumber++)
            {
                var entry = template.DeepClone();
                entry["id"] = Guid.NewGuid();
                entry["name"] = $"Preset {entryNumber}";
                presets.Add(entry);
            }
        }

        if (limit == "languages")
        {
            audio["languages"] = new JsonArray(Enumerable.Repeat("en", RulePresetLimits.MaximumLanguageValues + 1)
                .Select(value => JsonValue.Create(value)).ToArray());
        }

        if (limit == "language-text")
        {
            audio["languages"]![0] = new string('x', RulePresetLimits.MaximumLanguageLength + 1);
        }

        if (limit == "title-text")
        {
            audio["titleFilter"]!["text"] = new string('x', RulePresetLimits.MaximumTitleLength + 1);
        }

        var bytes = Encoding.UTF8.GetBytes(root.ToJsonString());
        await File.WriteAllBytesAsync(CatalogPath(workspace), bytes, TestContext.Current.CancellationToken);
        var result = await Store(workspace).ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(RulePresetErrorCode.LimitExceeded, result.Failure?.Code);
        Assert.Null(result.Catalog);
        Assert.Equal(bytes, await ReadBytesAsync(workspace));
    }

    /// <summary>Unknown deep structures and invalid UTF-8 cannot escape as partially decoded configurations.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsDepthOverflowAndInvalidUtf8(bool invalidUtf8)
    {
        using var workspace = new TestWorkspace();
        var bytes = invalidUtf8 ? new byte[] { (byte)'"', 0xC3, 0x28, (byte)'"' }
            : Encoding.UTF8.GetBytes(new string('[', 17) + "0" + new string(']', 17));
        await File.WriteAllBytesAsync(CatalogPath(workspace), bytes, TestContext.Current.CancellationToken);
        AssertRejected(await Store(workspace).ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(bytes, await ReadBytesAsync(workspace));
    }

    /// <summary>A controlled publication failure preserves old bytes and removes only the uniquely owned temporary.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ControlledPrecommitFailurePreservesPrimaryFailureAndReportsCleanupSeparately(bool failCleanup)
    {
        using var workspace = new TestWorkspace();
        await AddAsync(Store(workspace), PresetId, "Original");
        var before = await ReadBytesAsync(workspace);
        var unrelated = Path.Combine(workspace.Path, ".rule-presets.unrelated.tmp");
        await File.WriteAllTextAsync(unrelated, "not owned", TestContext.Current.CancellationToken);
        var actions = new RulePresetFileActions((_, _) => throw new IOException("controlled publication failure"), path =>
        {
            if (failCleanup)
            {
                throw new IOException("controlled cleanup failure");
            }

            File.Delete(path);
        });
        var result = await AddAsync(new PortableRulePresetStore(workspace.Path, actions), Guid.NewGuid(), "New");
        Assert.False(result.Committed);
        Assert.Equal(RulePresetErrorCode.PersistenceUnavailable, result.Failure?.Code);
        Assert.Contains("controlled publication failure", result.Failure!.Detail, StringComparison.Ordinal);
        Assert.Equal(failCleanup ? 1 : 0, result.CleanupIssues.IsDefault ? 0 : result.CleanupIssues.Length);
        Assert.Equal(before, await ReadBytesAsync(workspace));
        Assert.Equal("not owned", await File.ReadAllTextAsync(unrelated, TestContext.Current.CancellationToken));
        Assert.Equal(failCleanup ? 2 : 1, Directory.GetFiles(workspace.Path, ".rule-presets.*.tmp").Length);
    }

    /// <summary>Successful publication consumes ownership of the old temporary name and never deletes a recreated entry.</summary>
    [Fact]
    public async Task PublishedTemporaryPathCannotAuthorizeCleanupOfARecreatedUnrelatedFile()
    {
        using var workspace = new TestWorkspace();
        string? unrelatedPath = null;
        var cleanupCalled = false;
        var actions = new RulePresetFileActions((source, destination) =>
        {
            File.Move(source, destination, overwrite: true);
            unrelatedPath = source;
            File.WriteAllText(source, "unrelated recreated entry");
        }, _ => cleanupCalled = true);
        var result = await AddAsync(new PortableRulePresetStore(workspace.Path, actions), PresetId, "Saved");
        Assert.True(result.Committed);
        Assert.Null(result.Failure);
        Assert.True(result.CleanupIssues.IsDefaultOrEmpty);
        Assert.False(cleanupCalled);
        Assert.Equal(PresetId, Assert.Single(result.Catalog!.Items).Id);
        Assert.Equal(PresetId, Assert.Single((await Store(workspace).ReadAsync(TestContext.Current.CancellationToken)).Catalog!.Items).Id);
        Assert.NotNull(unrelatedPath);
        Assert.Equal("unrelated recreated entry", await File.ReadAllTextAsync(unrelatedPath, TestContext.Current.CancellationToken));
    }

    /// <summary>Cancellation requested after the real atomic move cannot turn a saved catalog into an uncommitted result.</summary>
    [Fact]
    public async Task CancellationAfterPublicationRetainsTheCommittedResult()
    {
        using var workspace = new TestWorkspace();
        using var cancellation = new CancellationTokenSource();
        var actions = new RulePresetFileActions((source, destination) =>
        {
            File.Move(source, destination, overwrite: true);
            cancellation.Cancel();
        }, File.Delete);
        var result = await new PortableRulePresetStore(workspace.Path, actions).ChangeAsync(new(
            RulePresetMutationKind.Add, PresetId, "Saved", Rules(), cancellation.Token));
        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(result.Committed);
        Assert.Null(result.Failure);
        Assert.Equal(PresetId, Assert.Single((await Store(workspace).ReadAsync(TestContext.Current.CancellationToken)).Catalog!.Items).Id);
        AssertNoTemporaryFiles(workspace.Path);
    }

    /// <summary>Pre-cancelled operations neither create state nor hide cancellation as a successful empty catalog.</summary>
    [Fact]
    public async Task PrecancelledReadAndChangeAreExplicitAndDoNotCreateData()
    {
        using var workspace = new TestWorkspace();
        var dataPath = Path.Combine(workspace.Path, "data");
        var store = new PortableRulePresetStore(dataPath);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var read = await store.ReadAsync(cancellation.Token);
        var change = await store.ChangeAsync(new(RulePresetMutationKind.Add, PresetId, "New", Rules(), cancellation.Token));
        Assert.Equal(RulePresetErrorCode.Cancelled, read.Failure?.Code);
        Assert.Equal(RulePresetErrorCode.Cancelled, change.Failure?.Code);
        Assert.False(Directory.Exists(dataPath));
    }

    /// <summary>A separate real process holds the production lock until an explicit release, making contention fail fast.</summary>
    [Fact]
    public async Task SeparateProcessLockPreventsConcurrentChangeAndAllowsRetryAfterRelease()
    {
        using var workspace = new TestWorkspace();
        var store = Store(workspace);
        await AddAsync(store, PresetId, "Original");
        var before = await ReadBytesAsync(workspace);
        using var worker = StartLockWorker(Path.Combine(workspace.Path, PortableRulePresetStore.LockFileName));

        try
        {
            Assert.Equal("PRESET_LOCK_ACQUIRED", await worker.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken)
                .AsTask().WaitAsync(ProcessTimeout, TestContext.Current.CancellationToken));
            var result = await AddAsync(Store(workspace), Guid.NewGuid(), "Blocked").WaitAsync(ProcessTimeout, TestContext.Current.CancellationToken);
            Assert.Equal(RulePresetErrorCode.Busy, result.Failure?.Code);
            Assert.False(result.Committed);
            Assert.Equal(before, await ReadBytesAsync(workspace));
            Assert.Null((await store.ReadAsync(TestContext.Current.CancellationToken)).Failure);
            await worker.StandardInput.WriteLineAsync(ReadOnlyMemory<char>.Empty, TestContext.Current.CancellationToken);
            await worker.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(ProcessTimeout, TestContext.Current.CancellationToken);
            Assert.Equal(0, worker.ExitCode);
            var retried = await AddAsync(Store(workspace), Guid.NewGuid(), "After release");
            Assert.True(retried.Committed);
            Assert.Equal(2, retried.Catalog!.Items.Length);
            Assert.True(File.Exists(Path.Combine(workspace.Path, PortableRulePresetStore.LockFileName)));
            AssertNoTemporaryFiles(workspace.Path);
        }
        finally
        {
            if (!worker.HasExited)
            {
                worker.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync(CancellationToken.None).WaitAsync(ProcessTimeout, CancellationToken.None);
            }
        }
    }

    /// <summary>Provides complete original configuration with inactive data that must survive storage verbatim.</summary>
    private static LanguageRules Rules() => new(
        new(LanguageRuleMode.Unchanged, ["ENG", "en", "arbitrary inactive"], new(false, "  Main[cut].* Русский  ")),
        new(LanguageRuleMode.KeepMatching, ["ru", "rus"], new(true, " Captions ")), true);

    /// <summary>Creates a fresh port instance over this test's isolated portable data.</summary>
    private static PortableRulePresetStore Store(TestWorkspace workspace) => new(workspace.Path);

    /// <summary>Issues an add intent without substituting a stale complete catalog.</summary>
    private static Task<RulePresetCatalogResult> AddAsync(PortableRulePresetStore store, Guid id, string name) =>
        store.ChangeAsync(new(RulePresetMutationKind.Add, id, name, Rules(), TestContext.Current.CancellationToken));

    /// <summary>Resolves only the catalog owned by this filesystem test.</summary>
    private static string CatalogPath(TestWorkspace workspace) => Path.Combine(workspace.Path, PortableRulePresetStore.CatalogFileName);

    /// <summary>Reads exact persisted bytes for preservation assertions.</summary>
    private static Task<byte[]> ReadBytesAsync(TestWorkspace workspace) => File.ReadAllBytesAsync(CatalogPath(workspace), TestContext.Current.CancellationToken);

    /// <summary>Builds one complete versioned JSON object before a targeted corruption.</summary>
    private static JsonObject ValidDocument() => JsonNode.Parse(RulePresetJson.Write(new([new(PresetId, "Preset", Rules())])))!.AsObject();

    /// <summary>Changes exactly one schema boundary while leaving unrelated valid values intact.</summary>
    private static byte[] CorruptDocument(string corruption)
    {
        var root = ValidDocument();
        var presets = root["presets"]!.AsArray();
        var preset = presets[0]!.AsObject();
        var rules = preset["rules"]!.AsObject();
        var audio = rules["audio"]!.AsObject();
        var title = audio["titleFilter"]!.AsObject();

        switch (corruption)
        {
            case "root-null": return "null"u8.ToArray();
            case "root-array": return "[]"u8.ToArray();
            case "malformed": return "{broken"u8.ToArray();
            case "missing-version": root.Remove("version"); break;
            case "null-version": root["version"] = null; break;
            case "string-version": root["version"] = "1"; break;
            case "missing-presets": root.Remove("presets"); break;
            case "null-presets": root["presets"] = null; break;
            case "null-entry": presets[0] = null; break;
            case "missing-id": preset.Remove("id"); break;
            case "empty-id": preset["id"] = Guid.Empty; break;
            case "null-name": preset["name"] = null; break;
            case "untrimmed-name": preset["name"] = " Preset "; break;
            case "null-rules": preset["rules"] = null; break;
            case "missing-audio": rules.Remove("audio"); break;
            case "null-audio": rules["audio"] = null; break;
            case "null-subtitles": rules["subtitles"] = null; break;
            case "missing-forced": rules.Remove("keepOnlyForcedSubtitles"); break;
            case "null-forced": rules["keepOnlyForcedSubtitles"] = null; break;
            case "missing-mode": audio.Remove("mode"); break;
            case "numeric-mode": audio["mode"] = 0; break;
            case "unknown-mode": audio["mode"] = "Future"; break;
            case "wrong-case-mode": audio["mode"] = "unchanged"; break;
            case "missing-languages": audio.Remove("languages"); break;
            case "null-languages": audio["languages"] = null; break;
            case "null-language": audio["languages"]![0] = null; break;
            case "missing-title-filter": audio.Remove("titleFilter"); break;
            case "null-audio-title-filter": audio["titleFilter"] = null; break;
            case "null-subtitle-title-filter": rules["subtitles"]!["titleFilter"] = null; break;
            case "missing-title-enabled": title.Remove("enabled"); break;
            case "null-title-text": title["text"] = null; break;
            case "unknown-root-field": root["queue"] = new JsonArray(); break;
            case "unknown-preset-field": preset["sourcePath"] = "not-allowed"; break;
            case "unknown-rule-field": rules["outputFolder"] = "not-allowed"; break;
            case "unknown-track-field": audio["trackIds"] = new JsonArray(1, 2); break;
            case "unknown-title-field": title["regex"] = true; break;
            case "duplicate-id": presets.Add(preset.DeepClone()); presets[1]!["name"] = "Other"; break;
            case "duplicate-name": presets.Add(preset.DeepClone()); presets[1]!["id"] = Guid.NewGuid(); break;
            default: throw new ArgumentOutOfRangeException(nameof(corruption));
        }

        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    /// <summary>Checks that invalid data never becomes an apparently usable partial catalog.</summary>
    private static void AssertRejected(RulePresetCatalogResult result)
    {
        Assert.NotNull(result.Failure);
        Assert.Null(result.Catalog);
        Assert.False(result.Committed);
    }

    /// <summary>Compares raw immutable configuration values rather than record array identity.</summary>
    private static void AssertRulesEqual(LanguageRules expected, LanguageRules actual)
    {
        Assert.Equal(expected.Audio.Mode, actual.Audio.Mode);
        Assert.Equal(expected.Audio.Languages, actual.Audio.Languages);
        Assert.Equal(expected.Audio.TitleFilter, actual.Audio.TitleFilter);
        Assert.Equal(expected.Subtitles.Mode, actual.Subtitles.Mode);
        Assert.Equal(expected.Subtitles.Languages, actual.Subtitles.Languages);
        Assert.Equal(expected.Subtitles.TitleFilter, actual.Subtitles.TitleFilter);
        Assert.Equal(expected.KeepOnlyForcedSubtitles, actual.KeepOnlyForcedSubtitles);
    }

    /// <summary>Checks that normal failures and successes leave no owned temporary file behind.</summary>
    private static void AssertNoTemporaryFiles(string directory) => Assert.Empty(Directory.GetFiles(directory, ".rule-presets.*.tmp"));

    /// <summary>Launches the existing controlled helper in a separate process using the test suite's exact runtime.</summary>
    private static Process StartLockWorker(string lockPath)
    {
        var runtime = new DirectoryInfo(Path.GetDirectoryName(typeof(object).Assembly.Location)!);
        var runtimeRoot = runtime.Parent!.Parent!.Parent!.FullName;
        var executable = Path.Combine(runtimeRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var helper = Path.Combine(MediaTestEnvironment.RepositoryDirectory, "tests", "TrackTrim.ProcessTestWorker", "bin",
            configuration, "net10.0", "TrackTrim.ProcessTestWorker.dll");
        Assert.True(File.Exists(helper), $"The existing test worker must be built: {helper}");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(helper);
        start.ArgumentList.Add("preset-lock");
        start.ArgumentList.Add(lockPath);
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the controlled lock holder.");
    }
}
