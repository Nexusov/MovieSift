namespace MovieSift.Application.Ports;

public interface ISettingsStore
{
    Task<string> LoadLanguageAsync(CancellationToken cancellationToken = default);
    Task SaveLanguageAsync(SaveLanguageRequest request);
}
