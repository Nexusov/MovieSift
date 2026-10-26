namespace MovieSift.Application.Updates;

/// <summary>Identifies the complete, platform-specific archive offered by a public stable release.</summary>
public sealed record UpdateAsset(string Name, Uri DownloadUri, long Size, string Sha256);

/// <summary>Freezes the version and archive identity selected by a successful update check.</summary>
public sealed record UpdateRelease(AppVersion Version, Uri ReleasePage, UpdateAsset Asset);

/// <summary>Identifies a verified staged package; preparation never changes the running installation.</summary>
public sealed record PreparedUpdate(string SessionDirectory, AppVersion Version);

/// <summary>Reports bounded download progress without coupling installation to a desktop framework.</summary>
public sealed record UpdateProgress(long BytesReceived, long TotalBytes);

/// <summary>Separates recoverable update failures from media job failures.</summary>
public enum UpdateErrorCode { Network, InvalidRelease, InvalidPackage, Integrity, UnsupportedInstallation, Busy, Storage, Installer }

/// <summary>Supplies a stable UI error code and a diagnostic while preserving its original cause.</summary>
public sealed class UpdateException(UpdateErrorCode code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public UpdateErrorCode Code { get; } = code;
}
