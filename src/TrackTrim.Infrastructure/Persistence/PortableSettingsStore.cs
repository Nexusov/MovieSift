using System.Text.Json;
using TrackTrim.Application;
using TrackTrim.Application.Ports;
using TrackTrim.Core.Failures;

namespace TrackTrim.Infrastructure.Persistence;

/// <summary>Keeps the selected UI language beside the portable application.</summary>
public sealed class PortableSettingsStore(string dataDirectory) : ISettingsStore
{
    private const int SettingsVersion = 1;
    private readonly string dataPath = Path.GetFullPath(dataDirectory);

    /// <summary>Defaults to English independently of the operating-system culture.</summary>
    public async Task<string> LoadLanguageAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.Combine(dataPath, "settings.json");

        if (!File.Exists(path))
        {
            return "en";
        }

        try
        {
            var settings = await BoundedJson.ReadAsync<LanguageSettings>(path);
            return settings is { Version: SettingsVersion, Language: "ru" } ? "ru" : "en";
        }
        catch (Exception exception) when (exception is JsonException or OperationFailureException)
        {
            return "en";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new OperationFailureException(new(ErrorCode.PersistenceUnavailable, exception.Message));
        }
    }

    /// <summary>Persists only supported invariant language codes and reports write failures.</summary>
    public async Task SaveLanguageAsync(SaveLanguageRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        var language = request.Language == "ru" ? "ru" : "en";

        try
        {
            Directory.CreateDirectory(dataPath);
            await BoundedJson.WriteAsync(new JsonWriteRequest<LanguageSettings>(
                Path.Combine(dataPath, "settings.json"), new(SettingsVersion, language)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationFailureException)
        {
            throw new OperationFailureException(new(ErrorCode.PersistenceUnavailable, exception.Message));
        }
    }
}
