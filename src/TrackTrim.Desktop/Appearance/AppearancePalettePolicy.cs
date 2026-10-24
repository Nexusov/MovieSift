using Avalonia.Media;
using TrackTrim.Application.Appearance;

namespace TrackTrim.Desktop.Appearance;

/// <summary>Resolves warm Soft-inspired surfaces and checks actual linearized-sRGB contrast pairs.</summary>
public static class AppearancePalettePolicy
{
    public const double TextContrast = 4.5;
    public const double IndicatorContrast = 3;
    private const double SrgbLinearBoundary = 0.04045;
    private const double SrgbLinearDivisor = 12.92;
    private const double SrgbOffset = 0.055;
    private const double SrgbDivisor = 1.055;
    private const double SrgbExponent = 2.4;
    private const double ContrastOffset = 0.05;
    private const int ColorMaximum = 255;
    private const int AdjustmentSteps = 100;
    private const double HoverAmount = 0.10;
    private const double PressedAmount = 0.18;
    private const double SelectionAmount = 0.12;
    private const double SecondarySurfaceBlend = 0.55;
    private const double RedLuminanceWeight = 0.2126;
    private const double GreenLuminanceWeight = 0.7152;
    private const double BlueLuminanceWeight = 0.0722;

    /// <summary>Validates both variants before a live edit becomes active or is persisted.</summary>
    public static AppearancePaletteResult Resolve(AppearanceSettings settings)
    {
        if (!AppearanceSettingsPolicy.IsValid(settings) || !TryColor(settings.AccentHex, out var accent))
        {
            return new(null, null, "AppearanceInvalidHex");
        }

        var light = ResolveVariant(settings.LightOverrides, false, accent);
        var dark = ResolveVariant(settings.DarkOverrides, true, accent);
        return light is null || dark is null ? new(null, null, "AppearanceInvalidContrast") : new(light, dark);
    }

    /// <summary>Accepts opaque six-digit HEX only, matching the portable schema.</summary>
    public static bool TryColor(string? value, out Color color)
    {
        color = default;
        var validLength = value is { Length: 7 } && value[0] == '#';

        if (!validLength || value!.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") >= 0)
        {
            return false;
        }

        return Color.TryParse(value, out color) && color.A == ColorMaximum;
    }

    /// <summary>Computes the WCAG contrast ratio after the sRGB transfer function, never from brightness.</summary>
    public static double Contrast(Color first, Color second)
    {
        var firstLuminance = Luminance(first);
        var secondLuminance = Luminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + ContrastOffset) /
            (Math.Min(firstLuminance, secondLuminance) + ContrastOffset);
    }

    /// <summary>Converts an opaque color to the exact portable representation.</summary>
    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>Builds semantic colors and rejects custom text or borders that cannot remain readable.</summary>
    private static AppearancePalette? ResolveVariant(AppearancePaletteOverrides? custom, bool dark, Color accent)
    {
        var window = Read(custom?.Page, dark ? "#191B20" : "#F6F5F1");
        var panel = Read(custom?.Surface, dark ? "#24272E" : "#FFFFFF");
        var text = Read(custom?.PrimaryText, dark ? "#F5F2EA" : "#24231F");
        var muted = Read(custom?.SecondaryText, dark ? "#B9B4AA" : "#625F58");
        var border = Read(custom?.Border, dark ? "#818B9D" : "#7C7971");

        if (window is null || panel is null || text is null || muted is null || border is null)
        {
            return null;
        }

        var secondary = Blend(panel.Value, window.Value, SecondarySurfaceBlend);
        Color[] surfaces = [window.Value, panel.Value, secondary];
        var readable = Meets(text.Value, surfaces, TextContrast) && Meets(muted.Value, surfaces, TextContrast) &&
            Meets(border.Value, surfaces, IndicatorContrast);

        if (!readable)
        {
            return null;
        }

        var resolvedAccent = Adjust(accent, surfaces, IndicatorContrast);
        var onAccent = Contrast(resolvedAccent, Colors.White) >= Contrast(resolvedAccent, Colors.Black) ? Colors.White : Colors.Black;
        var hover = AccentState(resolvedAccent, onAccent, surfaces, HoverAmount);
        var pressed = AccentState(resolvedAccent, onAccent, surfaces, PressedAmount);
        var selection = Blend(panel.Value, resolvedAccent, SelectionAmount);

        if (Contrast(text.Value, selection) < TextContrast)
        {
            selection = panel.Value;
        }

        var error = Adjust(Color.Parse(dark ? "#FF95A8" : "#A12239"), surfaces, TextContrast);
        var warning = Adjust(Color.Parse(dark ? "#F1BC68" : "#80500A"), surfaces, TextContrast);
        var validDerived = Meets(resolvedAccent, surfaces, IndicatorContrast) && Meets(error, surfaces, TextContrast) &&
            Meets(warning, surfaces, TextContrast) && Contrast(resolvedAccent, onAccent) >= TextContrast;

        if (!validDerived)
        {
            return null;
        }

        return new(window.Value, panel.Value, secondary, text.Value, muted.Value, border.Value,
            resolvedAccent, hover, pressed, onAccent, selection, resolvedAccent, error, warning);
    }

