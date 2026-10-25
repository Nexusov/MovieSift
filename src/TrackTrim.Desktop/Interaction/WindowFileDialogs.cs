using Avalonia.Controls;
using Avalonia.Platform.Storage;
using TrackTrim.Application;
using TrackTrim.Core.Failures;
using TrackTrim.Desktop.Localization;

namespace TrackTrim.Desktop.Interaction;

/// <summary>Uses the active window's native storage provider for local paths.</summary>
public sealed class WindowFileDialogs(WindowFileDialogsOptions options) : IFileDialogs
{
    /// <summary>Prompts for independently inspected local MKV/MP4 files.</summary>
    public async Task<IReadOnlyList<string>> SelectSourcesAsync()
    {
        var files = await options.Window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = options.Localization["BatchFilesDialogTitle"],
            AllowMultiple = true,
            FileTypeFilter = [MediaFileType()]
        });
        return files.Select(file => RequireLocalPath(file, ErrorCode.InvalidSource)).ToArray();
    }

    /// <summary>Prompts for one root; the batch request separately captures whether to include subfolders.</summary>
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

    /// <summary>Filters candidates without treating their names as proof of a supported format.</summary>
    private static FilePickerFileType MediaFileType() => new("MKV / MP4")
    {
        Patterns = ["*.mkv", "*.mp4"],
        MimeTypes = ["video/x-matroska", "video/mp4"],
    };

    /// <summary>Rejects an accepted non-local selection visibly instead of treating it as cancellation.</summary>
    private static string RequireLocalPath(IStorageItem item, ErrorCode errorCode)
    {
        return item.TryGetLocalPath() ?? throw new OperationFailureException(new(errorCode,
            "The storage provider returned a non-local path."));
    }
}
