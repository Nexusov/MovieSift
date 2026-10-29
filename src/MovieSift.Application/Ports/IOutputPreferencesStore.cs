using MovieSift.Core.Planning;

namespace MovieSift.Application.Ports;

/// <summary>Stores a portable output-mode preference, never plan approval or replacement consent.</summary>
public interface IOutputPreferencesStore
{
    Task<OutputPreference> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(OutputPreference preference, CancellationToken cancellationToken = default);
}

/// <summary>Captures explicit remembering separately from a queue's actual reviewed output mode.</summary>
public sealed record OutputPreference(bool Remember, OutputMode Mode);
