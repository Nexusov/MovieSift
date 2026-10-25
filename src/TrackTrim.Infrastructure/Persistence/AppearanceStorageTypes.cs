using System.Text.Json.Serialization;
using TrackTrim.Application.Appearance;

namespace TrackTrim.Infrastructure.Persistence;

internal sealed record AppearanceDocument(
    [property: JsonRequired] int Version,
    [property: JsonRequired] AppearanceTheme Theme,
    [property: JsonRequired] string AccentHex,
    [property: JsonRequired] AppearancePaletteOverrides? LightOverrides,
    [property: JsonRequired] AppearancePaletteOverrides? DarkOverrides,
    [property: JsonRequired] AppearanceMotion Motion);

internal sealed record AppearanceFileActions(Action<string, string> Publish, Action<string> DeleteTemporary);
