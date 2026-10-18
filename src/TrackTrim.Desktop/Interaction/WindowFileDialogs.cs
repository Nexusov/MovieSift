using Avalonia.Controls;
using Avalonia.Platform.Storage;
using TrackTrim.Desktop.Localization;

namespace TrackTrim.Desktop.Interaction;

/// <summary>Uses the active window's native storage provider for local paths.</summary>
public sealed class WindowFileDialogs(WindowFileDialogsOptions options) : IFileDialogs
{
    /// <summary>Prompts for a single local Matroska source.</summary>
    public async Task<string?> SelectSourceAsync()
    {
        var files = await options.Window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = options.Localization["FileDialogTitle"],
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Matroska") { Patterns = ["*.mkv"], MimeTypes = ["video/x-matroska"] }]
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    /// <summary>Prompts for a separate destination folder without writing media.</summary>
    public async Task<string?> SelectOutputFolderAsync()
    {
        var folders = await options.Window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = options.Localization["OutputDialogTitle"],
            AllowMultiple = false
        });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }
}
