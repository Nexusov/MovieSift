using System.Text.Json;
using System.Text.Json.Serialization;

namespace MovieSift.Infrastructure.Updates;

/// <summary>Freezes one explicitly prepared installation; its hash is passed separately to the helper.</summary>
internal sealed record UpdateInstallPlan(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string Token,
    [property: JsonRequired] string ApplicationDirectory,
    [property: JsonRequired] string RuntimeIdentifier,
    [property: JsonRequired] string Version,
    [property: JsonRequired] string ManifestSha256,
    [property: JsonRequired] int ParentProcessId,
    [property: JsonRequired] long ParentStartedUtcTicks)
{
    internal const string FileName = "install.json";

    /// <summary>Publishes only a completely flushed newly owned plan.</summary>
    internal static async Task WriteAsync(string directory, UpdateInstallPlan plan, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(Path.Combine(directory, FileName), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(stream, plan, cancellationToken: cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }
}
