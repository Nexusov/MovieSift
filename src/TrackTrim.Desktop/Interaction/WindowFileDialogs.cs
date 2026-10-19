using Avalonia.Controls;
using Avalonia.Platform.Storage;
using TrackTrim.Application;
using TrackTrim.Core.Failures;
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
        return files.FirstOrDefault() is { } file ? RequireLocalPath(file, ErrorCode.InvalidSource) : null;
    }

    /// <summary>Prompts for independently inspected local Matroska files.</summary>
    public async Task<IReadOnlyList<string>> SelectSourcesAsync()
    {
        var files = await options.Window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = options.Localization["BatchFilesDialogTitle"],
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Matroska") { Patterns = ["*.mkv"], MimeTypes = ["video/x-matroska"] }]
        });
        return files.Select(file => RequireLocalPath(file, ErrorCode.InvalidSource)).ToArray();
    }

    /// <summary>Prompts for one folder; discovery never descends into its children.</summary>
    public async Task<string?> SelectInputFolderAsync()
    {
        var folders = await options.Window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = options.Localization["BatchFolderDialogTitle"],
            AllowMultiple = false
        });
        return folders.FirstOrDefault() is { } folder ? RequireLocalPath(folder, ErrorCode.InvalidSource) : null;
    }

    /// <summary>Prompts for a separate destination folder without writing media.</summary>
    public async Task<string?> SelectOutputFolderAsync()
    {
        var folders = await options.Window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = options.Localization["OutputDialogTitle"],
            AllowMultiple = false
        });
        return folders.FirstOrDefault() is { } folder ? RequireLocalPath(folder, ErrorCode.InvalidDestination) : null;
    }

    /// <summary>Rejects an accepted non-local selection visibly instead of treating it as cancellation.</summary>
    private static string RequireLocalPath(IStorageItem item, ErrorCode errorCode)
    {
        return item.TryGetLocalPath() ?? throw new OperationFailureException(new(errorCode,
            "The storage provider returned a non-local path."));
    }
}
