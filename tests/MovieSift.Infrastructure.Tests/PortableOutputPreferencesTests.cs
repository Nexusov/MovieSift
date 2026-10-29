using System.Text.Json;
using MovieSift.Application.Ports;
using MovieSift.Core.Planning;
using MovieSift.Infrastructure.Persistence;
using Xunit;

namespace MovieSift.Infrastructure.Tests;

/// <summary>Checks remembered output intent without serializing media paths, jobs or approval authority.</summary>
public sealed class PortableOutputPreferencesTests
{
    /// <summary>A fresh portable copy defaults to safe copies and does not manufacture a preference record.</summary>
    [Fact]
    public async Task MissingRecordDoesNotWriteAndRoundTripRestoresOnlyOutputIntent()
    {
        using var workspace = new TestWorkspace();
        var store = new PortableOutputPreferencesStore(workspace.Path);
        Assert.Equal(new(false, OutputMode.Copy), await store.LoadAsync(TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(workspace.Path, "output-preferences.json")));
        await store.SaveAsync(new(true, OutputMode.ReplaceSource), TestContext.Current.CancellationToken);
        var fresh = new PortableOutputPreferencesStore(workspace.Path);
        Assert.Equal(new(true, OutputMode.ReplaceSource), await fresh.LoadAsync(TestContext.Current.CancellationToken));
        using var record = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace.Path, "output-preferences.json"), TestContext.Current.CancellationToken));
        Assert.Equal(["Version", "Remember", "Mode"], record.RootElement.EnumerateObject().Select(property => property.Name));
    }

    /// <summary>A corrupt record is reported and remains untouched instead of being silently overwritten.</summary>
    [Fact]
    public async Task MalformedPreferenceIsNotSilentlyReplaced()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Path, "output-preferences.json");
        await File.WriteAllTextAsync(path, "{broken", TestContext.Current.CancellationToken);
        var store = new PortableOutputPreferencesStore(workspace.Path);
        await Assert.ThrowsAsync<JsonException>(() => store.SaveAsync(new(true, OutputMode.ReplaceSource), TestContext.Current.CancellationToken));
        Assert.Equal("{broken", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(workspace.Path, "*.tmp"));
    }

    /// <summary>Another instance receives a mapped persistence failure while the prior preference stays unchanged.</summary>
    [Fact]
    public async Task ConcurrentWriterDoesNotFaultOutsideThePresentationFailurePolicy()
    {
        using var workspace = new TestWorkspace();
        var store = new PortableOutputPreferencesStore(workspace.Path);
        await store.SaveAsync(new(false, OutputMode.Copy), TestContext.Current.CancellationToken);
        using (PortableRecordFileLock.Acquire(Path.Combine(workspace.Path, ".output-preferences.lock")))
        {
            await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(new(true, OutputMode.ReplaceSource), TestContext.Current.CancellationToken));
        }

        Assert.Equal(new(false, OutputMode.Copy), await store.LoadAsync(TestContext.Current.CancellationToken));
        await store.SaveAsync(new(true, OutputMode.ReplaceSource), TestContext.Current.CancellationToken);
        Assert.Equal(new(true, OutputMode.ReplaceSource), await store.LoadAsync(TestContext.Current.CancellationToken));
    }
}
