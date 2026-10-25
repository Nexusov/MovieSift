namespace TrackTrim.Application.Appearance;

/// <summary>Validates bounded portable values; visual contrast is evaluated by the Desktop palette.</summary>
public static class AppearanceSettingsPolicy
{
    private const int OpaqueHexLength = 7;

    /// <summary>Rejects invalid modes and color syntax without normalizing or applying appearance.</summary>
    public static bool IsValid(AppearanceSettings settings)
    {
        var valid = settings is not null && Enum.IsDefined(settings.Theme) && Enum.IsDefined(settings.Motion) &&
            IsOpaqueHex(settings.AccentHex) && ValidOverrides(settings.LightOverrides) && ValidOverrides(settings.DarkOverrides);
        return valid;
    }

    /// <summary>Accepts opaque RGB only so unknown alpha cannot undermine contrast validation.</summary>
    public static bool IsOpaqueHex(string? value) => value is { Length: OpaqueHexLength } && value[0] == '#' &&
        value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    /// <summary>Allows missing overrides while requiring each provided field to be a bounded RGB value.</summary>
    private static bool ValidOverrides(AppearancePaletteOverrides? palette) => palette is null ||
        new[] { palette.Page, palette.Surface, palette.PrimaryText, palette.SecondaryText, palette.Border }
            .All(value => value is null || IsOpaqueHex(value));
}
