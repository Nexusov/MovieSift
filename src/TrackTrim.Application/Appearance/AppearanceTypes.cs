namespace TrackTrim.Application.Appearance;

public enum AppearanceTheme { System, Light, Dark }
public enum AppearanceMotion { System, Reduced, Full }
public enum AppearanceErrorCode { InvalidData, UnsupportedVersion, PersistenceUnavailable, Busy, Cancelled }

/// <summary>Stores optional opaque RGB values without depending on a UI color or brush implementation.</summary>
public sealed record AppearancePaletteOverrides(
    string? Page = null,
    string? Surface = null,
    string? PrimaryText = null,
    string? SecondaryText = null,
    string? Border = null);

public sealed record AppearanceSettings(
    AppearanceTheme Theme = AppearanceTheme.System,
    string AccentHex = "#5B6CDA",
    AppearancePaletteOverrides? LightOverrides = null,
    AppearancePaletteOverrides? DarkOverrides = null,
    AppearanceMotion Motion = AppearanceMotion.System)
{
    public static AppearanceSettings Default { get; } = new();
}

public sealed record AppearanceFailure(AppearanceErrorCode Code, string? Detail = null);
public sealed record AppearanceLoadResult(AppearanceSettings? Settings, AppearanceFailure? Failure = null, bool IsMissing = false);
public sealed record AppearanceSaveRequest(
    AppearanceSettings Settings,
    CancellationToken CancellationToken = default,
    bool ReplaceInvalidData = false);
public sealed record AppearanceSaveResult(bool Saved, AppearanceFailure? Failure = null, AppearanceFailure? CleanupFailure = null);
