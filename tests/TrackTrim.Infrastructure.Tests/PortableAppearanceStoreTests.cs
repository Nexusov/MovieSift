using System.Text;
using TrackTrim.Application;
using TrackTrim.Application.Appearance;
using TrackTrim.Infrastructure.Persistence;
using Xunit;

namespace TrackTrim.Infrastructure.Tests;

/// <summary>Checks portable cosmetic records without coupling failures to media ownership or language settings.</summary>
public sealed class PortableAppearanceStoreTests
{
    /// <summary>Reading absent preferences uses built-ins without creating a file or data directory.</summary>
    [Fact]
    public async Task MissingRecordUsesBuiltInsWithoutWriting()
    {
        using var workspace = new TestWorkspace();
        var dataPath = Path.Combine(workspace.Path, "missing data");
        var result = await new PortableAppearanceStore(dataPath).LoadAsync(TestContext.Current.CancellationToken);
        Assert.Null(result.Failure);
        Assert.True(result.IsMissing);
        Assert.Equal(AppearanceSettings.Default, result.Settings);
        Assert.False(Directory.Exists(dataPath));
    }

    /// <summary>Round-trips every independent appearance value and leaves the language-only document untouched.</summary>
    [Fact]
    public async Task FreshStoreRestoresEveryAppearanceFieldSeparatelyFromLanguage()
    {
        using var workspace = new TestWorkspace();
        var settingsStore = new PortableSettingsStore(workspace.Path);
        await settingsStore.SaveLanguageAsync(new("ru", TestContext.Current.CancellationToken));
        var originalLanguage = await File.ReadAllBytesAsync(Path.Combine(workspace.Path, "settings.json"), TestContext.Current.CancellationToken);
        var settings = ConfiguredSettings();
        var saved = await new PortableAppearanceStore(workspace.Path).SaveAsync(new(settings, TestContext.Current.CancellationToken));
        Assert.True(saved.Saved);
        Assert.Null(saved.Failure);
        var loaded = await new PortableAppearanceStore(workspace.Path).LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(settings, loaded.Settings);
        Assert.False(loaded.IsMissing);
        Assert.Null(loaded.Failure);
        Assert.Equal(originalLanguage, await File.ReadAllBytesAsync(Path.Combine(workspace.Path, "settings.json"), TestContext.Current.CancellationToken));
        var appearanceBytes = await File.ReadAllBytesAsync(RecordPath(workspace), TestContext.Current.CancellationToken);
        await settingsStore.SaveLanguageAsync(new("en", TestContext.Current.CancellationToken));
        Assert.Equal(appearanceBytes, await File.ReadAllBytesAsync(RecordPath(workspace), TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(workspace.Path, ".appearance.*.tmp"));
    }

    /// <summary>Malformed data stays byte-identical through startup and ordinary saves until an explicit reset.</summary>
    [Theory]
    [InlineData("not json", AppearanceErrorCode.InvalidData)]
    [InlineData("{\"version\":2}", AppearanceErrorCode.UnsupportedVersion)]
    [InlineData("{\"version\":\"1\"}", AppearanceErrorCode.InvalidData)]
    [InlineData("{\"version\":1}", AppearanceErrorCode.InvalidData)]
    [InlineData("{\"version\":1,\"version\":1}", AppearanceErrorCode.InvalidData)]
    public async Task BadRecordIsVisibleAndPreservedUntilExplicitReset(string json, AppearanceErrorCode code)
    {
        using var workspace = new TestWorkspace();
        var original = Encoding.UTF8.GetBytes(json);
        await File.WriteAllBytesAsync(RecordPath(workspace), original, TestContext.Current.CancellationToken);
        var store = new PortableAppearanceStore(workspace.Path);
        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Null(loaded.Settings);
        Assert.Equal(code, loaded.Failure?.Code);
        var saved = await store.SaveAsync(new(ConfiguredSettings(), TestContext.Current.CancellationToken));
        Assert.False(saved.Saved);
        Assert.Equal(code, saved.Failure?.Code);
        Assert.Equal(original, await File.ReadAllBytesAsync(RecordPath(workspace), TestContext.Current.CancellationToken));
        var reset = await store.SaveAsync(new(AppearanceSettings.Default, TestContext.Current.CancellationToken, ReplaceInvalidData: true));
        Assert.True(reset.Saved);
        Assert.Equal(AppearanceSettings.Default, (await store.LoadAsync(TestContext.Current.CancellationToken)).Settings);
    }

    /// <summary>Rejects invalid values before writing and enforces the bounded UTF-8 record budget on reads.</summary>
    [Theory]
    [InlineData("#FF000080")]
    [InlineData("red")]
    [InlineData("")]
    [InlineData("#GG0000")]
    public async Task InvalidColorCannotReplaceLastUsableRecord(string color)
    {
        using var workspace = new TestWorkspace();
        var store = new PortableAppearanceStore(workspace.Path);
        Assert.True((await store.SaveAsync(new(ConfiguredSettings()))).Saved);
        var original = await File.ReadAllBytesAsync(RecordPath(workspace), TestContext.Current.CancellationToken);
        var invalid = await store.SaveAsync(new(ConfiguredSettings() with { AccentHex = color }));
        Assert.False(invalid.Saved);
        Assert.Equal(AppearanceErrorCode.InvalidData, invalid.Failure?.Code);
        Assert.Equal(original, await File.ReadAllBytesAsync(RecordPath(workspace), TestContext.Current.CancellationToken));
    }

    /// <summary>Size and enum/field constraints are explicit failures without partial fallback settings.</summary>
    [Fact]
    public async Task OversizedOrInvalidRequiredFieldsDoNotBecomeSuccessfulDefaults()
    {
        using var workspace = new TestWorkspace();
        var store = new PortableAppearanceStore(workspace.Path);
        await File.WriteAllBytesAsync(RecordPath(workspace), new byte[AppearanceJson.MaximumBytes + 1], TestContext.Current.CancellationToken);
        Assert.Equal(AppearanceErrorCode.InvalidData, (await store.LoadAsync(TestContext.Current.CancellationToken)).Failure?.Code);
        await File.WriteAllTextAsync(RecordPath(workspace), "{\"version\":1,\"theme\":99}", TestContext.Current.CancellationToken);
        Assert.Equal(AppearanceErrorCode.InvalidData, (await store.LoadAsync(TestContext.Current.CancellationToken)).Failure?.Code);
        var invalid = await store.SaveAsync(new(ConfiguredSettings() with { Theme = (AppearanceTheme)99 }));
        Assert.False(invalid.Saved);
        Assert.Equal(AppearanceErrorCode.InvalidData, invalid.Failure?.Code);
    }

    /// <summary>A failed replacement preserves the prior complete record and removes only the owned temporary file.</summary>
    [Fact]
    public async Task FailedReplacePreservesPreviousSettingsAndReportsPrimaryFailure()
    {
        using var workspace = new TestWorkspace();
        var store = new PortableAppearanceStore(workspace.Path);
        Assert.True((await store.SaveAsync(new(ConfiguredSettings()))).Saved);
        var original = await File.ReadAllBytesAsync(RecordPath(workspace), TestContext.Current.CancellationToken);
        var failing = new PortableAppearanceStore(workspace.Path, new(
            (_, _) => throw new IOException("controlled replacement failure"), File.Delete));
        var result = await failing.SaveAsync(new(AppearanceSettings.Default));
        Assert.False(result.Saved);
        Assert.Contains("controlled replacement failure", result.Failure!.Detail);
        Assert.Null(result.CleanupFailure);
        Assert.Equal(original, await File.ReadAllBytesAsync(RecordPath(workspace), TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(workspace.Path, ".appearance.*.tmp"));
    }

    /// <summary>A secondary temporary-cleanup issue cannot replace the primary persistence failure.</summary>
    [Fact]
    public async Task CleanupIssueRemainsSeparateFromPrimaryWriteFailure()
    {
        using var workspace = new TestWorkspace();
        var store = new PortableAppearanceStore(workspace.Path, new(
            (_, _) => throw new IOException("primary replace failure"),
            _ => throw new IOException("secondary cleanup failure")));
        var result = await store.SaveAsync(new(AppearanceSettings.Default));
        Assert.False(result.Saved);
        Assert.Contains("primary replace failure", result.Failure!.Detail);
        Assert.Contains("secondary cleanup failure", result.CleanupFailure!.Detail);
        Assert.False(File.Exists(RecordPath(workspace)));
        Assert.Single(Directory.GetFiles(workspace.Path, ".appearance.*.tmp"));
    }

    /// <summary>Cooperating instances fail fast while another appearance writer owns the stable OS lock.</summary>
    [Fact]
    public async Task ConcurrentWriterReturnsBusyWithoutReplacingRecord()
    {
        using var workspace = new TestWorkspace();
        var store = new PortableAppearanceStore(workspace.Path);
        Assert.True((await store.SaveAsync(new(ConfiguredSettings()))).Saved);
        var original = await File.ReadAllBytesAsync(RecordPath(workspace), TestContext.Current.CancellationToken);
        using (PortableRecordFileLock.Acquire(Path.Combine(workspace.Path, PortableAppearanceStore.LockFileName)))
        {
            var result = await store.SaveAsync(new(AppearanceSettings.Default));
            Assert.False(result.Saved);
            Assert.Equal(AppearanceErrorCode.Busy, result.Failure?.Code);
        }
        Assert.Equal(original, await File.ReadAllBytesAsync(RecordPath(workspace), TestContext.Current.CancellationToken));
    }

    /// <summary>Directories and other unsafe records cannot be overwritten even by the explicit reset path.</summary>
    [Fact]
    public async Task NonRegularRecordIsRejectedWithoutTreatingItAsCorruption()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(RecordPath(workspace));
        var store = new PortableAppearanceStore(workspace.Path);
        Assert.Equal(AppearanceErrorCode.PersistenceUnavailable, (await store.LoadAsync(TestContext.Current.CancellationToken)).Failure?.Code);
        var result = await store.SaveAsync(new(AppearanceSettings.Default, ReplaceInvalidData: true));
        Assert.False(result.Saved);
        Assert.Equal(AppearanceErrorCode.PersistenceUnavailable, result.Failure?.Code);
        Assert.True(Directory.Exists(RecordPath(workspace)));
    }

    /// <summary>Supplies distinct light/dark overrides and explicit reduced motion for full-value round-trip coverage.</summary>
    private static AppearanceSettings ConfiguredSettings() => new(AppearanceTheme.Dark, "#aBc123",
        new("#FAF6F0", "#FFFFFF", "#202020", "#555555", "#888888"),
        new("#171A20", "#222733", "#F5F5F5", "#CCCCCC", "#777777"), AppearanceMotion.Reduced);

    /// <summary>Uses only the task-owned portable appearance record.</summary>
    private static string RecordPath(TestWorkspace workspace) => Path.Combine(workspace.Path, PortableAppearanceStore.FileName);
}
