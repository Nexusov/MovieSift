namespace TrackTrim.Desktop.Interaction;

/// <summary>Separates native picker interaction from the workflow ViewModel.</summary>
public interface IFileDialogs
{
    Task<string?> SelectSourceAsync();
    Task<string?> SelectOutputFolderAsync();
}