    /// <summary>Retains default colors when a field has no override.</summary>
    private static Color? Read(string? value, string fallback) => TryColor(value ?? fallback, out var color) ? color : null;

    /// <summary>Finds the closest monotone shade satisfying the actual surface pairs.</summary>
    private static Color Adjust(Color color, Color[] surfaces, double minimum)
    {
        var endpoint = surfaces.Min(surface => Contrast(Colors.White, surface)) >= surfaces.Min(surface => Contrast(Colors.Black, surface))
            ? Colors.White : Colors.Black;

        for (var step = 0; step <= AdjustmentSteps; step++)
        {
            var candidate = Blend(color, endpoint, (double)step / AdjustmentSteps);

            if (Meets(candidate, surfaces, minimum))
            {
                return candidate;
            }
        }

        return endpoint;
    }

    /// <summary>Derives visible hover/pressed shades while retaining both indicator and label contrast.</summary>
    private static Color AccentState(Color accent, Color foreground, Color[] surfaces, double amount)
    {
        var endpoint = foreground == Colors.White ? Colors.Black : Colors.White;

        for (var step = AdjustmentSteps; step >= 0; step--)
        {
            var candidate = Blend(accent, endpoint, amount * step / AdjustmentSteps);

            if (Meets(candidate, surfaces, IndicatorContrast) && Contrast(candidate, foreground) >= TextContrast)
            {
                return candidate;
            }
        }

        return accent;
    }

    /// <summary>Checks every admitted surface rather than a representative single background.</summary>
    private static bool Meets(Color foreground, IEnumerable<Color> backgrounds, double minimum) =>
        backgrounds.All(background => Contrast(foreground, background) >= minimum);

    /// <summary>Blends opaque channels only; opacity must not hide an unchecked contrast pair.</summary>
    private static Color Blend(Color first, Color second, double amount) => Color.FromRgb(
        (byte)Math.Round(first.R + (second.R - first.R) * amount),
        (byte)Math.Round(first.G + (second.G - first.G) * amount),
        (byte)Math.Round(first.B + (second.B - first.B) * amount));

    /// <summary>Applies the normative linearized-sRGB relative luminance weights.</summary>
    private static double Luminance(Color color) => RedLuminanceWeight * Linearize(color.R) + GreenLuminanceWeight * Linearize(color.G) + BlueLuminanceWeight * Linearize(color.B);

    /// <summary>Converts an encoded sRGB channel into its linear light value.</summary>
    private static double Linearize(byte channel)
    {
        var value = (double)channel / ColorMaximum;
        return value <= SrgbLinearBoundary ? value / SrgbLinearDivisor : Math.Pow((value + SrgbOffset) / SrgbDivisor, SrgbExponent);
    }
}
