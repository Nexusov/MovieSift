using MovieSift.Infrastructure.Files;
using Xunit;

namespace MovieSift.Infrastructure.Tests;

/// <summary>Checks that only the explicit ordinary version-two marker moves data/leases to the launcher root.</summary>
public sealed class PortableLayoutTests
{
    /// <summary>A directory named app is not sufficient to redirect an ordinary source build or flat installation.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("MovieSift portable data policy version 1")]
    [InlineData("MovieSift portable data policy version 99")]
    public async Task UnrecognizedParentDoesNotChangeTheApplicationRoot(string? marker)
    {
        using var workspace = new TestWorkspace();
        var payload = Path.Combine(workspace.Path, "app");
        Directory.CreateDirectory(payload);

        if (marker is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(workspace.Path, "portable.flag"), marker, TestContext.Current.CancellationToken);
        }

        Assert.Equal(payload, PortableLayout.ApplicationRootFromBaseDirectory(payload));
        Assert.Equal(workspace.Path, PortableLayout.PayloadDirectory(workspace.Path));
    }

    /// <summary>Working directory changes do not affect the recognized fixed relationship between payload and portable data.</summary>
    [Fact]
    public async Task NestedMarkerKeepsSettingsBesideTheLauncher()
    {
        using var workspace = new TestWorkspace();
        var payload = Path.Combine(workspace.Path, "app");
        Directory.CreateDirectory(payload);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "portable.flag"), PortableLayout.NestedMarker + "\n", TestContext.Current.CancellationToken);
        Assert.Equal(workspace.Path, PortableLayout.ApplicationRootFromBaseDirectory(payload));
        Assert.Equal(payload, PortableLayout.PayloadDirectory(workspace.Path));
        Assert.Equal(workspace.Path, PortableLayout.ApplicationRootFromBaseDirectory(workspace.Path));
    }

    /// <summary>A hostile marker cannot cause an unbounded read or a guessed layout.</summary>
    [Fact]
    public async Task OversizedMarkerFailsVisibly()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace.Path, "app"));
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "portable.flag"), new string('x', 129), TestContext.Current.CancellationToken);
        Assert.Throws<IOException>(() => PortableLayout.PayloadDirectory(workspace.Path));
    }
}
