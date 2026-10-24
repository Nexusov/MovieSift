using Avalonia.Media;

namespace TrackTrim.Desktop.Appearance;

/// <summary>Contains only resolved Desktop colors; portable settings remain free of Avalonia types.</summary>
public sealed record AppearancePalette(
    Color Window, Color Panel, Color Secondary, Color Text, Color Muted, Color Border,
    Color Accent, Color AccentHover, Color AccentPressed, Color OnAccent, Color Selection, Color Focus,
    Color Error, Color Warning);

/// <summary>Reports an unusable edit without replacing the last valid palette.</summary>
public sealed record AppearancePaletteResult(AppearancePalette? Light, AppearancePalette? Dark, string? ErrorKey = null)
{
    public bool IsValid => Light is not null && Dark is not null && ErrorKey is null;
}
