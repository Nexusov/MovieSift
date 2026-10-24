namespace TrackTrim.Desktop.Interaction;

/// <summary>Separates native picker interaction from the workflow ViewModel.</summary>
public interface IFileDialogs
{
    Task<IReadOnlyList<string>> SelectSourcesAsync();
    Task<string?> SelectInputFolderAsync();
    Task<string?> SelectOutputFolderAsync();
}
